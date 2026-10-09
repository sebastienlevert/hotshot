namespace Hotshot.Recording;

/// <summary>
/// Clock-driven audio mixer: every ~20 ms it writes exactly as many 48 kHz frames as the recording clock says should
/// exist, pulling each source (silence on underrun or mute). Audio stays continuous and locked to the video timeline.
/// </summary>
internal sealed class AudioPump
{
    private const int Rate = MediaWriter.AudioSampleRate;
    private const int Channels = MediaWriter.AudioChannels;
    private const int ChunkFrames = Rate / 10;            // write at most 100 ms per sample
    private const int MaxLatencyFrames = Rate * 150 / 1000;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(20);

    private readonly MediaWriter _writer;
    private readonly RecordingClock _clock;
    private readonly IReadOnlyList<AudioSource> _sources;
    private readonly Action<Exception> _onError;
    private readonly float[] _mix = new float[ChunkFrames * Channels];
    private readonly float[] _scratch = new float[ChunkFrames * Channels];
    private readonly short[] _pcm = new short[ChunkFrames * Channels];
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private readonly object _pumpLock = new();
    private Thread? _thread;
    private long _framesWritten;
    private bool _faulted;

    public AudioPump(MediaWriter writer, RecordingClock clock, IReadOnlyList<AudioSource> sources, Action<Exception> onError)
    {
        _writer = writer;
        _clock = clock;
        _sources = sources;
        _onError = onError;
    }

    public void Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Hotshot audio pump",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Stops the pump thread, then writes the remainder up to the (frozen) clock so audio ends with the video.</summary>
    public void Stop()
    {
        if (_stopSignal.IsSet)
        {
            return;
        }

        _stopSignal.Set();
        _thread?.Join();
        _thread = null;
        try
        {
            WriteUpTo(_clock.ElapsedHns);
        }
        catch
        {
            // The pump already reported the failure, or the writer is gone; nothing to add at shutdown.
        }
    }

    private void Run()
    {
        while (!_stopSignal.Wait(TickInterval))
        {
            try
            {
                WriteUpTo(_clock.ElapsedHns);
            }
            catch (Exception ex)
            {
                _faulted = true;
                _onError(ex);
                return;
            }
        }
    }

    private void WriteUpTo(long clockHns)
    {
        lock (_pumpLock)
        {
            if (_faulted)
            {
                return;
            }

            long target = clockHns * Rate / TimeSpan.TicksPerSecond;
            while (_framesWritten < target)
            {
                int frames = (int)Math.Min(ChunkFrames, target - _framesWritten);
                int samples = frames * Channels;
                var mix = _mix.AsSpan(0, samples);
                mix.Clear();
                var scratch = _scratch.AsSpan(0, samples);
                foreach (var source in _sources)
                {
                    source.Read(scratch, MaxLatencyFrames);
                    for (int i = 0; i < samples; i++)
                    {
                        mix[i] += scratch[i];
                    }
                }

                var pcm = _pcm.AsSpan(0, samples);
                for (int i = 0; i < samples; i++)
                {
                    pcm[i] = (short)(Math.Clamp(mix[i], -1f, 1f) * short.MaxValue);
                }

                long start = _framesWritten * TimeSpan.TicksPerSecond / Rate;
                long end = (_framesWritten + frames) * TimeSpan.TicksPerSecond / Rate;
                _writer.WriteAudio(pcm, start, end - start);
                _framesWritten += frames;
            }
        }
    }
}
