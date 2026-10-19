using System.Globalization;

namespace Hotshot.Core.Settings;

public readonly record struct RecordingTimerBounds(int X, int Y, int Width, int Height);

public static class RecordingTimerLayout
{
    public static RecordingTimerBounds Place(int left, int top, int workWidth, int workHeight, double scale,
        RecordingTimerSize size, RecordingTimerPlacement placement)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workHeight, 1);
        if (!double.IsFinite(scale) || scale is < 0.5 or > 8) throw new ArgumentOutOfRangeException(nameof(scale));
        if (!Enum.IsDefined(size)) throw new ArgumentOutOfRangeException(nameof(size));
        if (!Enum.IsDefined(placement)) throw new ArgumentOutOfRangeException(nameof(placement));
        var width = Math.Min(workWidth, (int)Math.Round((size == RecordingTimerSize.Mini ? 96 : 140) * scale));
        var height = Math.Min(workHeight, (int)Math.Round((size == RecordingTimerSize.Mini ? 32 : 48) * scale));
        var gapX = Math.Min((int)Math.Round(12 * scale), (workWidth - width) / 2);
        var gapY = Math.Min((int)Math.Round(12 * scale), (workHeight - height) / 2);
        var right = placement is RecordingTimerPlacement.TopRight or RecordingTimerPlacement.BottomRight;
        var bottom = placement is RecordingTimerPlacement.BottomRight or RecordingTimerPlacement.BottomLeft;
        return new(left + (right ? workWidth - width - gapX : gapX),
            top + (bottom ? workHeight - height - gapY : gapY), width, height);
    }

    public static IReadOnlyList<int> Countdown(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, 10);
        return seconds == 0 ? [] : Enumerable.Range(0, seconds + 1).Reverse().ToArray();
    }

    public static string Elapsed(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        return string.Create(CultureInfo.InvariantCulture, $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
    }

    public static string PlacementName(RecordingTimerPlacement placement) => placement switch
    {
        RecordingTimerPlacement.TopRight => "Top right",
        RecordingTimerPlacement.TopLeft => "Top left",
        RecordingTimerPlacement.BottomRight => "Bottom right",
        RecordingTimerPlacement.BottomLeft => "Bottom left",
        _ => throw new ArgumentOutOfRangeException(nameof(placement)),
    };
}
