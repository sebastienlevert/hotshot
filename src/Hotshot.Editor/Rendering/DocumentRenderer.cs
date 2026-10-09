using System.Numerics;
using Hotshot.Editor.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Hotshot.Editor.Rendering;

/// <summary>
/// Pair of image-sized render targets used to compose base image + annotations
/// (two are needed to ping-pong the multiply blend of highlighters).
/// </summary>
internal sealed class RenderTargetPair : IDisposable
{
    private CanvasRenderTarget? _a;
    private CanvasRenderTarget? _b;

    public (CanvasRenderTarget A, CanvasRenderTarget B) Ensure(CanvasDevice device, int width, int height)
    {
        if (_a is null || _b is null || _a.Device != device || _a.SizeInPixels.Width != width || _a.SizeInPixels.Height != height)
        {
            Dispose();
            _a = Create(device, width, height);
            _b = Create(device, width, height);
        }

        return (_a, _b);
    }

    public void Dispose()
    {
        _a?.Dispose();
        _b?.Dispose();
        _a = _b = null;
    }

    private static CanvasRenderTarget Create(CanvasDevice device, int width, int height) =>
        new(device, width, height, 96f, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
}

/// <summary>
/// Renders an <see cref="AnnotationDocument"/> over its base image at native resolution (DPI 96: 1 DIP = 1 image pixel).
/// The same output is shown on screen (scaled) and exported, so the saved file matches the editor by construction.
/// </summary>
internal sealed class DocumentRenderer : IDisposable
{
    private static readonly Color ShadowColor = Color.FromArgb(110, 0, 0, 0);
    private readonly Dictionary<Guid, PixelatedEntry> _pixelated = new();
    private readonly Dictionary<string, CanvasTextLayout> _layouts = new();
    private readonly CanvasStrokeStyle _round = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
    private readonly CanvasStrokeStyle _shaft = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Flat, LineJoin = CanvasLineJoin.Round };
    private readonly CanvasStrokeStyle _boxJoin = new() { LineJoin = CanvasLineJoin.Round };
    private byte[]? _basePixels;

    public DocumentRenderer(CanvasBitmap baseImage)
    {
        BaseImage = baseImage;
        Device = baseImage.Device;
        Width = (int)baseImage.SizeInPixels.Width;
        Height = (int)baseImage.SizeInPixels.Height;
    }

    public CanvasDevice Device { get; private set; }

    public CanvasBitmap BaseImage { get; private set; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Swaps the base bitmap (e.g. after a lost device) and drops device-bound caches.</summary>
    public void ReplaceBaseImage(CanvasBitmap baseImage)
    {
        ClearCaches();
        BaseImage = baseImage;
        Device = baseImage.Device;
    }

    /// <summary>Composes base image + annotations into <paramref name="targets"/>; returns the target holding the result.</summary>
    public CanvasRenderTarget Render(AnnotationDocument document, RenderTargetPair targets, Guid? hiddenId = null)
    {
        var (current, other) = targets.Ensure(Device, Width, Height);
        var items = document.Annotations.Where(a => a.Id != hiddenId).ToList();
        PrunePixelCache(document);

        using (var ds = current.CreateDrawingSession())
        {
            ds.Clear(default(Color));
            ds.DrawImage(BaseImage, 0, 0, BaseImage.Bounds, 1f, CanvasImageInterpolation.NearestNeighbor);

            // Pixelated regions only ever show base image pixels, so they sit directly above the base.
            foreach (var blur in items.OfType<BlurAnnotation>())
            {
                if (GetPixelated(blur) is { } entry)
                {
                    ds.DrawImage(entry.Bitmap, entry.Rect.X, entry.Rect.Y);
                }
            }
        }

        var run = new List<Annotation>();
        foreach (var a in items)
        {
            switch (a)
            {
                case BlurAnnotation:
                    continue;
                case HighlighterAnnotation h:
                    FlushRun(current, run);
                    DrawHighlighter(current, other, h);
                    (current, other) = (other, current);
                    break;
                default:
                    run.Add(a);
                    break;
            }
        }

        FlushRun(current, run);
        return current;
    }

    /// <summary>Measures a text annotation with DirectWrite so its bounds match what is drawn.</summary>
    public void Measure(TextAnnotation text)
    {
        if (text.MeasuredKey == text.LayoutKey)
        {
            return;
        }

        var layout = GetLayout(text);
        var b = layout.LayoutBoundsIncludingTrailingWhitespace;
        text.MeasuredTextSize = new Vector2(MathF.Max((float)b.Width, text.FontSize * 0.3f), MathF.Max((float)b.Height, text.FontSize));
        text.MeasuredKey = text.LayoutKey;
    }

    public void MeasureAll(AnnotationDocument document)
    {
        foreach (var t in document.Annotations.OfType<TextAnnotation>())
        {
            Measure(t);
        }
    }

    public void Dispose()
    {
        ClearCaches();
        _round.Dispose();
        _shaft.Dispose();
        _boxJoin.Dispose();
    }

    public static Color ToColor(RgbaColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    // ---- runs / effects ----

    private void FlushRun(CanvasRenderTarget target, List<Annotation> run)
    {
        if (run.Count == 0)
        {
            return;
        }

        using var commands = new CanvasCommandList(Device);
        using (var cds = commands.CreateDrawingSession())
        {
            cds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            foreach (var a in run)
            {
                DrawAnnotation(cds, a);
            }
        }

        using var shadow = new ShadowEffect { Source = commands, BlurAmount = 2.5f, ShadowColor = ShadowColor, Optimization = EffectOptimization.Quality };
        using (var ds = target.CreateDrawingSession())
        {
            ds.DrawImage(shadow, 0f, 1.5f);
            ds.DrawImage(commands);
        }

        run.Clear();
    }

    /// <summary>Multiplies the highlighter stroke onto <paramref name="source"/>, writing the result to <paramref name="destination"/>.</summary>
    private void DrawHighlighter(CanvasRenderTarget source, CanvasRenderTarget destination, HighlighterAnnotation h)
    {
        using var stroke = new CanvasCommandList(Device);
        using (var cds = stroke.CreateDrawingSession())
        {
            var color = h.Color.WithAlpha((byte)Math.Round(h.Color.A * StylePresets.HighlighterOpacity));
            DrawPolyline(cds, h.Points, ToColor(color), h.GetEffectiveWidth());
        }

        using var blend = new BlendEffect { Mode = BlendEffectMode.Multiply, Background = source, Foreground = stroke };
        using var ds = destination.CreateDrawingSession();
        ds.Clear(default(Color));
        ds.DrawImage(blend);
    }

    // ---- primitives ----

    private void DrawAnnotation(CanvasDrawingSession ds, Annotation a)
    {
        var color = ToColor(a.Color);
        switch (a)
        {
            case ArrowAnnotation arrow:
                DrawArrow(ds, arrow, color);
                break;
            case LineAnnotation line:
                if (Vector2.Distance(line.Start, line.End) > 0.01f)
                {
                    ds.DrawLine(line.Start, line.End, color, line.StrokeWidth, _round);
                }

                break;
            case RectangleAnnotation r:
                if (r.Rect.Width > 0.01f || r.Rect.Height > 0.01f)
                {
                    var rect = r.Rect.ToRect();
                    if (r.Filled)
                    {
                        ds.FillRectangle(rect, color);
                    }

                    ds.DrawRectangle(rect, color, r.StrokeWidth, _boxJoin);
                }

                break;
            case EllipseAnnotation e:
                if (e.Rect.Width > 0.01f || e.Rect.Height > 0.01f)
                {
                    var c = e.Rect.Center;
                    var rx = e.Rect.Width / 2f;
                    var ry = e.Rect.Height / 2f;
                    if (e.Filled)
                    {
                        ds.FillEllipse(c, rx, ry, color);
                    }

                    ds.DrawEllipse(c, rx, ry, color, e.StrokeWidth);
                }

                break;
            case HighlighterAnnotation:
                break;
            case PenAnnotation pen:
                DrawPolyline(ds, pen.Points, color, pen.GetEffectiveWidth());
                break;
            case TextAnnotation text:
                DrawText(ds, text);
                break;
            case StepAnnotation step:
                DrawStep(ds, step);
                break;
        }
    }

    private void DrawArrow(CanvasDrawingSession ds, ArrowAnnotation arrow, Color color)
    {
        if (Vector2.Distance(arrow.Start, arrow.End) < 0.5f)
        {
            return;
        }

        var shape = ArrowGeometry.Compute(arrow.Start, arrow.End, arrow.StrokeWidth);
        var shaftWidth = MathF.Min(arrow.StrokeWidth, shape.HeadHalfWidth * 1.4f);
        if (Vector2.Distance(arrow.Start, shape.ShaftEnd) > 0.01f && Vector2.Dot(shape.ShaftEnd - arrow.Start, arrow.End - arrow.Start) > 0)
        {
            ds.DrawLine(arrow.Start, shape.ShaftEnd, color, shaftWidth, _shaft);
        }

        using var head = CanvasGeometry.CreatePolygon(Device, [shape.Tip, shape.LeftWing, shape.Notch, shape.RightWing]);
        ds.FillGeometry(head, color);
        ds.DrawGeometry(head, color, MathF.Max(1f, arrow.StrokeWidth * 0.25f), _round);
    }

    private void DrawPolyline(CanvasDrawingSession ds, IReadOnlyList<Vector2> points, Color color, float width)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count == 1 || points.All(p => Vector2.DistanceSquared(p, points[0]) < 1e-4f))
        {
            ds.FillCircle(points[0], width / 2f, color);
            return;
        }

        using var builder = new CanvasPathBuilder(Device);
        builder.BeginFigure(points[0]);
        foreach (var (control, end) in PathSmoothing.QuadraticSegments(points))
        {
            builder.AddQuadraticBezier(control, end);
        }

        builder.EndFigure(CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(geometry, color, width, _round);
    }

    private void DrawText(CanvasDrawingSession ds, TextAnnotation text)
    {
        if (string.IsNullOrEmpty(text.Text))
        {
            return;
        }

        Measure(text);
        var layout = GetLayout(text);
        var bounds = text.GetBounds();
        var pad = text.GetPadding();
        var textColor = text.Color;
        if (text.HasBackground)
        {
            var radius = text.GetCornerRadius();
            ds.FillRoundedRectangle(bounds.ToRect(), radius, radius, ToColor(text.Color));
            textColor = text.Color.Contrasting;
        }

        ds.DrawTextLayout(layout, text.Position + pad, ToColor(textColor));
    }

    private void DrawStep(CanvasDrawingSession ds, StepAnnotation step)
    {
        var r = step.GetRadius();
        var fill = ToColor(step.Color);
        var ringColor = step.Color.Luminance > 0.85f ? Color.FromArgb(255, 60, 60, 60) : Color.FromArgb(255, 255, 255, 255);
        var ring = MathF.Max(1.5f, r * 0.12f);
        ds.FillCircle(step.Center, r, fill);
        ds.DrawCircle(step.Center, r - ring / 2f, ringColor, ring);

        var label = step.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var size = r * (label.Length switch { 1 => 1.15f, 2 => 0.95f, _ => 0.72f });
        using var format = new CanvasTextFormat
        {
            FontFamily = TextAnnotation.FontFamily,
            FontSize = size,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var layout = new CanvasTextLayout(Device, label, format, 0f, 0f);
        var ink = layout.DrawBounds;
        var origin = step.Center - new Vector2((float)(ink.X + ink.Width / 2), (float)(ink.Y + ink.Height / 2));
        ds.DrawTextLayout(layout, origin, ToColor(step.Color.Contrasting));
    }

    // ---- caches ----

    private CanvasTextLayout GetLayout(TextAnnotation text)
    {
        var key = text.LayoutKey;
        if (_layouts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_layouts.Count > 256)
        {
            foreach (var l in _layouts.Values)
            {
                l.Dispose();
            }

            _layouts.Clear();
        }

        using var format = new CanvasTextFormat
        {
            FontFamily = TextAnnotation.FontFamily,
            FontSize = text.FontSize,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        var layout = new CanvasTextLayout(Device, text.Text.Length == 0 ? " " : text.Text, format, 0f, 0f);
        _layouts[key] = layout;
        return layout;
    }

    private PixelatedEntry? GetPixelated(BlurAnnotation blur)
    {
        var rect = PixelRect.FromRect(blur.Rect, Width, Height);
        if (rect.IsEmpty)
        {
            return null;
        }

        var block = blur.BlockSize;
        if (_pixelated.TryGetValue(blur.Id, out var entry) && entry.Rect == rect && entry.BlockSize == block)
        {
            return entry;
        }

        entry?.Bitmap.Dispose();
        var bytes = Pixelator.Pixelate(GetBasePixels(), Width, Height, rect, block);
        var bitmap = CanvasBitmap.CreateFromBytes(Device, bytes, rect.Width, rect.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f, CanvasAlphaMode.Premultiplied);
        entry = new PixelatedEntry(rect, block, bitmap);
        _pixelated[blur.Id] = entry;
        return entry;
    }

    /// <summary>Base image as 32bpp premultiplied BGRA (converted through a render target so any source format works).</summary>
    private byte[] GetBasePixels()
    {
        if (_basePixels is null)
        {
            using var target = new CanvasRenderTarget(Device, Width, Height, 96f, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(default(Color));
                ds.DrawImage(BaseImage, 0, 0, BaseImage.Bounds, 1f, CanvasImageInterpolation.NearestNeighbor);
            }

            _basePixels = target.GetPixelBytes();
        }

        return _basePixels;
    }

    private void PrunePixelCache(AnnotationDocument document)
    {
        if (_pixelated.Count == 0)
        {
            return;
        }

        foreach (var id in _pixelated.Keys.ToList())
        {
            if (document.Find(id) is not BlurAnnotation)
            {
                _pixelated[id].Bitmap.Dispose();
                _pixelated.Remove(id);
            }
        }
    }

    private void ClearCaches()
    {
        foreach (var e in _pixelated.Values)
        {
            e.Bitmap.Dispose();
        }

        _pixelated.Clear();
        foreach (var l in _layouts.Values)
        {
            l.Dispose();
        }

        _layouts.Clear();
        _basePixels = null;
    }

    private sealed record PixelatedEntry(PixelRect Rect, int BlockSize, CanvasBitmap Bitmap);
}

internal static class RenderExtensions
{
    public static Windows.Foundation.Rect ToRect(this RectF r) => new(r.X, r.Y, MathF.Max(0, r.Width), MathF.Max(0, r.Height));
}
