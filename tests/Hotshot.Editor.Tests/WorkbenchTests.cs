using System.Numerics;
using Hotshot.Editor;
using Hotshot.Editor.Model;

namespace Hotshot.Editor.Tests;

public sealed class WorkbenchTests
{
    [Fact]
    public void DuplicateAndReorderLayersAreUndoableAndDoNotAliasPoints()
    {
        var document = new AnnotationDocument(400, 300);
        var pen = new PenAnnotation { Points = [new Vector2(10, 10), new Vector2(40, 50)] };
        var box = new RectangleAnnotation { Rect = new RectF(20, 20, 80, 40) };
        document.Add(pen);
        document.Add(box);
        var id = document.Duplicate(pen.Id);
        var duplicate = Assert.IsType<PenAnnotation>(document.Find(id));
        duplicate.Translate(Vector2.One);
        Assert.Equal(new Vector2(10, 10), pen.Points[0]);
        Assert.True(document.MoveLayer(pen.Id, 2));
        Assert.Equal(pen.Id, document.Annotations[^1].Id);
        Assert.True(document.Undo());
        Assert.Equal(pen.Id, document.Annotations[0].Id);
        Assert.True(document.Redo());
        Assert.Equal(pen.Id, document.Annotations[^1].Id);
        Assert.Equal(3, AnnotationDocument.FromJson(document.ToJson()).Annotations.Count);
    }

    [Fact]
    public void SolidRedactionsRemainDistinctFromBlurAndSupportResize()
    {
        var document = new AnnotationDocument(400, 300);
        var state = new EditorState(document);
        var redaction = Assert.IsType<RedactionAnnotation>(state.CreateAnnotation(EditorTool.Redact, Vector2.Zero));
        redaction.Rect = new RectF(10, 10, 30, 40);
        redaction.MoveHandle(HandleKind.BottomRight, new Vector2(80, 90), false);
        document.Add(redaction);
        Assert.Equal(70, redaction.Rect.Width);
        Assert.False(StyleTraits.UsesColor(redaction.Kind));
        Assert.IsType<RedactionAnnotation>(AnnotationDocument.FromJson(document.ToJson()).Annotations[0]);
    }

    [Fact]
    public void ExportDimensionsMaintainAspectRatioAndIncludePadding()
    {
        var layout = new ImageExportOptions(Width: 500, Padding: 24).GetLayout(1000, 600);
        Assert.Equal((500, 300, 548, 348), layout);
        Assert.Equal((100, 80, 100, 80), new ImageExportOptions().GetLayout(100, 80));
        Assert.Throws<ArgumentException>(() => new ImageExportOptions(Width: 16384, Padding: 20).GetLayout(100, 100));
        Assert.Throws<ArgumentException>(() => new ImageExportOptions(Padding: -1).GetLayout(100, 100));
    }
}
