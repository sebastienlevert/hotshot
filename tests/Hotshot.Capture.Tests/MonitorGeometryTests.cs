using Hotshot.Core.Settings;

namespace Hotshot.Capture.Tests;

public sealed class MonitorGeometryTests
{
    [Fact]
    public void SpanningWindowUsesGreatestOverlapRatherThanCenter()
    {
        MonitorInfo[] monitors =
        [
            Monitor(1, new PixelRect(0, 0, 1920, 1080), 1, true),
            Monitor(2, new PixelRect(1920, 0, 800, 2160), 2),
        ];
        var window = new PixelRect(1700, 0, 400, 1800);
        Assert.Equal(2, MonitorGeometry.Select(monitors, window).Index);
    }

    [Theory]
    [InlineData(1, 1.25, 1000, 750)]
    [InlineData(1, 1.5, 1200, 900)]
    [InlineData(1, 2, 1600, 1200)]
    [InlineData(2, 1, 400, 300)]
    [InlineData(1.5, 2.5, 1333, 1000)]
    public void RestoringBetweenScalesPreservesLogicalSize(double oldScale, double newScale, int width, int height)
    {
        MonitorInfo[] monitors = [Monitor(1, new PixelRect(-3840, -500, 3840, 2160), newScale, true)];
        var restored = MonitorGeometry.Restore(new WindowLayout(-3000, -200, 800, 600, oldScale), monitors);
        Assert.Equal(width, restored.Width);
        Assert.Equal(height, restored.Height);
        Assert.Equal(-3000, restored.X);
        Assert.Equal(-200, restored.Y);
    }

    [Fact]
    public void DisconnectedMonitorRestoresOnNearestRemainingDisplay()
    {
        MonitorInfo[] monitors =
        [
            Monitor(1, new PixelRect(-1920, 0, 1920, 1080), 1),
            Monitor(2, new PixelRect(0, 0, 2560, 1440), 2, true),
        ];
        var restored = MonitorGeometry.Restore(new WindowLayout(4000, 200, 1000, 700, 1), monitors);
        Assert.Equal(new PixelRect(560, 40, 2000, 1400), restored);
    }

    [Fact]
    public void OversizedAndTinyScaleValuesClampBeforeIntegerConversion()
    {
        MonitorInfo[] monitors = [Monitor(1, new PixelRect(0, 0, 1920, 1080), 2, true)];
        var restored = MonitorGeometry.Restore(new WindowLayout(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, double.Epsilon), monitors);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), restored);
    }

    [Fact]
    public void InvalidScaleIsRejected()
    {
        MonitorInfo[] monitors = [Monitor(1, new PixelRect(0, 0, 1920, 1080), 1, true)];
        Assert.Throws<ArgumentException>(() => MonitorGeometry.Restore(new WindowLayout(0, 0, 800, 600, double.NaN), monitors));
    }

    private static MonitorInfo Monitor(int index, PixelRect bounds, double scale, bool primary = false) =>
        new(index, bounds, bounds, primary, index, "Synthetic", scale);
}
