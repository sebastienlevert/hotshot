using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// WasapiCapture/WasapiLoopbackCapture are obsolete in NAudio 3 in favour of WasapiRecorder, but remain the simple
// event-driven API for shared-mode microphone + system loopback capture.
#pragma warning disable CS0618

namespace Hotshot.Recording;

/// <summary>
/// One WASAPI source (loopback or microphone) normalized to 48 kHz stereo float and buffered for the audio pump.
/// Reads never block: an empty buffer yields silence.
/// </summary>
internal sealed class AudioSource : IDisposable
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly WaveFormat OutputFormat = WaveFormat.CreateIeeeFloatWaveFormat(MediaWriter.AudioSampleRate, MediaWriter.AudioChannels);
    private const int OutputBlockAlign = MediaWriter.AudioChannels * sizeof(float);

    private readonly object _sync = new();
    private readonly string _name;
    private readonly Action<string> _warn;
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly SampleKind _kind;
    private readonly int _channels;
    private readonly int _blockAlign;
    private readonly BufferedWaveProvider _output;
    private readonly BufferedWaveProvider? _resampleInput;
    private readonly ISampleProvider? _resampler;
    private float[] _convertBuffer = new float[4096];
    private readonly float[] _resampleBuffer = new float[4096];
    private readonly byte[] _discardBuffer = new byte[16384];
    private volatile bool _accepting;
    private volatile bool _faulted;
    private bool _started;
    private bool _disposed;

    private enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

    private AudioSource(string name, MMDevice device, WasapiCapture capture, Action<string> warn)
    {
        _name = name;
        _device = device;
        _capture = capture;
        _warn = warn;

        var format = capture.WaveFormat;
        _kind = GetSampleKind(format);
        _channels = format.Channels;
        _blockAlign = format.BlockAlign;
        if (_channels < 1 || _blockAlign < 1)
        {
            throw new NotSupportedException($"Unsupported {name} format: {format}.");
        }

        _output = new BufferedWaveProvider(OutputFormat, TimeSpan.FromSeconds(10))
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
        };

        if (format.SampleRate != MediaWriter.AudioSampleRate)
        {
            _resampleInput = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, MediaWriter.AudioChannels), TimeSpan.FromSeconds(2))
            {
                ReadFully = false,
                DiscardOnBufferOverflow = true,
            };
            _resampler = new WdlResamplingSampleProvider(_resampleInput.ToSampleProvider(), MediaWriter.AudioSampleRate);
        }

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public string Name => _name;

    public bool Muted { get; set; }

    /// <summary>Opens the default render endpoint in loopback mode. Returns null (with a warning) if unavailable.</summary>
    public static AudioSource? TryCreateSystemAudio(Action<string> warn)
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return Create("system audio", device, new WasapiLoopbackCapture(device), warn);
        }
        catch (Exception ex)
        {
            device?.Dispose();
            warn($"System audio unavailable, recording without it: {ex.Message}");
            return null;
        }
    }

    /// <summary>Opens the given (or default) capture endpoint. Returns null (with a warning) if no usable microphone exists.</summary>
    public static AudioSource? TryCreateMicrophone(string? deviceId, Action<string> warn)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(deviceId))
        {
            MMDevice? device = null;
            try
            {
                device = enumerator.GetDevice(deviceId);
                if (device.State != DeviceState.Active)
                {
                    throw new InvalidOperationException($"device state is {device.State}");
                }

                return Create("microphone", device, new WasapiCapture(device, true), warn);
            }
            catch (Exception ex)
            {
                device?.Dispose();
                warn($"Microphone '{deviceId}' unavailable ({ex.Message}); trying the default microphone.");
            }
        }

        MMDevice? fallback = null;
        try
        {
            fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return Create("microphone", fallback, new WasapiCapture(fallback, true), warn);
        }
        catch (Exception ex)
        {
            fallback?.Dispose();
            warn($"No usable microphone, recording without it: {ex.Message}");
            return null;
        }
    }

    private static AudioSource Create(string name, MMDevice device, WasapiCapture capture, Action<string> warn)
    {
        try
        {
            return new AudioSource(name, device, capture, warn);
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    public void Start()
    {
        _capture.StartRecording();
        _started = true;
    }

    /// <summary>Starts or stops accepting captured data. Resuming discards anything buffered so paused audio never leaks in.</summary>
    public void SetAccepting(bool accepting)
    {
        lock (_sync)
        {
            if (accepting)
            {
                _output.ClearBuffer();
                _resampleInput?.ClearBuffer();
            }

            _accepting = accepting;
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> (interleaved stereo) with buffered audio, zero-filling underruns.
    /// Backlog beyond <paramref name="maxLatencyFrames"/> is dropped to stay in sync with the recording clock.
    /// </summary>
    public void Read(Span<float> destination, int maxLatencyFrames)
    {
        int wantedBytes = destination.Length * sizeof(float);
        int excess = _output.BufferedBytes - wantedBytes - (maxLatencyFrames * OutputBlockAlign);
        excess -= excess % OutputBlockAlign;
        while (excess > 0)
        {
            int chunk = Math.Min(excess, _discardBuffer.Length);
            int read = _output.Read(_discardBuffer.AsSpan(0, chunk));
            if (read <= 0)
            {
                break;
            }

            excess -= read;
        }

        _output.Read(MemoryMarshal.AsBytes(destination));
        if (Muted || _faulted)
        {
            destination.Clear();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_accepting || e.BytesRecorded <= 0)
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                if (!_accepting)
                {
                    return;
                }

                int frames = e.BytesRecorded / _blockAlign;
                if (_convertBuffer.Length < frames * 2)
                {
                    _convertBuffer = new float[frames * 2];
                }

                var converted = _convertBuffer.AsSpan(0, frames * 2);
                ConvertToStereoFloat(e.Buffer.AsSpan(0, frames * _blockAlign), frames, converted);
                var bytes = MemoryMarshal.AsBytes(converted);
                if (_resampler is null)
                {
                    _output.AddSamples(bytes);
                    return;
                }

                _resampleInput!.AddSamples(bytes);
                // Bounded drain: the WDL resampler returns 0 once its input is exhausted.
                for (int guard = 0; guard < 256; guard++)
                {
                    int read = _resampler.Read(_resampleBuffer);
                    if (read <= 0)
                    {
                        break;
                    }

                    _output.AddSamples(MemoryMarshal.AsBytes(_resampleBuffer.AsSpan(0, read)));
                }
            }
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    private void ConvertToStereoFloat(ReadOnlySpan<byte> input, int frames, Span<float> output)
    {
        int bytesPerSample = _blockAlign / _channels;
        for (int frame = 0; frame < frames; frame++)
        {
            var block = input.Slice(frame * _blockAlign, _blockAlign);
            float left = ReadSample(block, 0);
            float right = _channels > 1 ? ReadSample(block.Slice(bytesPerSample), 0) : left;
            output[frame * 2] = left;
            output[(frame * 2) + 1] = right;
        }
    }

    private float ReadSample(ReadOnlySpan<byte> data, int offset) => _kind switch
    {
        SampleKind.Float32 => MemoryMarshal.Read<float>(data.Slice(offset)),
        SampleKind.Pcm16 => MemoryMarshal.Read<short>(data.Slice(offset)) / 32768f,
        SampleKind.Pcm24 => ((data[offset] | (data[offset + 1] << 8) | ((sbyte)data[offset + 2] << 16)) / 8388608f),
        _ => MemoryMarshal.Read<int>(data.Slice(offset)) / 2147483648f,
    };

    private static SampleKind GetSampleKind(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm ||
            (format is WaveFormatExtensible ext2 && ext2.SubFormat == PcmSubFormat);

        return (isFloat, isPcm, format.BitsPerSample) switch
        {
            (true, _, 32) => SampleKind.Float32,
            (_, true, 16) => SampleKind.Pcm16,
            (_, true, 24) => SampleKind.Pcm24,
            (_, true, 32) => SampleKind.Pcm32,
            _ => throw new NotSupportedException($"Unsupported capture format: {format}."),
        };
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Fault(e.Exception);
        }
    }

    private void Fault(Exception ex)
    {
        if (_faulted || _disposed)
        {
            return;
        }

        _faulted = true;
        _warn($"Capture of {_name} stopped ({ex.Message}); continuing with silence for it.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _accepting = false;
        try
        {
            if (_started)
            {
                _capture.StopRecording();
            }
        }
        catch
        {
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try
        {
            // Joins the capture thread.
            _capture.Dispose();
        }
        catch
        {
        }

        _device.Dispose();
    }
}
