using Vortice.MediaFoundation;

namespace Hotshot.Recording;

/// <summary>Ref-counted MFStartup/MFShutdown so concurrent or back-to-back recordings share one MF session.</summary>
internal static class MediaFoundationRuntime
{
    private static readonly object s_lock = new();
    private static int s_refCount;

    public static void Acquire()
    {
        lock (s_lock)
        {
            if (s_refCount == 0)
            {
                MediaFactory.MFStartup(useLightVersion: false).CheckError();
            }

            s_refCount++;
        }
    }

    public static void Release()
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
}
