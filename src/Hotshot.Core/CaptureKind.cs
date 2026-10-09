namespace Hotshot.Core;

/// <summary>What produced a capture. Drives the {type} naming token and history badges.</summary>
public enum CaptureKind
{
    Region,
    Window,
    Monitor,
    AllMonitors,
    Recording,
    Gif,
}

public static class CaptureKindExtensions
{
    public static string TokenValue(this CaptureKind kind) => kind switch
    {
        CaptureKind.Region => "region",
        CaptureKind.Window => "window",
        CaptureKind.Monitor => "monitor",
        CaptureKind.AllMonitors => "screen",
        CaptureKind.Recording => "recording",
        CaptureKind.Gif => "gif",
        _ => "capture",
    };

    public static string Extension(this CaptureKind kind) => kind switch
    {
        CaptureKind.Recording => ".mp4",
        CaptureKind.Gif => ".gif",
        _ => ".png",
    };

    public static bool IsVideo(this CaptureKind kind) => kind is CaptureKind.Recording or CaptureKind.Gif;

    public static string DisplayName(this CaptureKind kind) => kind switch
    {
        CaptureKind.Region => "Region",
        CaptureKind.Window => "Window",
        CaptureKind.Monitor => "Monitor",
        CaptureKind.AllMonitors => "All monitors",
        CaptureKind.Recording => "Recording",
        CaptureKind.Gif => "GIF",
        _ => "Capture",
    };
}
