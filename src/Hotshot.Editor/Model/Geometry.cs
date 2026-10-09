using System.Numerics;

namespace Hotshot.Editor.Model;

/// <summary>Axis-aligned rectangle in image pixel space (float precision).</summary>
internal readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public Vector2 TopLeft => new(X, Y);
    public Vector2 BottomRight => new(Right, Bottom);
    public Vector2 Center => new(X + Width / 2f, Y + Height / 2f);
    public Vector2 Size => new(Width, Height);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectF FromLTRB(float left, float top, float right, float bottom) =>
        new(MathF.Min(left, right), MathF.Min(top, bottom), MathF.Abs(right - left), MathF.Abs(bottom - top));

    public static RectF FromPoints(Vector2 a, Vector2 b) => FromLTRB(a.X, a.Y, b.X, b.Y);

    public static RectF FromPoints(IEnumerable<Vector2> points)
    {
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        var any = false;
        foreach (var p in points)
        {
            any = true;
            l = MathF.Min(l, p.X);
            t = MathF.Min(t, p.Y);
            r = MathF.Max(r, p.X);
            b = MathF.Max(b, p.Y);
        }

        return any ? FromLTRB(l, t, r, b) : default;
    }

    public RectF Normalize() => FromLTRB(Left, Top, Right, Bottom);

    public bool Contains(Vector2 p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public RectF Inflate(float amount) => new(X - amount, Y - amount, Width + 2 * amount, Height + 2 * amount);

    public RectF Inflate(float dx, float dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    public RectF Offset(Vector2 delta) => new(X + delta.X, Y + delta.Y, Width, Height);

    public RectF Union(RectF other) => FromLTRB(
        MathF.Min(Left, other.Left), MathF.Min(Top, other.Top),
        MathF.Max(Right, other.Right), MathF.Max(Bottom, other.Bottom));

    public RectF Intersect(RectF other)
    {
        var l = MathF.Max(Left, other.Left);
        var t = MathF.Max(Top, other.Top);
        var r = MathF.Min(Right, other.Right);
        var b = MathF.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? new RectF(l, t, 0, 0) : new RectF(l, t, r - l, b - t);
    }

    public bool IntersectsWith(RectF other) =>
        other.Left < Right && Left < other.Right && other.Top < Bottom && Top < other.Bottom;
}

/// <summary>Integer pixel rectangle.</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public RectF ToRectF() => new(X, Y, Width, Height);

    /// <summary>Smallest pixel rect covering <paramref name="rect"/>, clamped to the image.</summary>
    public static PixelRect FromRect(RectF rect, int imageWidth, int imageHeight)
    {
        const float eps = 1e-3f;
        var n = rect.Normalize();
        var l = Math.Clamp((int)MathF.Floor(n.Left + eps), 0, imageWidth);
        var t = Math.Clamp((int)MathF.Floor(n.Top + eps), 0, imageHeight);
        var r = Math.Clamp((int)MathF.Ceiling(n.Right - eps), 0, imageWidth);
        var b = Math.Clamp((int)MathF.Ceiling(n.Bottom - eps), 0, imageHeight);
        return new PixelRect(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
    }
}

internal enum HandleKind
{
    None,
    Start,
    End,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

internal readonly record struct AnnotationHandle(HandleKind Kind, Vector2 Position);

internal static class GeometryMath
{
    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared();
        if (len2 < 1e-8f)
        {
            return Vector2.Distance(p, a);
        }

        var t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    public static float DistanceToPolyline(Vector2 p, IReadOnlyList<Vector2> points)
    {
        if (points.Count == 0)
        {
            return float.MaxValue;
        }

        if (points.Count == 1)
        {
            return Vector2.Distance(p, points[0]);
        }

        var best = float.MaxValue;
        for (var i = 1; i < points.Count; i++)
        {
            best = MathF.Min(best, DistanceToSegment(p, points[i - 1], points[i]));
        }

        return best;
    }

    /// <summary>Snaps the direction origin→p to multiples of <paramref name="stepDegrees"/>, projecting the length.</summary>
    public static Vector2 SnapAngle(Vector2 origin, Vector2 p, float stepDegrees = 45f)
    {
        var d = p - origin;
        var len = d.Length();
        if (len < 1e-4f)
        {
            return p;
        }

        var angle = MathF.Atan2(d.Y, d.X);
        var step = stepDegrees * MathF.PI / 180f;
        var snapped = MathF.Round(angle / step) * step;
        var projected = len * MathF.Cos(angle - snapped);
        var dir = new Vector2(MathF.Cos(snapped), MathF.Sin(snapped));
        // Kill floating point noise so horizontal/vertical snaps are exact.
        dir = new Vector2(MathF.Abs(dir.X) < 1e-5f ? 0 : dir.X, MathF.Abs(dir.Y) < 1e-5f ? 0 : dir.Y);
        return origin + dir * projected;
    }

    /// <summary>Moves <paramref name="p"/> so that anchor→p spans a square (keeps the drag direction).</summary>
    public static Vector2 ConstrainSquare(Vector2 anchor, Vector2 p)
    {
        var d = p - anchor;
        var size = MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y));
        return anchor + new Vector2(d.X < 0 ? -size : size, d.Y < 0 ? -size : size);
    }

    public static IReadOnlyList<AnnotationHandle> RectHandles(RectF r) =>
    [
        new(HandleKind.TopLeft, new Vector2(r.Left, r.Top)),
        new(HandleKind.Top, new Vector2(r.Center.X, r.Top)),
        new(HandleKind.TopRight, new Vector2(r.Right, r.Top)),
        new(HandleKind.Right, new Vector2(r.Right, r.Center.Y)),
        new(HandleKind.BottomRight, new Vector2(r.Right, r.Bottom)),
        new(HandleKind.Bottom, new Vector2(r.Center.X, r.Bottom)),
        new(HandleKind.BottomLeft, new Vector2(r.Left, r.Bottom)),
        new(HandleKind.Left, new Vector2(r.Left, r.Center.Y)),
    ];

    /// <summary>Resizes <paramref name="original"/> by dragging <paramref name="handle"/> to <paramref name="p"/>. Result is normalized.</summary>
    public static RectF ResizeRect(RectF original, HandleKind handle, Vector2 p, bool square)
    {
        var o = original.Normalize();
        if (square)
        {
            switch (handle)
            {
                case HandleKind.TopLeft:
                    return RectF.FromPoints(o.BottomRight, ConstrainSquare(o.BottomRight, p));
                case HandleKind.TopRight:
                    return RectF.FromPoints(new Vector2(o.Left, o.Bottom), ConstrainSquare(new Vector2(o.Left, o.Bottom), p));
                case HandleKind.BottomRight:
                    return RectF.FromPoints(o.TopLeft, ConstrainSquare(o.TopLeft, p));
                case HandleKind.BottomLeft:
                    return RectF.FromPoints(new Vector2(o.Right, o.Top), ConstrainSquare(new Vector2(o.Right, o.Top), p));
                case HandleKind.Top:
                case HandleKind.Bottom:
                {
                    var h = handle == HandleKind.Top ? MathF.Abs(o.Bottom - p.Y) : MathF.Abs(p.Y - o.Top);
                    var cx = o.Center.X;
                    return handle == HandleKind.Top
                        ? RectF.FromLTRB(cx - h / 2, p.Y, cx + h / 2, o.Bottom)
                        : RectF.FromLTRB(cx - h / 2, o.Top, cx + h / 2, p.Y);
                }
                case HandleKind.Left:
                case HandleKind.Right:
                {
                    var w = handle == HandleKind.Left ? MathF.Abs(o.Right - p.X) : MathF.Abs(p.X - o.Left);
                    var cy = o.Center.Y;
                    return handle == HandleKind.Left
                        ? RectF.FromLTRB(p.X, cy - w / 2, o.Right, cy + w / 2)
                        : RectF.FromLTRB(o.Left, cy - w / 2, p.X, cy + w / 2);
                }
            }
        }

        float l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;
        switch (handle)
        {
            case HandleKind.TopLeft: l = p.X; t = p.Y; break;
            case HandleKind.Top: t = p.Y; break;
            case HandleKind.TopRight: r = p.X; t = p.Y; break;
            case HandleKind.Right: r = p.X; break;
            case HandleKind.BottomRight: r = p.X; b = p.Y; break;
            case HandleKind.Bottom: b = p.Y; break;
            case HandleKind.BottomLeft: l = p.X; b = p.Y; break;
            case HandleKind.Left: l = p.X; break;
        }

        return RectF.FromLTRB(l, t, r, b);
    }

    /// <summary>True when p lies on the stroked outline of r (within halfWidth).</summary>
    public static bool HitRectOutline(RectF r, Vector2 p, float halfWidth)
    {
        var outer = r.Inflate(halfWidth);
        if (!outer.Contains(p))
        {
            return false;
        }

        var inner = r.Inflate(-halfWidth);
        return inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p);
    }

    public static bool HitEllipse(RectF r, Vector2 p, float halfWidth, bool filled)
    {
        var rx = r.Width / 2f;
        var ry = r.Height / 2f;
        var d = p - r.Center;
        static float Norm(Vector2 d, float a, float b) => (d.X * d.X) / (a * a) + (d.Y * d.Y) / (b * b);

        var outerA = rx + halfWidth;
        var outerB = ry + halfWidth;
        if (outerA <= 0 || outerB <= 0 || Norm(d, outerA, outerB) > 1f)
        {
            return false;
        }

        if (filled)
        {
            return true;
        }

        var innerA = rx - halfWidth;
        var innerB = ry - halfWidth;
        return innerA <= 0 || innerB <= 0 || Norm(d, innerA, innerB) >= 1f;
    }

    /// <summary>Clamps a rectangle so it stays inside [0,w]x[0,h], keeping its size when possible (for moves).</summary>
    public static RectF KeepInside(RectF r, int width, int height)
    {
        var x = Math.Clamp(r.X, 0, MathF.Max(0, width - r.Width));
        var y = Math.Clamp(r.Y, 0, MathF.Max(0, height - r.Height));
        return new RectF(x, y, MathF.Min(r.Width, width), MathF.Min(r.Height, height));
    }

    /// <summary>Clips a rectangle to the image bounds.</summary>
    public static RectF ClampToImage(RectF r, int width, int height)
    {
        var n = r.Normalize();
        var l = Math.Clamp(n.Left, 0, width);
        var t = Math.Clamp(n.Top, 0, height);
        var rr = Math.Clamp(n.Right, 0, width);
        var b = Math.Clamp(n.Bottom, 0, height);
        return RectF.FromLTRB(l, t, rr, b);
    }
}
