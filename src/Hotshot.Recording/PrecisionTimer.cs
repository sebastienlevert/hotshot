namespace Hotshot.Recording;

/// <summary>High-resolution waitable timer (falls back to a regular waitable timer, then to Thread.Sleep).</summary>
internal sealed class PrecisionTimer : IDisposable
{
    private nint _handle;

    public PrecisionTimer()
    {
        _handle = Native.CreateWaitableTimerEx(0, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (_handle == 0)
        {
            _handle = Native.CreateWaitableTimerEx(0, null, 0, Native.TIMER_ALL_ACCESS);
        }
    }

    /// <summary>Blocks for approximately <paramref name="hns"/> 100-ns units.</summary>
    public void Wait(long hns)
    {
        hns = Math.Clamp(hns, 1, 10_000_000);
        if (_handle != 0)
        {
            long dueTime = -hns; // negative = relative
            if (Native.SetWaitableTimer(_handle, in dueTime, 0, 0, 0, false))
            {
                Native.WaitForSingleObject(_handle, 1000);
                return;
            }
        }

        Thread.Sleep(TimeSpan.FromTicks(Math.Max(hns, TimeSpan.TicksPerMillisecond)));
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            Native.CloseHandle(_handle);
            _handle = 0;
        }
    }
}
