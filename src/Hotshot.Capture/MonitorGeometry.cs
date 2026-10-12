using Hotshot.Core.Settings;

namespace Hotshot.Capture;

internal static class MonitorGeometry
{
    internal static MonitorInfo Select(IReadOnlyList<MonitorInfo> monitors, PixelRect rectangle)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0) throw new InvalidOperationException("No active display is available.");
        return monitors.OrderByDescending(monitor => IntersectionArea(rectangle, monitor.Bounds))
            .ThenBy(monitor => DistanceSquared(rectangle, monitor.Bounds))
            .ThenByDescending(monitor => monitor.IsPrimary).First();
    }

    internal static PixelRect Restore(WindowLayout saved, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (saved.Width <= 0 || saved.Height <= 0 || !double.IsFinite(saved.Scale) || saved.Scale <= 0)
            throw new ArgumentException("The saved window dimensions or DPI scale are invalid.", nameof(saved));
        var monitor = Select(monitors, new PixelRect(saved.X, saved.Y, saved.Width, saved.Height));
        if (!double.IsFinite(monitor.Scale) || monitor.Scale <= 0 || monitor.WorkArea.IsEmpty)
            throw new InvalidOperationException("The active display has invalid work-area or DPI information.");
        var work = monitor.WorkArea;
        var width = Scale(saved.Width, saved.Scale, monitor.Scale, Math.Min(200, work.Width), work.Width);
        var height = Scale(saved.Height, saved.Scale, monitor.Scale, Math.Min(150, work.Height), work.Height);
        return new PixelRect(Math.Clamp(saved.X, work.Left, work.Right - width),
            Math.Clamp(saved.Y, work.Top, work.Bottom - height), width, height);
    }

    private static int Scale(int pixels, double original, double target, int minimum, int maximum) =>
        checked((int)Math.Round(Math.Clamp(pixels * (target / original), minimum, maximum), MidpointRounding.AwayFromZero));

    private static long IntersectionArea(PixelRect first, PixelRect second)
    {
        var width = Math.Max(0, Math.Min((long)first.X + first.Width, (long)second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0, Math.Min((long)first.Y + first.Height, (long)second.Y + second.Height) - Math.Max(first.Y, second.Y));
        return checked(width * height);
    }

    private static double DistanceSquared(PixelRect first, PixelRect second)
    {
        double x = Math.Max(0, Math.Max((long)second.X - ((long)first.X + first.Width),
            (long)first.X - ((long)second.X + second.Width)));
        double y = Math.Max(0, Math.Max((long)second.Y - ((long)first.Y + first.Height),
            (long)first.Y - ((long)second.Y + second.Height)));
        return x * x + y * y;
    }
}
