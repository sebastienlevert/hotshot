using Hotshot.Interop;

namespace Hotshot.Capture.Tests;

public sealed class CaptureTargetFeedbackTests
{
    private static readonly PixelRect WindowBounds = new(80, 100, 720, 480);
    private static readonly WindowInfo Window = new(1, WindowBounds, "Example app", "Window", 1);

    [Fact]
    public void WindowHoverKeepsRegionSelectionToolsAndDragFirstGuidance()
    {
        var feedback = CaptureTargetFeedback.Create(false, Window, WindowBounds, true, true);
        Assert.True(feedback.WindowTarget);
        Assert.True(feedback.Magnifier);
        Assert.True(feedback.Crosshair);
        Assert.Equal("Window: Example app  ·  720 × 480", feedback.Label);
        Assert.Equal("Drag to select an area  ·  Click for window  ·  Space = screen  ·  Esc = cancel", feedback.Hint);
    }

    [Fact]
    public void StartingADragSwitchesImmediatelyToAreaFeedback()
    {
        var area = new PixelRect(100, 120, 200, 140);
        var feedback = CaptureTargetFeedback.Create(true, Window, area, true, true);
        Assert.False(feedback.WindowTarget);
        Assert.True(feedback.Magnifier);
        Assert.True(feedback.Crosshair);
        Assert.Equal("Area  ·  200 × 140", feedback.Label);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void SelectionToolsRespectPreferencesOverWindowsAndWhileDragging(bool dragging, bool magnifier, bool crosshair)
    {
        var feedback = CaptureTargetFeedback.Create(dragging, Window, new PixelRect(0, 0, 200, 100), magnifier, crosshair);
        Assert.Equal(magnifier, feedback.Magnifier);
        Assert.Equal(crosshair, feedback.Crosshair);
    }

    [Fact]
    public void UntitledWindowStillClearlyIdentifiesTheWindowTarget()
    {
        var feedback = CaptureTargetFeedback.Create(false, Window with { Title = "" }, WindowBounds, true, true);
        Assert.Equal("Window  ·  720 × 480", feedback.Label);
        Assert.True(feedback.Magnifier);
        Assert.True(feedback.Crosshair);
    }

    [Fact]
    public void DesktopHoverRemainsAScreenTarget()
    {
        var feedback = CaptureTargetFeedback.Create(false, null, new PixelRect(0, 0, 1920, 1080), true, true);
        Assert.Equal("Screen  ·  1920 × 1080", feedback.Label);
        Assert.False(feedback.WindowTarget);
        Assert.True(feedback.Magnifier);
        Assert.True(feedback.Crosshair);
        Assert.Equal("Drag to select an area  ·  Space = screen  ·  Esc = cancel", feedback.Hint);
    }
}
