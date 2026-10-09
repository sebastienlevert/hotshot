using Windows.Graphics.Capture;

namespace Hotshot.Recording;

public static class RecordingSupport
{
    private static readonly Lazy<bool> s_isSupported = new(() =>
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    });

    /// <summary>True when Windows.Graphics.Capture is available on this machine.</summary>
    public static bool IsSupported => s_isSupported.Value;
}
