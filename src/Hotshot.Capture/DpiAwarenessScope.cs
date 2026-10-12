using System.ComponentModel;
using System.Runtime.InteropServices;
using Hotshot.Interop;

namespace Hotshot.Capture;

internal readonly struct DpiAwarenessScope : IDisposable
{
    private readonly nint _previous;

    public DpiAwarenessScope()
    {
        _previous = Native.SetThreadDpiAwarenessContext(-4);
        if (_previous == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Physical-pixel display coordinates are unavailable.");
    }

    public void Dispose()
    {
        if (_previous != 0 && Native.SetThreadDpiAwarenessContext(_previous) == 0)
            System.Diagnostics.Trace.TraceError("Could not restore the calling thread's DPI awareness.");
    }
}
