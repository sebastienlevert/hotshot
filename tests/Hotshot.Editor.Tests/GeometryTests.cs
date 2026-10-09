using System.Numerics;
using Hotshot.Editor.Model;

namespace Hotshot.Editor.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void Line_and_arrow_hit_testing()
    {
        var line = new LineAnnotation { Start = new(0, 0), End = new(100, 0), StrokeWidth = 6 };
        Assert.True(line.HitTest(new Vector2(50, 2), 1));
        Assert.True(line.HitTest(new Vector2(50, -3.5f), 1));
        Assert.False(line.HitTest(new Vector2(50, 5), 1));
        Assert.False(line.HitTest(new Vector2(110, 0), 1));

        var arrow = new ArrowAnnotation { Start = new(0, 0), End = new(200, 0), StrokeWidth = 6 };
        var head = ArrowGeometry.Compute(arrow.Start, arrow.End, arrow.StrokeWidth);
        Assert.True(head.HeadHalfWidth > arrow.StrokeWidth);
        Assert.True(arrow.HitTest(new Vector2(195, head.HeadHalfWidth * 0.3f), 1));
    }

    [Fact]
    public void Arrow_head_scales_with_stroke_and_shrinks_for_short_arrows()
    {
        var thin = ArrowGeometry.Compute(Vector2.Zero, new Vector2(500, 0), 3);
        var thick = ArrowGeometry.Compute(Vector2.Zero, new Vector2(500, 0), 10);
        Assert.True(thick.HeadLength > thin.HeadLength);
        Assert.True(thick.HeadHalfWidth > thin.HeadHalfWidth);
        Assert.Equal(new Vector2(500, 0), thick.Tip);
        Assert.True(thick.ShaftEnd.X < 500 && thick.ShaftEnd.X > 500 - thick.HeadLength);

        var tiny = ArrowGeometry.Compute(Vector2.Zero, new Vector2(20, 0), 10);
        Assert.True(tiny.HeadLength <= 12.001f);
    }

    [Fact]
    public void Rectangle_outline_vs_filled_hit_testing()
    {
        var outline = new RectangleAnnotation { Rect = new RectF(100, 100, 200, 100), StrokeWidth = 4 };
        Assert.True(outline.HitTest(new Vector2(100, 150), 2));
        Assert.True(outline.HitTest(new Vector2(200, 201), 2));
        Assert.False(outline.HitTest(new Vector2(200, 150), 2));

        outline.Filled = true;
        Assert.True(outline.HitTest(new Vector2(200, 150), 2));
        Assert.False(outline.HitTest(new Vector2(320, 150), 2));
    }

    [Fact]
    public void Ellipse_hit_testing()
    {
        var e = new EllipseAnnotation { Rect = new RectF(0, 0, 200, 100), StrokeWidth = 4 };
        Assert.True(e.HitTest(new Vector2(100, 1), 2));
        Assert.True(e.HitTest(new Vector2(199, 50), 2));
        Assert.False(e.HitTest(new Vector2(100, 50), 2));
        Assert.False(e.HitTest(new Vector2(5, 5), 2));
        e.Filled = true;
        Assert.True(e.HitTest(new Vector2(100, 50), 2));
    }

    [Fact]
    public void Pen_text_step_blur_hit_testing()
    {
        var pen = new PenAnnotation { Points = [new(0, 0), new(100, 100)], StrokeWidth = 4 };
        Assert.True(pen.HitTest(new Vector2(50, 52), 1));
        Assert.False(pen.HitTest(new Vector2(50, 70), 1));

        var hl = new HighlighterAnnotation { Points = [new(0, 0), new(100, 0)], StrokeWidth = 6 };
        Assert.True(hl.HitTest(new Vector2(50, 11), 0));

        var text = new TextAnnotation { Position = new(10, 10), Text = "Hello", FontSize = 20 };
        Assert.True(text.HitTest(new Vector2(15, 15), 0));
        Assert.False(text.HitTest(new Vector2(5, 5), 0));
        text.MeasuredTextSize = new Vector2(100, 30);
        text.MeasuredKey = text.LayoutKey;
        var b = text.GetBounds();
        Assert.Equal(100 + 2 * text.GetPadding().X, b.Width);
        text.Text = "Changed";
        Assert.NotEqual(100 + 2 * text.GetPadding().X, text.GetBounds().Width);

        var step = new StepAnnotation { Center = new(50, 50), FontSize = 20 };
        Assert.True(step.HitTest(new Vector2(50 + step.GetRadius() - 1, 50), 0));
        Assert.False(step.HitTest(new Vector2(50 + step.GetRadius() + 3, 50), 0));

        var blur = new BlurAnnotation { Rect = new RectF(0, 0, 50, 50) };
        Assert.True(blur.HitTest(new Vector2(25, 25), 0));
    }

    [Fact]
    public void Crop_normalization()
    {
        Assert.Null(CropMath.Normalize(null, 100, 100));
        Assert.Null(CropMath.Normalize(new RectF(0, 0, 100, 100), 100, 100));
        Assert.Null(CropMath.Normalize(new RectF(-10, -10, 200, 200), 100, 100));
        Assert.Null(CropMath.Normalize(new RectF(10, 10, 2, 50), 100, 100));
        Assert.Equal(new RectF(0, 10, 50, 90), CropMath.Normalize(new RectF(50, 120, -80, -110.2f), 100, 100));
        Assert.Equal(new RectF(10, 20, 30, 40), CropMath.Normalize(new RectF(10.2f, 19.7f, 29.9f, 40.1f), 100, 100));

        Assert.Equal(new PixelRect(0, 0, 100, 80), CropMath.OutputRect(null, 100, 80));
        Assert.Equal(new PixelRect(10, 20, 30, 40), CropMath.OutputRect(new RectF(10, 20, 30, 40), 100, 80));
    }

    [Fact]
    public void Resize_rect_edges_and_square()
    {
        var r = new RectF(10, 10, 100, 50);
        Assert.Equal(new RectF(10, 0, 100, 60), GeometryMath.ResizeRect(r, HandleKind.Top, new Vector2(999, 0), false));
        Assert.Equal(new RectF(10, 10, 140, 50), GeometryMath.ResizeRect(r, HandleKind.Right, new Vector2(150, 999), false));
        var sq = GeometryMath.ResizeRect(r, HandleKind.Bottom, new Vector2(0, 110), true);
        Assert.Equal(sq.Width, sq.Height);
        Assert.Equal(r.Center.X, sq.Center.X, 3);
        Assert.Equal(new Vector2(30, -30), GeometryMath.ConstrainSquare(Vector2.Zero, new Vector2(30, -10)));
    }

    [Fact]
    public void Simplify_keeps_endpoints_and_corners()
    {
        var pts = new List<Vector2>();
        for (var i = 0; i <= 100; i++)
        {
            pts.Add(new Vector2(i, 0));
        }

        for (var i = 1; i <= 100; i++)
        {
            pts.Add(new Vector2(100, i));
        }

        var s = PathSmoothing.Simplify(pts, 0.5f);
        Assert.Equal(new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100) }, s);

        var segs = PathSmoothing.QuadraticSegments(pts).ToList();
        Assert.Equal(pts[^1], segs[^1].End);
    }

    [Fact]
    public void Pixelate_averages_blocks()
    {
        // 4x2 image; region covers the right 4x2 with block 2 → two blocks.
        const int w = 6, h = 2;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                px[i] = (byte)(x * 40);
                px[i + 1] = (byte)(y * 100);
                px[i + 2] = 7;
                px[i + 3] = 255;
            }
        }

        var region = new PixelRect(2, 0, 4, 2);
        var o = Pixelator.Pixelate(px, w, h, region, 2);
        Assert.Equal(4 * 2 * 4, o.Length);
        // Block 1 = x 2..3 → B avg (80+120)/2 = 100, G avg (0+100)/2 = 50.
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var i = (y * 4 + x) * 4;
                Assert.Equal(100, o[i]);
                Assert.Equal(50, o[i + 1]);
                Assert.Equal(7, o[i + 2]);
                Assert.Equal(255, o[i + 3]);
            }
        }

        // Block 2 = x 4..5 → (160+200)/2 = 180.
        Assert.Equal(180, o[(0 * 4 + 2) * 4]);
        Assert.Equal(180, o[(1 * 4 + 3) * 4]);
    }

    [Fact]
    public void Pixelate_obscures_detail()
    {
        // Fine checkerboard ("text") collapses to a uniform gray inside each block.
        const int w = 32, h = 32;
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var v = (byte)(((i % w) + (i / w)) % 2 == 0 ? 0 : 255);
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v;
            px[i * 4 + 3] = 255;
        }

        var o = Pixelator.Pixelate(px, w, h, new PixelRect(0, 0, w, h), 8);
        Assert.All(Enumerable.Range(0, w * h), i => Assert.InRange(o[i * 4], (byte)126, (byte)129));
    }
}
