using System.Numerics;

namespace Hotshot.Editor.Model;

internal enum EditorTool
{
    Select,
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Pen,
    Highlighter,
    Text,
    Step,
    Blur,
    Crop,
}

internal static class StylePresets
{
    public static IReadOnlyList<float> StrokeWidths { get; } = [3f, 6f, 10f];
    public static IReadOnlyList<string> StrokeNames { get; } = ["Thin", "Medium", "Thick"];
    public const float DefaultStrokeWidth = 6f;

    public static IReadOnlyList<float> FontSizes { get; } = [12f, 14f, 16f, 18f, 20f, 24f, 28f, 32f, 40f, 48f, 56f, 64f, 72f, 96f];
    public const float DefaultFontSize = 24f;

    /// <summary>Highlighter nib width = stroke width × this factor.</summary>
    public const float HighlighterWidthFactor = 4f;
    public const float HighlighterOpacity = 0.55f;

    /// <summary>Pixelation block size (image px) for a blur region with the given stroke preset.</summary>
    public static int BlurBlockSize(float strokeWidth) => Math.Clamp((int)MathF.Round(strokeWidth * 2.5f + 4f), 6, 64);

    public static int NearestStrokeIndex(float width)
    {
        var best = 0;
        for (var i = 1; i < StrokeWidths.Count; i++)
        {
            if (MathF.Abs(StrokeWidths[i] - width) < MathF.Abs(StrokeWidths[best] - width))
            {
                best = i;
            }
        }

        return best;
    }
}

internal readonly record struct ArrowShape(Vector2 Tip, Vector2 LeftWing, Vector2 RightWing, Vector2 Notch, Vector2 ShaftEnd, float HeadLength, float HeadHalfWidth);

internal static class ArrowGeometry
{
    /// <summary>Filled arrowhead whose size scales with the stroke width (and shrinks for very short arrows).</summary>
    public static ArrowShape Compute(Vector2 start, Vector2 end, float strokeWidth)
    {
        var d = end - start;
        var len = d.Length();
        var dir = len > 1e-4f ? d / len : Vector2.UnitX;
        var normal = new Vector2(-dir.Y, dir.X);

        var headLen = 10f + strokeWidth * 3.2f;
        var halfWidth = headLen * 0.52f;
        var maxHead = MathF.Max(len * 0.6f, 1f);
        if (headLen > maxHead)
        {
            var k = maxHead / headLen;
            headLen *= k;
            halfWidth *= k;
        }

        var back = end - dir * headLen;
        var notch = end - dir * (headLen * 0.78f);
        var shaftEnd = end - dir * (headLen * 0.70f);
        return new ArrowShape(end, back + normal * halfWidth, back - normal * halfWidth, notch, shaftEnd, headLen, halfWidth);
    }
}

internal static class PathSmoothing
{
    /// <summary>Ramer–Douglas–Peucker polyline simplification.</summary>
    public static List<Vector2> Simplify(IReadOnlyList<Vector2> points, float epsilon)
    {
        if (points.Count < 3)
        {
            return [.. points];
        }

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            var maxDist = 0f;
            var index = -1;
            for (var i = s + 1; i < e; i++)
            {
                var dist = GeometryMath.DistanceToSegment(points[i], points[s], points[e]);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    index = i;
                }
            }

            if (index >= 0 && maxDist > epsilon)
            {
                keep[index] = true;
                stack.Push((s, index));
                stack.Push((index, e));
            }
        }

        var result = new List<Vector2>();
        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Smooth curve through the points as quadratic Bézier segments (control = input point, end = midpoint to next).
    /// The first segment starts at points[0]; the last one ends exactly at the last point.
    /// </summary>
    public static IEnumerable<(Vector2 Control, Vector2 End)> QuadraticSegments(IReadOnlyList<Vector2> points)
    {
        if (points.Count < 2)
        {
            yield break;
        }

        if (points.Count == 2)
        {
            yield return ((points[0] + points[1]) / 2f, points[1]);
            yield break;
        }

        for (var i = 1; i < points.Count - 1; i++)
        {
            var mid = (points[i] + points[i + 1]) / 2f;
            var end = i == points.Count - 2 ? points[i + 1] : mid;
            yield return (points[i], end);
        }
    }
}

internal static class CropMath
{
    public const float MinCropSize = 4f;

    /// <summary>
    /// Normalizes a crop rectangle: positive size, clamped to the image, snapped to whole pixels.
    /// Returns null when the result is too small or covers the whole image (i.e. no crop).
    /// </summary>
    public static RectF? Normalize(RectF? crop, int imageWidth, int imageHeight)
    {
        if (crop is not { } c || imageWidth <= 0 || imageHeight <= 0)
        {
            return null;
        }

        var n = GeometryMath.ClampToImage(c, imageWidth, imageHeight);
        var l = MathF.Round(n.Left);
        var t = MathF.Round(n.Top);
        var r = MathF.Round(n.Right);
        var b = MathF.Round(n.Bottom);
        if (r - l < MinCropSize || b - t < MinCropSize)
        {
            return null;
        }

        if (l <= 0 && t <= 0 && r >= imageWidth && b >= imageHeight)
        {
            return null;
        }

        return RectF.FromLTRB(l, t, r, b);
    }

    /// <summary>Pixel rectangle that the output covers (the whole image when not cropped).</summary>
    public static PixelRect OutputRect(RectF? crop, int imageWidth, int imageHeight) =>
        Normalize(crop, imageWidth, imageHeight) is { } c
            ? PixelRect.FromRect(c, imageWidth, imageHeight)
            : new PixelRect(0, 0, imageWidth, imageHeight);
}

internal static class Pixelator
{
    /// <summary>
    /// Pixelates <paramref name="region"/> of a 32bpp image (any channel order) by averaging square blocks
    /// aligned to the region's top-left corner. Returns the region's pixels (Width×Height×4, tightly packed).
    /// </summary>
    public static byte[] Pixelate(ReadOnlySpan<byte> pixels, int imageWidth, int imageHeight, PixelRect region, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        if (pixels.Length < imageWidth * imageHeight * 4)
        {
            throw new ArgumentException("Pixel buffer is smaller than the image.", nameof(pixels));
        }

        var r = PixelRect.FromRect(region.ToRectF(), imageWidth, imageHeight);
        var output = new byte[Math.Max(0, r.Width * r.Height * 4)];
        if (r.IsEmpty)
        {
            return output;
        }

        var stride = imageWidth * 4;
        for (var by = 0; by < r.Height; by += blockSize)
        {
            var bh = Math.Min(blockSize, r.Height - by);
            for (var bx = 0; bx < r.Width; bx += blockSize)
            {
                var bw = Math.Min(blockSize, r.Width - bx);
                long s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (var y = 0; y < bh; y++)
                {
                    var row = (r.Y + by + y) * stride + (r.X + bx) * 4;
                    for (var x = 0; x < bw; x++)
                    {
                        var i = row + x * 4;
                        s0 += pixels[i];
                        s1 += pixels[i + 1];
                        s2 += pixels[i + 2];
                        s3 += pixels[i + 3];
                    }
                }

                var count = bw * bh;
                var c0 = (byte)((s0 + count / 2) / count);
                var c1 = (byte)((s1 + count / 2) / count);
                var c2 = (byte)((s2 + count / 2) / count);
                var c3 = (byte)((s3 + count / 2) / count);
                for (var y = 0; y < bh; y++)
                {
                    var row = ((by + y) * r.Width + bx) * 4;
                    for (var x = 0; x < bw; x++)
                    {
                        var o = row + x * 4;
                        output[o] = c0;
                        output[o + 1] = c1;
                        output[o + 2] = c2;
                        output[o + 3] = c3;
                    }
                }
            }
        }

        return output;
    }
}
