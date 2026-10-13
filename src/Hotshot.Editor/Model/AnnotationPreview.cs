using System.Numerics;

namespace Hotshot.Editor.Model;

internal static class AnnotationPreview
{
    public static Annotation? Create(EditorState state)
    {
        var annotation = state.Selected?.Clone() ?? state.CreateAnnotation(state.Tool, new Vector2(20, 20));
        switch (annotation)
        {
            case SegmentAnnotation segment:
                segment.Start = new Vector2(20, 64);
                segment.End = new Vector2(156, 24);
                break;
            case BoxAnnotation box:
                box.Rect = new RectF(24, 20, 128, 56);
                break;
            case HighlighterAnnotation highlighter:
                highlighter.Points = [new(24, 52), new(76, 44), new(148, 52)];
                break;
            case PenAnnotation pen:
                pen.Points = [new(20, 64), new(52, 24), new(84, 62), new(120, 28), new(156, 52)];
                break;
            case TextAnnotation text:
                text.Position = new Vector2(20, 20);
                text.Text = "Aa";
                break;
            case StepAnnotation step:
                step.Center = new Vector2(180, 160);
                break;
        }
        return annotation;
    }
}
