using System.Diagnostics;

namespace Hotshot.Recording;

/// <summary>QPC-based recording clock that excludes paused intervals. Shared by the video and audio pumps.</summary>
internal sealed class RecordingClock
{
    private readonly object _lock = new();
    private TimeSpan _accumulated;
    private long _segmentStart;
    private bool _running;

    public bool IsRunning
    {
        get { lock (_lock) return _running; }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
            {
                return _running ? _accumulated + Stopwatch.GetElapsedTime(_segmentStart) : _accumulated;
            }
        }
    }

    /// <summary>Elapsed time in 100 ns units (Media Foundation time base).</summary>
    public long ElapsedHns => Elapsed.Ticks;

    public void Start()
    {
        lock (_lock)
        {
            if (_running)
            {
                return;
            }

            _segmentStart = Stopwatch.GetTimestamp();
            _running = true;
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }

            _accumulated += Stopwatch.GetElapsedTime(_segmentStart);
            _running = false;
        }
    }
}
