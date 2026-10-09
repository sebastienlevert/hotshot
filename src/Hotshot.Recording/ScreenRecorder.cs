using System.Collections.Concurrent;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace Hotshot.Recording;

/// <summary>
/// Records a monitor (optionally cropped) or a window to an H.264/AAC MP4 at a constant frame rate.
/// </summary>
/// <remarks>
/// <see cref="TargetClosed"/> and <see cref="Failed"/> are raised on background threads (at most once each);
/// marshal to the UI thread before touching UI. All other members are thread-safe.
/// </remarks>
public sealed class ScreenRecorder : IAsyncDisposable
{
    private const int MinFps = 15;
    private const int MaxFps = 60;
    private const long MinAutoBitrate = 2_000_000;
    private const long MaxAutoBitrate = 40_000_000;
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AccessRequestTimeout = TimeSpan.FromSeconds(2);

    // GPU-path viability per adapter LUID; probed once per process (a runtime GPU failure flips it to false).
    private static readonly ConcurrentDictionary<long, bool> s_gpuPathByAdapter = new();

    private readonly RecordingOptions _options;
    private readonly string _outputPath;
    private readonly int _fps;
    private readonly object _gpuLock = new();
    private readonly object _stateLock = new();
    private readonly object _warningsLock = new();
    private readonly List<string> _warnings = [];
    private readonly RecordingClock _clock = new();
    private readonly ManualResetEventSlim _runSignal = new(false);
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private FrameCapture? _capture;
    private MediaWriter? _writer;
    private readonly List<AudioSource> _audioSources = [];
    private AudioSource? _systemAudio;
    private AudioSource? _microphone;
    private AudioPump? _audioPump;
    private Thread? _videoThread;
    private long _adapterLuid;
    private bool _mfAcquired;
    private bool _paused;
    private bool _microphoneMuted;
    private bool _systemAudioMuted;
    private volatile bool _stopping;
    private Task<RecordingResult>? _stopTask;
    private int _targetClosedRaised;
    private int _failedRaised;
    private long _framesDropped;

    private ScreenRecorder(RecordingOptions options)
    {
        _options = options;
        _outputPath = Path.GetFullPath(options.OutputPath);
        _fps = options.FramesPerSecond;
    }

    /// <summary>Raised once when the target window closes or the monitor disappears. The host should then call <see cref="StopAsync"/>.</summary>
    public event EventHandler? TargetClosed;

    /// <summary>Raised once on an unrecoverable error (device lost, encoder failure). The host should call <see cref="StopAsync"/> or <see cref="CancelAsync"/>.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>Recorded time, excluding paused intervals.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    public bool IsPaused
    {
        get { lock (_stateLock) return _paused; }
    }

    /// <summary>Output video width in pixels (even).</summary>
    public int Width { get; private set; }

    /// <summary>Output video height in pixels (even).</summary>
    public int Height { get; private set; }

    public VideoEncoderPath EncoderPath { get; private set; }

    public bool HasAudio => _writer?.HasAudio ?? false;

    /// <summary>Frames skipped because the encoder could not keep up (the previous frame is shown longer instead).</summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Non-fatal issues encountered so far (e.g. microphone unavailable, CPU encoder fallback).</summary>
    public IReadOnlyList<string> Warnings
    {
        get { lock (_warningsLock) return _warnings.ToArray(); }
    }

    /// <summary>Muting replaces the microphone with silence; capture keeps running so unmuting is instant.</summary>
    public bool MicrophoneMuted
    {
        get => _microphoneMuted;
        set
        {
            _microphoneMuted = value;
            if (_microphone is not null)
            {
                _microphone.Muted = value;
            }
        }
    }

    public bool SystemAudioMuted
    {
        get => _systemAudioMuted;
        set
        {
            _systemAudioMuted = value;
            if (_systemAudio is not null)
            {
                _systemAudio.Muted = value;
            }
        }
    }

    /// <summary>Starts recording. Initialization runs on a background (MTA) thread; safe to call from the UI thread.</summary>
    public static Task<ScreenRecorder> StartAsync(RecordingOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        return Task.Run(() => StartCoreAsync(options, ct), ct);
    }

    private static void Validate(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Target);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.FramesPerSecond, MinFps, nameof(options.FramesPerSecond));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.FramesPerSecond, MaxFps, nameof(options.FramesPerSecond));
        if (options.VideoBitrate is { } bitrate)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(bitrate, 100_000, nameof(options.VideoBitrate));
        }

        switch (options.Target)
        {
            case MonitorTarget { MonitorHandle: 0 }:
                throw new ArgumentException("Monitor handle is null.", nameof(options));
            case MonitorTarget { Crop: { } crop } when crop.Width < 2 || crop.Height < 2:
                throw new ArgumentException($"Crop {crop} is too small.", nameof(options));
            case WindowTarget window when window.WindowHandle == 0 || !Native.IsWindow(window.WindowHandle):
                throw new ArgumentException("Window handle is not a valid window.", nameof(options));
            case MonitorTarget or WindowTarget:
                break;
            default:
                throw new ArgumentException($"Unsupported target {options.Target.GetType().Name}.", nameof(options));
        }
    }

    private static async Task<ScreenRecorder> StartCoreAsync(RecordingOptions options, CancellationToken ct)
    {
        if (!RecordingSupport.IsSupported)
        {
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        }

        var recorder = new ScreenRecorder(options);
        try
        {
            await recorder.InitializeAsync(ct).ConfigureAwait(false);
            return recorder;
        }
        catch
        {
            recorder.Teardown();
            MediaWriter.TryDelete(recorder._outputPath);
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        MediaFoundationRuntime.Acquire();
        _mfAcquired = true;

        _device = CaptureInterop.CreateD3DDevice();
        _context = _device.ImmediateContext;
        _winrtDevice = CaptureInterop.CreateDirect3DDevice(_device);
        _adapterLuid = CaptureInterop.GetAdapterLuid(_device);

        RectInt? crop = null;
        switch (_options.Target)
        {
            case MonitorTarget monitor:
                _item = CaptureInterop.CreateItemForMonitor(monitor.MonitorHandle);
                crop = monitor.Crop;
                break;
            case WindowTarget window:
                _item = CaptureInterop.CreateItemForWindow(window.WindowHandle);
                break;
        }

        ct.ThrowIfCancellationRequested();
        await RequestBorderlessAccessAsync(ct).ConfigureAwait(false);

        _capture = new FrameCapture(_device, _context, _gpuLock, _winrtDevice, _item!, crop);
        Width = _capture.Width;
        Height = _capture.Height;
        _capture.Closed += RaiseTargetClosed;
        _capture.Error += ex => RaiseFailed(new InvalidOperationException("Frame capture failed.", ex));

        bool withAudio = _options.CaptureSystemAudio || _options.CaptureMicrophone;
        _writer = CreateWriter(withAudio);
        EncoderPath = _writer.EncoderPath;
        Log($"Recording {Width}x{Height} @ {_fps} fps, {EncoderPath} encoder, audio: {withAudio}.");
        ct.ThrowIfCancellationRequested();

        if (_options.CaptureSystemAudio)
        {
            _systemAudio = AudioSource.TryCreateSystemAudio(AddWarning);
            if (_systemAudio is not null)
            {
                _systemAudio.Muted = _systemAudioMuted;
                _audioSources.Add(_systemAudio);
            }
        }

        if (_options.CaptureMicrophone)
        {
            _microphone = AudioSource.TryCreateMicrophone(_options.MicrophoneDeviceId, AddWarning);
            if (_microphone is not null)
            {
                _microphone.Muted = _microphoneMuted;
                _audioSources.Add(_microphone);
            }
        }

        _capture.Start(_options.CaptureCursor);
        try
        {
            await _capture.FirstFrame.WaitAsync(FirstFrameTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AddWarning("No frame arrived within 2 s; recording starts with black frames until content updates.");
        }

        foreach (var source in _audioSources.ToArray())
        {
            try
            {
                source.Start();
            }
            catch (Exception ex)
            {
                AddWarning($"Could not start {source.Name} capture, recording without it: {ex.Message}");
                _audioSources.Remove(source);
                if (source == _systemAudio)
                {
                    _systemAudio = null;
                }
                else
                {
                    _microphone = null;
                }

                source.Dispose();
            }
        }

        ct.ThrowIfCancellationRequested();

        if (withAudio)
        {
            _audioPump = new AudioPump(_writer, _clock, _audioSources, ex => RaiseFailed(new InvalidOperationException("Audio encoding failed.", ex)));
        }

        lock (_stateLock)
        {
            foreach (var source in _audioSources)
            {
                source.SetAccepting(true);
            }

            _clock.Start();
            _runSignal.Set();
        }

        _videoThread = new Thread(VideoLoop)
        {
            IsBackground = true,
            Name = "Hotshot video pump",
            Priority = ThreadPriority.AboveNormal,
        };
        _videoThread.Start();
        _audioPump?.Start();
    }

    private static async Task RequestBorderlessAccessAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) ||
            !ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
        {
            return;
        }

        try
        {
            await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                .AsTask(ct)
                .WaitAsync(AccessRequestTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Not granted / not supported (e.g. unpackaged on older builds): the capture border stays visible.
        }
    }

    private MediaWriter CreateWriter(bool withAudio)
    {
        long bitrate = _options.VideoBitrate ?? Math.Clamp((long)(Width * (double)Height * _fps * 0.1), MinAutoBitrate, MaxAutoBitrate);
        bool gpuViable = false;
        if (_options.PreferHardwareEncoder && !s_gpuPathByAdapter.TryGetValue(_adapterLuid, out gpuViable))
        {
            gpuViable = MediaWriter.ProbeGpuPath(_device!, _context!, _gpuLock, _capture!.Latest, Width, Height, _fps, AddWarning);
            s_gpuPathByAdapter[_adapterLuid] = gpuViable;
        }

        if (gpuViable)
        {
            try
            {
                return MediaWriter.Create(_outputPath, _device!, _context!, Width, Height, _fps, (int)bitrate, withAudio, VideoEncoderPath.Gpu);
            }
            catch (Exception ex)
            {
                AddWarning($"GPU encoder setup failed, falling back to CPU encoding: {ex.Message}");
            }
        }

        return MediaWriter.Create(_outputPath, _device!, _context!, Width, Height, _fps, (int)bitrate, withAudio, VideoEncoderPath.Cpu);
    }

    private void VideoLoop()
    {
        var writer = _writer!;
        var capture = _capture!;
        long nextIndex = 0;
        long lastDeviceCheck = Environment.TickCount64;
        try
        {
            using var timer = new PrecisionTimer();
            while (!_stopping)
            {
                if (_options.Target is WindowTarget window && !Native.IsWindow(window.WindowHandle))
                {
                    RaiseTargetClosed();
                    break;
                }

                if (!_runSignal.IsSet)
                {
                    _runSignal.Wait(250);
                    continue;
                }

                long due = FrameIndexAt(_clock.ElapsedHns);
                if (due >= nextIndex)
                {
                    // When late, jump to the current slot: the previous frame simply stays on screen longer.
                    Interlocked.Add(ref _framesDropped, due - nextIndex);
                    WriteFrame(writer, capture, due);
                    nextIndex = due + 1;
                }

                if (Environment.TickCount64 - lastDeviceCheck > 1000)
                {
                    lastDeviceCheck = Environment.TickCount64;
                    var reason = _device!.DeviceRemovedReason;
                    if (reason.Failure)
                    {
                        throw new InvalidOperationException($"The graphics device was removed (0x{reason.Code:X8}).");
                    }
                }

                long wait = FrameTime(nextIndex) - _clock.ElapsedHns;
                if (wait > 0)
                {
                    timer.Wait(wait);
                }
            }
        }
        catch (Exception ex)
        {
            if (EncoderPath == VideoEncoderPath.Gpu)
            {
                s_gpuPathByAdapter[_adapterLuid] = false;
            }

            RaiseFailed(ex);
        }
    }

    private void WriteFrame(MediaWriter writer, FrameCapture capture, long index)
    {
        IMFSample? sample;
        lock (_gpuLock)
        {
            sample = writer.PrepareVideoSample(capture.Latest);
        }

        if (sample is null)
        {
            Interlocked.Increment(ref _framesDropped);
            return;
        }

        using (sample)
        {
            long start = FrameTime(index);
            writer.WriteVideo(sample, start, FrameTime(index + 1) - start);
        }
    }

    private long FrameTime(long index) => index * TimeSpan.TicksPerSecond / _fps;

    private long FrameIndexAt(long hns) => hns * _fps / TimeSpan.TicksPerSecond;

    /// <summary>Pauses recording; the clock and both pumps stop and audio captured while paused is dropped.</summary>
    public void Pause()
    {
        lock (_stateLock)
        {
            if (_paused || _stopTask is not null)
            {
                return;
            }

            _paused = true;
            _runSignal.Reset();
            _clock.Pause();
            foreach (var source in _audioSources)
            {
                source.SetAccepting(false);
            }
        }
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            if (!_paused || _stopTask is not null)
            {
                return;
            }

            _paused = false;
            foreach (var source in _audioSources)
            {
                source.SetAccepting(true);
            }

            _clock.Start();
            _runSignal.Set();
        }
    }

    /// <summary>Stops recording and finalizes the MP4. Idempotent; safe after <see cref="Failed"/> or <see cref="TargetClosed"/>.</summary>
    /// <exception cref="InvalidOperationException">The file could not be finalized.</exception>
    public Task<RecordingResult> StopAsync()
    {
        lock (_stateLock)
        {
            return _stopTask ??= Task.Run(StopCore);
        }
    }

    /// <summary>Stops recording and deletes the output file.</summary>
    public async Task CancelAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // The file is being discarded; finalization errors are irrelevant.
        }

        await Task.Run(() => MediaWriter.TryDelete(_outputPath)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private RecordingResult StopCore()
    {
        _clock.Pause();
        var duration = _clock.Elapsed;
        _stopping = true;
        _runSignal.Set();
        _videoThread?.Join();

        Exception? finalizeError = null;
        try
        {
            _audioPump?.Stop();
            if (_writer is { VideoFramesWritten: 0 } writer && _capture is not null)
            {
                // Guarantee at least one video frame so the file is playable.
                WriteFrame(writer, _capture, 0);
            }

            _capture?.Stop();
            foreach (var source in _audioSources)
            {
                source.Dispose();
            }

            _writer?.FinalizeFile();
        }
        catch (Exception ex)
        {
            finalizeError = ex;
        }

        bool hasAudio = _writer?.HasAudio ?? false;
        Teardown();
        if (finalizeError is not null)
        {
            throw new InvalidOperationException($"Failed to finalize '{_outputPath}'.", finalizeError);
        }

        var file = new FileInfo(_outputPath);
        return new RecordingResult(_outputPath, duration, Width, Height, file.Exists ? file.Length : 0, hasAudio);
    }

    /// <summary>Releases every native resource. Safe on partially initialized instances.</summary>
    private void Teardown()
    {
        _stopping = true;
        _runSignal.Set();
        foreach (var source in _audioSources)
        {
            source.Dispose();
        }

        _audioSources.Clear();
        _systemAudio = null;
        _microphone = null;
        _capture?.Dispose();
        _capture = null;
        _writer?.Dispose();
        _writer = null;
        _item = null;
        (_winrtDevice as IDisposable)?.Dispose();
        _winrtDevice = null;
        if (_context is not null)
        {
            lock (_gpuLock)
            {
                _context.ClearState();
                _context.Flush();
            }

            _context.Dispose();
            _context = null;
        }

        _device?.Dispose();
        _device = null;
        if (_mfAcquired)
        {
            _mfAcquired = false;
            MediaFoundationRuntime.Release();
        }
    }

    private void RaiseTargetClosed()
    {
        if (_stopping || Interlocked.Exchange(ref _targetClosedRaised, 1) != 0)
        {
            return;
        }

        Log("Capture target closed.");
        ThreadPool.QueueUserWorkItem(_ => TargetClosed?.Invoke(this, EventArgs.Empty));
    }

    private void RaiseFailed(Exception ex)
    {
        if (_stopping || Interlocked.Exchange(ref _failedRaised, 1) != 0)
        {
            return;
        }

        Log($"Recording failed: {ex}");
        ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke(this, ex));
    }

    private void AddWarning(string message)
    {
        lock (_warningsLock)
        {
            _warnings.Add(message);
        }

        Log(message);
    }

    private void Log(string message)
    {
        try
        {
            _options.Log?.Invoke(message);
        }
        catch
        {
        }
    }
}
