namespace Hotshot.Capture;

public static class WindowSnapGeometry
{
    /// <summary>Finds the whole topmost window at a physical screen point.</summary>
    public static WindowInfo? AtPoint(IReadOnlyList<WindowInfo> windows, int x, int y) =>
        windows.FirstOrDefault(window => window.Bounds.Contains(x, y));

    public static bool IsDrag(int anchorX, int anchorY, int x, int y, double scale)
    {
        var distance = Math.Max(4, (int)Math.Ceiling(4 * scale));
        return Math.Abs((long)x - anchorX) >= distance || Math.Abs((long)y - anchorY) >= distance;
    }
}
