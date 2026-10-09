using System.Numerics;

namespace Hotshot.Editor.Model;

/// <summary>Style used for new annotations of a tool (each tool remembers its own).</summary>
internal sealed record ToolStyle(RgbaColor Color, float StrokeWidth, float FontSize, bool Filled, bool TextBackground);

/// <summary>
/// UI-independent editor state: document, active tool, per-tool styles and selection.
/// Style changes apply to the selected annotation (with undo) and to the matching tool's style.
/// </summary>
internal sealed class EditorState
{
    private readonly Dictionary<EditorTool, ToolStyle> _styles = new();
    private EditorTool _tool = EditorTool.Arrow;
    private Guid? _selectedId;

    public EditorState(AnnotationDocument document)
    {
        Document = document;
        foreach (var tool in Enum.GetValues<EditorTool>())
        {
            _styles[tool] = new ToolStyle(RgbaColor.Red, StylePresets.DefaultStrokeWidth, StylePresets.DefaultFontSize, false, false);
        }

        _styles[EditorTool.Highlighter] = _styles[EditorTool.Highlighter] with { Color = RgbaColor.Yellow };
        _styles[EditorTool.Text] = _styles[EditorTool.Text] with { TextBackground = false };
        Document.Changed += (_, _) =>
        {
            if (_selectedId is { } id && Document.Find(id) is null)
            {
                _selectedId = null;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised on any document, selection, tool or style change.</summary>
    public event EventHandler? Changed;

    public AnnotationDocument Document { get; }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool != value)
            {
                _tool = value;
                if (value == EditorTool.Crop)
                {
                    _selectedId = null;
                }

                OnChanged();
            }
        }
    }

    public Guid? SelectedId => _selectedId;

    public Annotation? Selected => Document.Find(_selectedId);

    /// <summary>The kind whose style the toolbar shows: the selection's kind, otherwise the active tool's.</summary>
    public AnnotationKind? StyleKind => Selected?.Kind ?? StyleTraits.KindForTool(_tool);

    /// <summary>Style currently shown in the toolbar.</summary>
    public ToolStyle CurrentStyle
    {
        get
        {
            if (Selected is { } s)
            {
                return StyleOf(s);
            }

            return _styles[_tool];
        }
    }

    public ToolStyle GetToolStyle(EditorTool tool) => _styles[tool];

    public void Select(Guid? id)
    {
        if (id is { } g && Document.Find(g) is null)
        {
            id = null;
        }

        if (_selectedId != id)
        {
            _selectedId = id;
            OnChanged();
        }
    }

    public void SetColor(RgbaColor color) => ApplyStyle(
        s => s with { Color = color },
        a => StyleTraits.UsesColor(a.Kind),
        a => a.Color = color);

    public void SetStrokeWidth(float width) => ApplyStyle(
        s => s with { StrokeWidth = width },
        a => StyleTraits.UsesStroke(a.Kind),
        a => a.StrokeWidth = width);

    public void SetFontSize(float size) => ApplyStyle(
        s => s with { FontSize = size },
        a => StyleTraits.UsesFontSize(a.Kind),
        a =>
        {
            switch (a)
            {
                case TextAnnotation t: t.FontSize = size; break;
                case StepAnnotation st: st.FontSize = size; break;
            }
        });

    public void SetFilled(bool filled) => ApplyStyle(
        s => s with { Filled = filled },
        a => StyleTraits.UsesFill(a.Kind),
        a =>
        {
            switch (a)
            {
                case RectangleAnnotation r: r.Filled = filled; break;
                case EllipseAnnotation e: e.Filled = filled; break;
            }
        });

    public void SetTextBackground(bool background) => ApplyStyle(
        s => s with { TextBackground = background },
        a => StyleTraits.UsesTextBackground(a.Kind),
        a =>
        {
            if (a is TextAnnotation t)
            {
                t.HasBackground = background;
            }
        });

    public bool DeleteSelected()
    {
        if (_selectedId is not { } id)
        {
            return false;
        }

        _selectedId = null;
        var removed = Document.Remove(id);
        OnChanged();
        return removed;
    }

    /// <summary>Creates a new annotation for <paramref name="tool"/> at <paramref name="point"/> using that tool's style.</summary>
    public Annotation? CreateAnnotation(EditorTool tool, Vector2 point)
    {
        var s = _styles[tool];
        Annotation? a = tool switch
        {
            EditorTool.Arrow => new ArrowAnnotation { Start = point, End = point },
            EditorTool.Line => new LineAnnotation { Start = point, End = point },
            EditorTool.Rectangle => new RectangleAnnotation { Rect = new RectF(point.X, point.Y, 0, 0), Filled = s.Filled },
            EditorTool.Ellipse => new EllipseAnnotation { Rect = new RectF(point.X, point.Y, 0, 0), Filled = s.Filled },
            EditorTool.Blur => new BlurAnnotation { Rect = new RectF(point.X, point.Y, 0, 0) },
            EditorTool.Pen => new PenAnnotation { Points = [point] },
            EditorTool.Highlighter => new HighlighterAnnotation { Points = [point] },
            EditorTool.Text => new TextAnnotation { Position = point, FontSize = s.FontSize, HasBackground = s.TextBackground },
            EditorTool.Step => new StepAnnotation { Center = point, FontSize = s.FontSize, Number = Document.NextStepNumber },
            _ => null,
        };

        if (a is not null)
        {
            a.Color = s.Color;
            a.StrokeWidth = s.StrokeWidth;
        }

        return a;
    }

    public static ToolStyle StyleOf(Annotation a) => new(
        a.Color,
        a.StrokeWidth,
        a switch { TextAnnotation t => t.FontSize, StepAnnotation st => st.FontSize, _ => StylePresets.DefaultFontSize },
        a switch { RectangleAnnotation r => r.Filled, EllipseAnnotation e => e.Filled, _ => false },
        a is TextAnnotation { HasBackground: true });

    private void ApplyStyle(Func<ToolStyle, ToolStyle> updateStyle, Func<Annotation, bool> applies, Action<Annotation> mutate)
    {
        var target = Selected;
        var tool = target is not null ? StyleTraits.ToolForKind(target.Kind) : _tool;
        _styles[tool] = updateStyle(_styles[tool]);
        if (target is not null && applies(target))
        {
            Document.Update(target.Id, mutate);
        }

        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
