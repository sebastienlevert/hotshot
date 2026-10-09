using Hotshot.Interop;

namespace Hotshot.Capture;

/// <summary>A rectangle in physical (per-monitor DPI aware) virtual-screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public (int X, int Y) Center => (X + Width / 2, Y + Height / 2);

    public static PixelRect FromLTRB(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public static PixelRect FromPoints(int x1, int y1, int x2, int y2) =>
        FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    public static PixelRect FromRect(RECT r) => FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    public RECT ToRect() => new(Left, Top, Right, Bottom);

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        var l = Math.Max(Left, other.Left);
        var t = Math.Max(Top, other.Top);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r > l && b > t ? FromLTRB(l, t, r, b) : default;
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        return other.IsEmpty
            ? this
            : FromLTRB(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public PixelRect Inflate(int amount) => new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
