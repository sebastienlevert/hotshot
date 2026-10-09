using System.Numerics;
using System.Runtime.InteropServices;
using Hotshot.Interop;

namespace Hotshot.Capture;

/// <summary>A 32bpp top-down DIB section selected into its own memory DC.</summary>
public sealed unsafe class GdiBitmap : IDisposable
{
    private readonly nint _oldBitmap;
    private bool _disposed;

    public GdiBitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;

        var screen = Native.GetDC(0);
        try
        {
            Dc = Native.CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER
            {
                biSize = sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Native.BI_RGB,
            };
            Handle = Native.CreateDIBSection(screen, &header, Native.DIB_RGB_COLORS, out var bits, 0, 0);
            if (Handle == 0 || Dc == 0)
            {
                Native.DeleteDC(Dc);
                throw new InvalidOperationException($"Could not allocate a {width}x{height} bitmap.");
            }

            Bits = (byte*)bits;
            _oldBitmap = Native.SelectObject(Dc, Handle);
        }
        finally
        {
            Native.ReleaseDC(0, screen);
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public nint Dc { get; }
    public nint Handle { get; }
    public byte* Bits { get; }

    public Span<byte> Span => new(Bits, Stride * Height);

    /// <summary>Copies a sub-rectangle (in bitmap coordinates) into a managed, opaque BGRA image.</summary>
    public CapturedImage Copy(PixelRect area)
    {
        area = area.Intersect(new PixelRect(0, 0, Width, Height));
        if (area.IsEmpty)
        {
            throw new ArgumentException("The requested area is outside the captured image.", nameof(area));
        }

        Native.GdiFlush();
        var rowBytes = area.Width * 4;
        var pixels = new byte[rowBytes * area.Height];
        for (var y = 0; y < area.Height; y++)
        {
            new ReadOnlySpan<byte>(Bits + (area.Y + y) * (long)Stride + area.X * 4L, rowBytes)
                .CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
        }

        CapturedImage.ForceOpaque(pixels);
        return new CapturedImage(area.Width, area.Height, pixels);
    }

    /// <summary>Creates a darker copy of this bitmap (used as the overlay backdrop).</summary>
    public GdiBitmap CreateDimmedCopy(byte keep = 140)
    {
        Native.GdiFlush();
        var copy = new GdiBitmap(Width, Height);
        var src = Span;
        var dst = copy.Span;
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var factor = new Vector<ushort>(keep);
            var count = Vector<byte>.Count;
            for (; i + count <= src.Length; i += count)
            {
                var v = new Vector<byte>(src[i..]);
                Vector.Widen(v, out var lo, out var hi);
                lo = (lo * factor) >> 8;
                hi = (hi * factor) >> 8;
                Vector.Narrow(lo, hi).CopyTo(dst[i..]);
            }
        }

        for (; i < src.Length; i++)
        {
            dst[i] = (byte)(src[i] * keep >> 8);
        }

        return copy;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Native.SelectObject(Dc, _oldBitmap);
        Native.DeleteObject(Handle);
        Native.DeleteDC(Dc);
        GC.SuppressFinalize(this);
    }

    ~GdiBitmap()
    {
        Dispose();
    }
}

/// <summary>A frozen copy of (part of) the virtual screen.</summary>
public sealed class ScreenSnapshot : IDisposable
{
    private CURSORINFO _cursor;
    private bool _cursorDrawn;

    private ScreenSnapshot(PixelRect bounds, GdiBitmap bitmap, CURSORINFO cursor)
    {
        Bounds = bounds;
        Bitmap = bitmap;
        _cursor = cursor;
    }

    /// <summary>Screen area covered, in virtual-screen pixels.</summary>
    public PixelRect Bounds { get; }

    public GdiBitmap Bitmap { get; }

    /// <summary>Grabs the given screen area (default: the whole virtual screen) with GDI.</summary>
    public static ScreenSnapshot Take(PixelRect? area = null, bool includeCursor = false)
    {
        var bounds = area ?? Monitors.VirtualScreen;
        var bitmap = new GdiBitmap(bounds.Width, bounds.Height);
        var screen = Native.GetDC(0);
        try
        {
            Native.BitBlt(bitmap.Dc, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, Native.SRCCOPY | Native.CAPTUREBLT);
        }
        finally
        {
            Native.ReleaseDC(0, screen);
        }

        var cursor = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!Native.GetCursorInfo(ref cursor))
        {
            cursor = default;
        }

        var snapshot = new ScreenSnapshot(bounds, bitmap, cursor);
        if (includeCursor)
        {
            snapshot.DrawCursor();
        }

        return snapshot;
    }

    /// <summary>
    /// Burns the cursor (as it was when the snapshot was taken) into the image. Lets the overlay show a
    /// cursor-free frame while the final capture still includes the pointer.
    /// </summary>
    public void DrawCursor()
    {
        if (_cursorDrawn)
        {
            return;
        }

        _cursorDrawn = true;
        DrawCursor(Bitmap.Dc, Bounds, _cursor);
    }

    /// <summary>Crops a rectangle given in virtual-screen coordinates.</summary>
    public CapturedImage Crop(PixelRect screenRect) =>
        Bitmap.Copy(screenRect.Intersect(Bounds).Offset(-Bounds.X, -Bounds.Y));

    public CapturedImage ToImage() => Bitmap.Copy(new PixelRect(0, 0, Bitmap.Width, Bitmap.Height));

    public void Dispose() => Bitmap.Dispose();

    private static void DrawCursor(nint hdc, PixelRect bounds, CURSORINFO info)
    {
        if ((info.flags & Native.CURSOR_SHOWING) == 0 || info.hCursor == 0)
        {
            return;
        }

        if (!Native.GetIconInfo(info.hCursor, out var icon))
        {
            return;
        }

        try
        {
            Native.DrawIconEx(hdc, info.ptScreenPos.X - icon.xHotspot - bounds.X, info.ptScreenPos.Y - icon.yHotspot - bounds.Y,
                info.hCursor, 0, 0, 0, 0, Native.DI_NORMAL);
        }
        finally
        {
            if (icon.hbmMask != 0)
            {
                Native.DeleteObject(icon.hbmMask);
            }

            if (icon.hbmColor != 0)
            {
                Native.DeleteObject(icon.hbmColor);
            }
        }
    }
}
