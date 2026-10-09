using System.Numerics;
using System.Text.Json.Serialization;

namespace Hotshot.Editor.Model;

internal enum AnnotationKind
{
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Pen,
    Highlighter,
    Text,
    Step,
    Blur,
}

/// <summary>Base class for all annotation objects. Coordinates are image pixels.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ArrowAnnotation), "arrow")]
[JsonDerivedType(typeof(LineAnnotation), "line")]
[JsonDerivedType(typeof(RectangleAnnotation), "rectangle")]
[JsonDerivedType(typeof(EllipseAnnotation), "ellipse")]
[JsonDerivedType(typeof(PenAnnotation), "pen")]
[JsonDerivedType(typeof(HighlighterAnnotation), "highlighter")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(StepAnnotation), "step")]
[JsonDerivedType(typeof(BlurAnnotation), "blur")]
internal abstract class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public RgbaColor Color { get; set; } = RgbaColor.Red;

    public float StrokeWidth { get; set; } = StylePresets.DefaultStrokeWidth;

    [JsonIgnore]
    public AnnotationKind Kind => this switch
    {
        ArrowAnnotation => AnnotationKind.Arrow,
        LineAnnotation => AnnotationKind.Line,
        RectangleAnnotation => AnnotationKind.Rectangle,
        EllipseAnnotation => AnnotationKind.Ellipse,
        HighlighterAnnotation => AnnotationKind.Highlighter,
        PenAnnotation => AnnotationKind.Pen,
        TextAnnotation => AnnotationKind.Text,
        StepAnnotation => AnnotationKind.Step,
        BlurAnnotation => AnnotationKind.Blur,
        _ => throw new NotSupportedException(GetType().Name),
    };

    /// <summary>Geometric bounds (without stroke thickness).</summary>
    public abstract RectF GetBounds();

    /// <summary>Area touched when rendered (stroke, arrowheads, shadows…), used for invalidation and selection outlines.</summary>
    public virtual RectF GetVisualBounds() => GetBounds().Inflate(StrokeWidth / 2f + 1f);

    public abstract bool HitTest(Vector2 point, float tolerance);

    public abstract void Translate(Vector2 delta);

    public virtual IReadOnlyList<AnnotationHandle> GetHandles() => [];

    /// <summary>Moves a resize handle to <paramref name="point"/>. <paramref name="constrain"/> = Shift.</summary>
    public virtual void MoveHandle(HandleKind handle, Vector2 point, bool constrain)
    {
    }

    public Annotation Clone()
    {
        var clone = (Annotation)MemberwiseClone();
        clone.OnCloned();
        return clone;
    }

    protected virtual void OnCloned()
    {
    }
}

internal abstract class SegmentAnnotation : Annotation
{
    public Vector2 Start { get; set; }

    public Vector2 End { get; set; }

    public override RectF GetBounds() => RectF.FromPoints(Start, End);

    public override bool HitTest(Vector2 point, float tolerance) =>
        GeometryMath.DistanceToSegment(point, Start, End) <= StrokeWidth / 2f + tolerance;

    public override void Translate(Vector2 delta)
    {
        Start += delta;
        End += delta;
    }

    public override IReadOnlyList<AnnotationHandle> GetHandles() =>
        [new(HandleKind.Start, Start), new(HandleKind.End, End)];

    public override void MoveHandle(HandleKind handle, Vector2 point, bool constrain)
    {
        if (handle == HandleKind.Start)
        {
            Start = constrain ? GeometryMath.SnapAngle(End, point) : point;
        }
        else if (handle == HandleKind.End)
        {
            End = constrain ? GeometryMath.SnapAngle(Start, point) : point;
        }
    }
}

internal sealed class ArrowAnnotation : SegmentAnnotation
{
    public override RectF GetVisualBounds()
    {
        var shape = ArrowGeometry.Compute(Start, End, StrokeWidth);
        return base.GetVisualBounds().Union(RectF.FromPoints([shape.LeftWing, shape.RightWing, shape.Tip])).Inflate(2f);
    }

    public override bool HitTest(Vector2 point, float tolerance)
    {
        if (base.HitTest(point, tolerance))
        {
            return true;
        }

        var shape = ArrowGeometry.Compute(Start, End, StrokeWidth);
        return GeometryMath.DistanceToSegment(point, shape.Tip, (shape.LeftWing + shape.RightWing) / 2f) <= shape.HeadHalfWidth * 0.6f + tolerance;
    }
}

internal sealed class LineAnnotation : SegmentAnnotation;

internal abstract class BoxAnnotation : Annotation
{
    public RectF Rect { get; set; }

    public override RectF GetBounds() => Rect;

    public override void Translate(Vector2 delta) => Rect = Rect.Offset(delta);

    public override IReadOnlyList<AnnotationHandle> GetHandles() => GeometryMath.RectHandles(Rect);

    public override void MoveHandle(HandleKind handle, Vector2 point, bool constrain) =>
        Rect = GeometryMath.ResizeRect(Rect, handle, point, constrain);
}

internal sealed class RectangleAnnotation : BoxAnnotation
{
    public bool Filled { get; set; }

    public override bool HitTest(Vector2 point, float tolerance) => Filled
        ? Rect.Inflate(StrokeWidth / 2f + tolerance).Contains(point)
        : GeometryMath.HitRectOutline(Rect, point, StrokeWidth / 2f + tolerance);
}

internal sealed class EllipseAnnotation : BoxAnnotation
{
    public bool Filled { get; set; }

    public override bool HitTest(Vector2 point, float tolerance) =>
        GeometryMath.HitEllipse(Rect, point, StrokeWidth / 2f + tolerance, Filled);
}

/// <summary>Pixelated (obscured) region of the base image.</summary>
internal sealed class BlurAnnotation : BoxAnnotation
{
    [JsonIgnore]
    public int BlockSize => StylePresets.BlurBlockSize(StrokeWidth);

    public override RectF GetVisualBounds() => Rect;

    public override bool HitTest(Vector2 point, float tolerance) => Rect.Inflate(tolerance).Contains(point);
}

internal class PenAnnotation : Annotation
{
    public List<Vector2> Points { get; set; } = [];

    /// <summary>Actual nib width in image pixels.</summary>
    public virtual float GetEffectiveWidth() => StrokeWidth;

    public override RectF GetBounds() => RectF.FromPoints(Points);

    public override RectF GetVisualBounds() => GetBounds().Inflate(GetEffectiveWidth() / 2f + 1f);

    public override bool HitTest(Vector2 point, float tolerance) =>
        GeometryMath.DistanceToPolyline(point, Points) <= GetEffectiveWidth() / 2f + tolerance;

    public override void Translate(Vector2 delta)
    {
        for (var i = 0; i < Points.Count; i++)
        {
            Points[i] += delta;
        }
    }

    public override IReadOnlyList<AnnotationHandle> GetHandles()
    {
        var b = GetBounds();
        if (b.Width < 2f || b.Height < 2f)
        {
            return [];
        }

        return
        [
            new(HandleKind.TopLeft, b.TopLeft),
            new(HandleKind.TopRight, new Vector2(b.Right, b.Top)),
            new(HandleKind.BottomRight, b.BottomRight),
            new(HandleKind.BottomLeft, new Vector2(b.Left, b.Bottom)),
        ];
    }

    public override void MoveHandle(HandleKind handle, Vector2 point, bool constrain)
    {
        var old = GetBounds();
        if (old.Width < 1e-3f || old.Height < 1e-3f)
        {
            return;
        }

        var resized = GeometryMath.ResizeRect(old, handle, point, false);
        if (constrain)
        {
            // Keep the aspect ratio: scale uniformly by the dominant axis.
            var k = MathF.Max(resized.Width / old.Width, resized.Height / old.Height);
            var w = old.Width * k;
            var h = old.Height * k;
            resized = handle switch
            {
                HandleKind.TopLeft => new RectF(old.Right - w, old.Bottom - h, w, h),
                HandleKind.TopRight => new RectF(old.Left, old.Bottom - h, w, h),
                HandleKind.BottomLeft => new RectF(old.Right - w, old.Top, w, h),
                _ => new RectF(old.Left, old.Top, w, h),
            };
        }

        var sx = MathF.Max(resized.Width, 1f) / old.Width;
        var sy = MathF.Max(resized.Height, 1f) / old.Height;
        for (var i = 0; i < Points.Count; i++)
        {
            var p = Points[i] - old.TopLeft;
            Points[i] = resized.TopLeft + new Vector2(p.X * sx, p.Y * sy);
        }
    }

    protected override void OnCloned() => Points = [.. Points];
}

/// <summary>Semi-transparent marker rendered with a multiply blend.</summary>
internal sealed class HighlighterAnnotation : PenAnnotation
{
    public HighlighterAnnotation()
    {
        Color = RgbaColor.Yellow;
    }

    public override float GetEffectiveWidth() => StrokeWidth * StylePresets.HighlighterWidthFactor;
}

internal sealed class TextAnnotation : Annotation
{
    public const string FontFamily = "Segoe UI";

    /// <summary>Top-left corner of the text box (including padding).</summary>
    public Vector2 Position { get; set; }

    public string Text { get; set; } = string.Empty;

    public float FontSize { get; set; } = StylePresets.DefaultFontSize;

    /// <summary>Draws a rounded "pill" in <see cref="Annotation.Color"/> behind contrasting text.</summary>
    public bool HasBackground { get; set; }

    /// <summary>Text layout size measured by the renderer for <see cref="MeasuredKey"/> (not persisted).</summary>
    [JsonIgnore]
    public Vector2 MeasuredTextSize { get; set; }

    [JsonIgnore]
    public string? MeasuredKey { get; set; }

    [JsonIgnore]
    public string LayoutKey => $"{FontSize:R}|{Text}";

    public Vector2 GetPadding() => new(MathF.Round(FontSize * 0.4f), MathF.Round(FontSize * 0.2f));

    public float GetCornerRadius() => FontSize * 0.35f;

    public Vector2 GetTextSize()
    {
        if (MeasuredKey == LayoutKey)
        {
            return MeasuredTextSize;
        }

        var lines = (Text.Length == 0 ? " " : Text).Split('\n');
        var longest = lines.Max(l => l.Length);
        return new Vector2(MathF.Max(1, longest) * FontSize * 0.55f, lines.Length * FontSize * 1.33f);
    }

    public override RectF GetBounds()
    {
        var pad = GetPadding();
        var size = GetTextSize();
        return new RectF(Position.X, Position.Y, size.X + pad.X * 2, size.Y + pad.Y * 2);
    }

    public override RectF GetVisualBounds() => GetBounds().Inflate(2f);

    public override bool HitTest(Vector2 point, float tolerance) => GetBounds().Inflate(tolerance).Contains(point);

    public override void Translate(Vector2 delta) => Position += delta;
}

/// <summary>Numbered circular badge. Numbers are assigned by <see cref="AnnotationDocument"/> in z-order.</summary>
internal sealed class StepAnnotation : Annotation
{
    public Vector2 Center { get; set; }

    public int Number { get; set; } = 1;

    /// <summary>Badge size follows the font size selector.</summary>
    public float FontSize { get; set; } = StylePresets.DefaultFontSize;

    public float GetRadius() => MathF.Max(6f, FontSize * 0.7f);

    public override RectF GetBounds()
    {
        var r = GetRadius();
        return new RectF(Center.X - r, Center.Y - r, r * 2, r * 2);
    }

    public override RectF GetVisualBounds() => GetBounds().Inflate(3f);

    public override bool HitTest(Vector2 point, float tolerance) => Vector2.Distance(point, Center) <= GetRadius() + tolerance;

    public override void Translate(Vector2 delta) => Center += delta;
}

/// <summary>Which style properties apply to which annotation kinds.</summary>
internal static class StyleTraits
{
    public static bool UsesColor(AnnotationKind k) => k != AnnotationKind.Blur;

    public static bool UsesStroke(AnnotationKind k) => k is not (AnnotationKind.Text or AnnotationKind.Step);

    public static bool UsesFontSize(AnnotationKind k) => k is AnnotationKind.Text or AnnotationKind.Step;

    public static bool UsesFill(AnnotationKind k) => k is AnnotationKind.Rectangle or AnnotationKind.Ellipse;

    public static bool UsesTextBackground(AnnotationKind k) => k == AnnotationKind.Text;

    public static AnnotationKind? KindForTool(EditorTool tool) => tool switch
    {
        EditorTool.Arrow => AnnotationKind.Arrow,
        EditorTool.Line => AnnotationKind.Line,
        EditorTool.Rectangle => AnnotationKind.Rectangle,
        EditorTool.Ellipse => AnnotationKind.Ellipse,
        EditorTool.Pen => AnnotationKind.Pen,
        EditorTool.Highlighter => AnnotationKind.Highlighter,
        EditorTool.Text => AnnotationKind.Text,
        EditorTool.Step => AnnotationKind.Step,
        EditorTool.Blur => AnnotationKind.Blur,
        _ => null,
    };

    public static EditorTool ToolForKind(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Arrow => EditorTool.Arrow,
        AnnotationKind.Line => EditorTool.Line,
        AnnotationKind.Rectangle => EditorTool.Rectangle,
        AnnotationKind.Ellipse => EditorTool.Ellipse,
        AnnotationKind.Pen => EditorTool.Pen,
        AnnotationKind.Highlighter => EditorTool.Highlighter,
        AnnotationKind.Text => EditorTool.Text,
        AnnotationKind.Step => EditorTool.Step,
        AnnotationKind.Blur => EditorTool.Blur,
        _ => EditorTool.Select,
    };
}
