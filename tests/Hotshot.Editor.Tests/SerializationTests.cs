using System.Numerics;
using Hotshot.Editor.Model;

namespace Hotshot.Editor.Tests;

public sealed class SerializationTests
{
    [Fact]
    public void Json_round_trip_preserves_every_annotation_type()
    {
        var doc = new AnnotationDocument(1920, 1080);
        var arrow = new ArrowAnnotation { Start = new(10, 20), End = new(300.5f, 400.25f), Color = RgbaColor.Blue, StrokeWidth = 10 };
        doc.Add(arrow);
        doc.Add(new LineAnnotation { Start = new(1, 2), End = new(3, 4), Color = RgbaColor.Black });
        doc.Add(new RectangleAnnotation { Rect = new RectF(5, 6, 70, 80), Filled = true, Color = RgbaColor.Green });
        doc.Add(new EllipseAnnotation { Rect = new RectF(15, 16, 170, 180), Color = RgbaColor.Orange with { A = 128 } });
        doc.Add(new PenAnnotation { Points = [new(1, 1), new(2, 3), new(5, 8)], StrokeWidth = 3 });
        doc.Add(new HighlighterAnnotation { Points = [new(10, 10), new(200, 10)] });
        doc.Add(new TextAnnotation { Position = new(40, 50), Text = "Hello\nworld \"quoted\" ✓", FontSize = 32, HasBackground = true, Color = RgbaColor.Purple });
        doc.Add(new StepAnnotation { Center = new(100, 100), FontSize = 20 });
        doc.Add(new StepAnnotation { Center = new(200, 100) });
        doc.Add(new BlurAnnotation { Rect = new RectF(300, 300, 64, 32), StrokeWidth = 10 });
        doc.SetCrop(new RectF(10, 10, 1000, 500));

        var json = doc.ToJson();
        var copy = AnnotationDocument.FromJson(json);

        Assert.Equal(1920, copy.ImageWidth);
        Assert.Equal(1080, copy.ImageHeight);
        Assert.Equal(doc.Crop, copy.Crop);
        Assert.Equal(doc.Annotations.Count, copy.Annotations.Count);
        for (var i = 0; i < doc.Annotations.Count; i++)
        {
            Assert.Equal(doc.Annotations[i].GetType(), copy.Annotations[i].GetType());
            Assert.Equal(doc.Annotations[i].Id, copy.Annotations[i].Id);
            Assert.Equal(AnnotationDocument.SerializeAnnotation(doc.Annotations[i]), AnnotationDocument.SerializeAnnotation(copy.Annotations[i]));
        }

        var text = Assert.IsType<TextAnnotation>(copy.Annotations[6]);
        Assert.Equal("Hello\nworld \"quoted\" ✓", text.Text);
        Assert.True(text.HasBackground);
        Assert.Equal(RgbaColor.Purple, text.Color);
        Assert.Equal(new RgbaColor(0xFF, 0x95, 0x00, 128), copy.Annotations[3].Color);
        Assert.Equal(new Vector2(300.5f, 400.25f), ((ArrowAnnotation)copy.Annotations[0]).End);
        Assert.Equal(new[] { 1, 2 }, copy.Annotations.OfType<StepAnnotation>().Select(s => s.Number));
        Assert.Equal(3, ((PenAnnotation)copy.Annotations[4]).Points.Count);
        Assert.False(copy.IsDirty);
        Assert.False(copy.CanUndo);

        // The format is stable and readable.
        Assert.Contains("\"type\": \"arrow\"", json);
        Assert.Contains("\"color\": \"#0A84FF\"", json);
        Assert.DoesNotContain("measured", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kind", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_json_is_rejected()
    {
        Assert.False(AnnotationDocument.TryFromJson("{ not json", out _));
        Assert.False(AnnotationDocument.TryFromJson("""{"version":1,"imageWidth":10,"imageHeight":10,"annotations":[{"type":"unknown"}]}""", out _));
        Assert.False(AnnotationDocument.TryFromJson("""{"version":99,"imageWidth":10,"imageHeight":10,"annotations":[]}""", out _));
        Assert.True(AnnotationDocument.TryFromJson("""{"version":1,"imageWidth":10,"imageHeight":10,"annotations":[]}""", out var ok));
        Assert.NotNull(ok);
    }

    [Theory]
    [InlineData("#FF3B30", 255, 59, 48, 255)]
    [InlineData("0a84ff80", 10, 132, 255, 128)]
    public void Color_parsing(string hex, int r, int g, int b, int a)
    {
        Assert.True(RgbaColor.TryParse(hex, out var c));
        Assert.Equal(new RgbaColor((byte)r, (byte)g, (byte)b, (byte)a), c);
        Assert.True(RgbaColor.TryParse(c.ToHex(), out var again));
        Assert.Equal(c, again);
    }

    [Fact]
    public void Contrasting_text_color()
    {
        Assert.Equal(RgbaColor.Black, RgbaColor.Yellow.Contrasting);
        Assert.Equal(RgbaColor.Black, RgbaColor.White.Contrasting);
        Assert.Equal(RgbaColor.White, RgbaColor.Blue.Contrasting);
        Assert.Equal(RgbaColor.White, RgbaColor.Red.Contrasting);
    }
}
