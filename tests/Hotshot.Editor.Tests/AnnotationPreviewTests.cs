using System.Numerics;
using Hotshot.Editor.Model;

namespace Hotshot.Editor.Tests;

public sealed class AnnotationPreviewTests
{
    [Fact]
    public void PreviewShowsTheChosenGreenThickArrowWithoutEditingTheDocument()
    {
        var document = new AnnotationDocument(800, 600);
        var state = new EditorState(document);
        state.SetColor(RgbaColor.Green);
        state.SetStrokeWidth(10);

        var preview = Assert.IsType<ArrowAnnotation>(AnnotationPreview.Create(state));

        Assert.Equal(RgbaColor.Green, preview.Color);
        Assert.Equal(10, preview.StrokeWidth);
        Assert.NotEqual(preview.Start, preview.End);
        Assert.Empty(document.Annotations);
        Assert.False(document.IsDirty);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void CustomRgbAndTransparencySurvivePreviewCreationAndDocumentRoundtrip()
    {
        var color = new RgbaColor(7, 133, 215, 73);
        var document = new AnnotationDocument(800, 600);
        var state = new EditorState(document);
        state.SetColor(color);
        Assert.Equal(color, AnnotationPreview.Create(state)!.Color);

        document.Add(state.CreateAnnotation(EditorTool.Arrow, Vector2.Zero)!);
        var restored = AnnotationDocument.FromJson(document.ToJson());
        Assert.Equal(color, Assert.Single(restored.Annotations).Color);
    }

    [Fact]
    public void SelectionPreviewUsesTheSelectedStyleAndDoesNotMoveOrAlterItsPoints()
    {
        var document = new AnnotationDocument(800, 600);
        var pen = new PenAnnotation { Color = RgbaColor.Blue, StrokeWidth = 3, Points = [new(10, 20), new(30, 40)] };
        document.Add(pen);
        document.MarkSaved();
        var state = new EditorState(document) { Tool = EditorTool.Select };
        state.Select(pen.Id);

        var preview = Assert.IsType<PenAnnotation>(AnnotationPreview.Create(state));

        Assert.Equal(RgbaColor.Blue, preview.Color);
        Assert.Equal(3, preview.StrokeWidth);
        Assert.NotSame(pen.Points, preview.Points);
        Assert.Equal(new Vector2(10, 20), pen.Points[0]);
        Assert.Equal(new Vector2(30, 40), pen.Points[1]);
        Assert.Equal(pen.Id, state.SelectedId);
        Assert.False(document.IsDirty);
    }

    [Theory]
    [InlineData((int)EditorTool.Arrow)]
    [InlineData((int)EditorTool.Line)]
    [InlineData((int)EditorTool.Rectangle)]
    [InlineData((int)EditorTool.Ellipse)]
    [InlineData((int)EditorTool.Pen)]
    [InlineData((int)EditorTool.Highlighter)]
    [InlineData((int)EditorTool.Text)]
    [InlineData((int)EditorTool.Step)]
    [InlineData((int)EditorTool.Blur)]
    [InlineData((int)EditorTool.Redact)]
    public void EveryDrawingToolHasANonEmptyPreview(int tool)
    {
        var state = new EditorState(new AnnotationDocument(800, 600)) { Tool = (EditorTool)tool };
        var preview = AnnotationPreview.Create(state);
        Assert.NotNull(preview);
        Assert.True(preview.GetVisualBounds().Width > 0);
        Assert.True(preview.GetVisualBounds().Height > 0);
        Assert.Empty(state.Document.Annotations);
    }
}
