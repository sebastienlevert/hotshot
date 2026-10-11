using System.Numerics;
using Hotshot.Editor.Model;

namespace Hotshot.Editor.Tests;

public sealed class EditorViewportTests
{
    [Fact]
    public void FitZoomAndPanMaintainAnchorWithoutResetOnDraw()
    {
        var view = new EditorViewport();
        view.Layout(new Vector2(532, 432), new Vector2(1000, 800));
        Assert.Equal(0.5f, view.Scale);
        var anchor = new Vector2(200, 120);
        var imagePoint = (anchor - view.Offset) / view.Scale;
        view.Zoom(1, anchor);
        Assert.Equal(imagePoint, (anchor - view.Offset) / view.Scale);
        view.Pan(new Vector2(12, 8));
        var offset = view.Offset;
        view.Layout(new Vector2(700, 500), new Vector2(1000, 800));
        Assert.Equal(1, view.Scale);
        Assert.Equal(offset, view.Offset);
        view.Reset();
        view.Layout(new Vector2(532, 432), new Vector2(1000, 800));
        Assert.Equal(0.5f, view.Scale);
    }
}
