using Vortice.MediaFoundation;

namespace Hotshot.Gif.Internal;

/// <summary>Process-wide, ref-counted MFStartup/MFShutdown pairing that is safe to use concurrently.</summary>
internal static class MediaFoundationRuntime
{
    private static readonly Lock s_lock = new();
    private static int s_refCount;

    public static Scope Enter()
    {
        lock (s_lock)
        {
            if (s_refCount == 0)
            {
                MediaFactory.MFStartup().CheckError();
            }

            s_refCount++;
        }

        return new Scope();
    }

    private static void Release()
    {
        lock (s_lock)
        {
            if (s_refCount == 0)
            {
                return;
            }

            if (--s_refCount == 0)
            {
                MediaFactory.MFShutdown();
            }
        }
    }

    internal sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Release();
            }
        }
    }
}

/// <summary>Ensures the current thread is in the COM multithreaded apartment (Media Foundation requirement).</summary>
internal readonly struct ComApartmentScope : IDisposable
{
    private readonly bool _uninitialize;

    private ComApartmentScope(bool uninitialize) => _uninitialize = uninitialize;

    public static ComApartmentScope EnterMta()
    {
        int hr = NativeMethods.CoInitializeEx(0, NativeMethods.COINIT_MULTITHREADED);
        // RPC_E_CHANGED_MODE (STA thread) is tolerated: the MF objects used here are free-threaded.
        return new ComApartmentScope(hr is NativeMethods.S_OK or NativeMethods.S_FALSE);
    }

    public void Dispose()
    {
        if (_uninitialize)
        {
            NativeMethods.CoUninitialize();
        }
    }
}
