using System.Numerics;
using Hotshot.Editor.Model;

namespace Hotshot.Editor.Tests;

public sealed class EditorStateTests
{
    [Fact]
    public void Style_change_updates_selection_with_undo_and_tool_style()
    {
        var doc = new AnnotationDocument(800, 600);
        var state = new EditorState(doc);
        var arrow = state.CreateAnnotation(EditorTool.Arrow, new Vector2(10, 10))!;
        Assert.Equal(RgbaColor.Red, arrow.Color);
        doc.Add(arrow);
        state.Select(arrow.Id);

        state.SetColor(RgbaColor.Blue);
        Assert.Equal(RgbaColor.Blue, doc.Annotations[0].Color);
        Assert.Equal(RgbaColor.Blue, state.GetToolStyle(EditorTool.Arrow).Color);

        state.SetStrokeWidth(10);
        Assert.Equal(10, doc.Annotations[0].StrokeWidth);

        doc.Undo();
        Assert.Equal(StylePresets.DefaultStrokeWidth, doc.Annotations[0].StrokeWidth);
        doc.Undo();
        Assert.Equal(RgbaColor.Red, doc.Annotations[0].Color);
    }

    [Fact]
    public void Style_without_selection_only_changes_tool_style()
    {
        var doc = new AnnotationDocument(800, 600);
        var state = new EditorState(doc) { Tool = EditorTool.Rectangle };
        state.SetFilled(true);
        state.SetColor(RgbaColor.Green);
        Assert.False(doc.CanUndo);
        var r = Assert.IsType<RectangleAnnotation>(state.CreateAnnotation(EditorTool.Rectangle, Vector2.Zero));
        Assert.True(r.Filled);
        Assert.Equal(RgbaColor.Green, r.Color);
        Assert.Equal(RgbaColor.Yellow, state.GetToolStyle(EditorTool.Highlighter).Color);
    }

    [Fact]
    public void Font_size_applies_to_text_and_steps()
    {
        var doc = new AnnotationDocument(800, 600);
        var state = new EditorState(doc) { Tool = EditorTool.Step };
        var step = (StepAnnotation)state.CreateAnnotation(EditorTool.Step, new Vector2(5, 5))!;
        doc.Add(step);
        state.Select(step.Id);
        state.SetFontSize(48);
        Assert.Equal(48, ((StepAnnotation)doc.Annotations[0]).FontSize);
    }

    [Fact]
    public void Delete_selected_clears_selection_and_undo_restores()
    {
        var doc = new AnnotationDocument(800, 600);
        var state = new EditorState(doc);
        var a = state.CreateAnnotation(EditorTool.Line, Vector2.Zero)!;
        doc.Add(a);
        state.Select(a.Id);
        Assert.True(state.DeleteSelected());
        Assert.Null(state.SelectedId);
        Assert.Empty(doc.Annotations);
        doc.Undo();
        Assert.Single(doc.Annotations);
    }

    [Fact]
    public void Selection_is_dropped_when_undo_removes_it()
    {
        var doc = new AnnotationDocument(800, 600);
        var state = new EditorState(doc);
        var a = state.CreateAnnotation(EditorTool.Ellipse, Vector2.Zero)!;
        doc.Add(a);
        state.Select(a.Id);
        doc.Undo();
        Assert.Null(state.SelectedId);
    }
}
