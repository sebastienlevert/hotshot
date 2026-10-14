using Hotshot.Interop;

namespace Hotshot.Capture.Tests;

public sealed class WindowSnapGeometryTests
{
    [Theory]
    [InlineData("Windows.UI.Core.CoreWindow", "Modern app", true)]
    [InlineData("Windows.UI.Core.CoreWindow", "", false)]
    [InlineData("Chrome_WidgetWin_1", "Browser", true)]
    [InlineData("WinUIDesktopWin32WindowClass", "Windows app", true)]
    [InlineData("Progman", "Desktop", false)]
    [InlineData("WorkerW", "", false)]
    public void ModernAppWindowsCanSnapButDesktopAndUntitledShellSurfacesDoNot(string className, string title, bool selectable) =>
        Assert.Equal(selectable, WindowEnumerator.IsSelectableClass(className, title));

    [Fact]
    public void SelectsWholeTopmostWindowRatherThanTheOverlappingBackgroundWindow()
    {
        var front = new WindowInfo(1, new PixelRect(120, 80, 720, 480), "Front", "Windows.UI.Core.CoreWindow", 1);
        var back = new WindowInfo(2, new PixelRect(0, 0, 1920, 1080), "Back", "Window", 2);
        var selected = WindowSnapGeometry.AtPoint([front, back], 450, 300);
        Assert.NotNull(selected);
        Assert.Same(front, selected);
        Assert.Equal(front.Bounds, selected.Bounds);
    }

    [Fact]
    public void NegativeCoordinatesAndWindowsAcrossMonitorEdgesKeepTheirFullBounds()
    {
        var window = new WindowInfo(1, new PixelRect(-400, 100, 1200, 800), "Wide", "Window", 1);
        Assert.Same(window, WindowSnapGeometry.AtPoint([window], -200, 400));
        Assert.Same(window, WindowSnapGeometry.AtPoint([window], 700, 400));
        Assert.Null(WindowSnapGeometry.AtPoint([window], 800, 400));
        Assert.Null(WindowSnapGeometry.AtPoint([window], 0, 900));
    }

    [Theory]
    [InlineData(1, 3, false)]
    [InlineData(1, 4, true)]
    [InlineData(1.5, 5, false)]
    [InlineData(1.5, 6, true)]
    [InlineData(2, 7, false)]
    [InlineData(2, 8, true)]
    public void NormalClickJitterStillSelectsTheWindowAtEachDisplayScale(double scale, int movement, bool drag)
    {
        Assert.Equal(drag, WindowSnapGeometry.IsDrag(100, 100, 100 + movement, 100, scale));
        Assert.Equal(drag, WindowSnapGeometry.IsDrag(100, 100, 100, 100 - movement, scale));
    }
}
