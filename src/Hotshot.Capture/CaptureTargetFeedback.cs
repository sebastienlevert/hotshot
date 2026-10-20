using Hotshot.Interop;

namespace Hotshot.Capture;

internal readonly record struct CaptureTargetFeedback(bool WindowTarget, bool Magnifier, bool Crosshair, string Label, string Hint)
{
    public static CaptureTargetFeedback Create(bool dragging, WindowInfo? window, PixelRect selection, bool magnifier, bool crosshair)
    {
        var windowTarget = !dragging && window is not null;
        var size = $"{selection.Width} × {selection.Height}";
        var label = dragging ? $"Area  ·  {size}" : windowTarget ? $"Window  ·  {size}" : $"Screen  ·  {size}";
        if (windowTarget && window is { Title.Length: > 0 })
        {
            var title = window.Title.Length > 48 ? window.Title[..47] + "…" : window.Title;
            label = $"Window: {title}  ·  {size}";
        }
        var hint = windowTarget
            ? "Drag to select an area  ·  Click for window  ·  Space = screen  ·  Esc = cancel"
            : "Drag to select an area  ·  Space = screen  ·  Esc = cancel";
        return new(windowTarget, magnifier, crosshair, label, hint);
    }
}
