using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Hotshot.Interop;

namespace Hotshot.Capture;

/// <summary>A 32bpp top-down DIB section selected into its own memory DC.</summary>
public sealed unsafe class GdiBitmap : IDisposable
{
    private readonly BitmapDcHandle _resource;

    public GdiBitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        _ = checked(checked(width * 4) * height);

        using var screen = new ScreenDcHandle();
        _resource = new BitmapDcHandle(screen.DangerousGetHandle());
        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Native.BI_RGB,
            };
            _resource.CreateBitmap(screen.DangerousGetHandle(), &header);
        }
        catch
        {
            _resource.Dispose();
            throw;
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public nint Dc { get { ThrowIfDisposed(); return _resource.DangerousGetHandle(); } }
    public nint Handle { get { ThrowIfDisposed(); return _resource.Bitmap; } }
    public byte* Bits { get { ThrowIfDisposed(); return _resource.Bits; } }

    public Span<byte> Span => new(Bits, checked(Stride * Height));

    /// <summary>Copies a sub-rectangle (in bitmap coordinates) into a managed, opaque BGRA image.</summary>
    public CapturedImage Copy(PixelRect area)
    {
        area = area.Intersect(new PixelRect(0, 0, Width, Height));
        if (area.IsEmpty)
        {
            throw new ArgumentException("The requested area is outside the captured image.", nameof(area));
        }

        if (!Native.GdiFlush()) throw new InvalidOperationException("GDI could not finish copying the capture pixels.");
        var rowBytes = area.Width * 4;
        var pixels = new byte[rowBytes * area.Height];
        for (var y = 0; y < area.Height; y++)
        {
            new ReadOnlySpan<byte>(Bits + (area.Y + y) * (long)Stride + area.X * 4L, rowBytes)
                .CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
        }

        CapturedImage.ForceOpaque(pixels);
        GC.KeepAlive(_resource);
        return new CapturedImage(area.Width, area.Height, pixels);
    }

    /// <summary>Creates a darker copy of this bitmap (used as the overlay backdrop).</summary>
    public GdiBitmap CreateDimmedCopy(byte keep = 140)
    {
        if (!Native.GdiFlush()) throw new InvalidOperationException("GDI could not finish preparing the capture backdrop.");
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

    public void Dispose() => _resource.Dispose();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_resource.IsClosed, this);

    private sealed class BitmapDcHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private nint _previous;
        internal nint Bitmap { get; private set; }
        internal byte* Bits { get; private set; }

        internal BitmapDcHandle(nint screen) : base(ownsHandle: true)
        {
            SetHandle(Native.CreateCompatibleDC(screen));
            if (IsInvalid) throw new InvalidOperationException("GDI could not allocate a capture device context.");
        }

        internal void CreateBitmap(nint screen, BITMAPINFOHEADER* header)
        {
            Bitmap = Native.CreateDIBSection(screen, header, Native.DIB_RGB_COLORS, out var bits, 0, 0);
            if (Bitmap == 0) throw new InvalidOperationException("GDI could not allocate a capture bitmap.");
            _previous = Native.SelectObject(handle, Bitmap);
            if (_previous == 0 || _previous == -1)
                throw new InvalidOperationException("GDI could not select the capture bitmap.");
            Bits = (byte*)bits;
        }

        protected override bool ReleaseHandle()
        {
            if (_previous != 0 && _previous != -1) Native.SelectObject(handle, _previous);
            var released = Bitmap == 0 || Native.DeleteObject(Bitmap);
            released = Native.DeleteDC(handle) && released;
            if (!released) System.Diagnostics.Trace.TraceWarning("GDI could not release a capture bitmap/device context.");
            return released;
        }
    }
}

internal sealed class ScreenDcHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ScreenDcHandle() : base(ownsHandle: true)
    {
        SetHandle(Native.GetDC(0));
        if (IsInvalid) throw new InvalidOperationException("The screen device context is unavailable.");
    }

    protected override bool ReleaseHandle()
    {
        var released = Native.ReleaseDC(0, handle) == 1;
        if (!released) System.Diagnostics.Trace.TraceWarning("GDI could not release the screen device context.");
        return released;
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
        if (bounds.IsEmpty || (long)bounds.Width * bounds.Height > 64_000_000)
            throw new ArgumentException("Capture bounds must be non-empty and at most 64 megapixels.");
        using var screen = new ScreenDcHandle();
        var bitmap = new GdiBitmap(bounds.Width, bounds.Height);
        try
        {
            if (!Native.BitBlt(bitmap.Dc, 0, 0, bounds.Width, bounds.Height, screen.DangerousGetHandle(), bounds.X, bounds.Y, Native.SRCCOPY | Native.CAPTUREBLT))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "The screen pixels could not be captured.");
        }
        catch
        {
            bitmap.Dispose();
            throw;
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
