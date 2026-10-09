using System.Numerics;
using System.Runtime.InteropServices;

namespace Hotshot.Capture;

/// <summary>An opaque BGRA8 image (alpha is always 255 for captures).</summary>
public sealed class CapturedImage
{
    public CapturedImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (pixels.Length < width * height * 4)
        {
            throw new ArgumentException("Pixel buffer is too small.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Pixels { get; }

    /// <summary>Area-averaging downscale that fits the image inside <paramref name="maxSize"/>.</summary>
    public CapturedImage Downscale(int maxSize)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxSize / Width, (double)maxSize / Height));
        if (scale >= 1.0)
        {
            return this;
        }

        var w = Math.Max(1, (int)Math.Round(Width * scale));
        var h = Math.Max(1, (int)Math.Round(Height * scale));
        var dst = new byte[w * h * 4];
        var src = Pixels;
        var stride = Stride;

        for (var y = 0; y < h; y++)
        {
            var sy0 = y * Height / h;
            var sy1 = Math.Max(sy0 + 1, (y + 1) * Height / h);
            for (var x = 0; x < w; x++)
            {
                var sx0 = x * Width / w;
                var sx1 = Math.Max(sx0 + 1, (x + 1) * Width / w);
                long b = 0, g = 0, r = 0, a = 0;
                var count = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var row = sy * stride;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var i = row + sx * 4;
                        b += src[i];
                        g += src[i + 1];
                        r += src[i + 2];
                        a += src[i + 3];
                        count++;
                    }
                }

                var o = (y * w + x) * 4;
                dst[o] = (byte)(b / count);
                dst[o + 1] = (byte)(g / count);
                dst[o + 2] = (byte)(r / count);
                dst[o + 3] = (byte)(a / count);
            }
        }

        return new CapturedImage(w, h, dst);
    }

    internal static void ForceOpaque(Span<byte> bgra)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(bgra);
        var i = 0;
        if (Vector.IsHardwareAccelerated && pixels.Length >= Vector<uint>.Count)
        {
            var mask = new Vector<uint>(0xFF000000);
            var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(pixels);
            for (var v = 0; v < vectors.Length; v++)
            {
                vectors[v] |= mask;
            }

            i = vectors.Length * Vector<uint>.Count;
        }

        for (; i < pixels.Length; i++)
        {
            pixels[i] |= 0xFF000000;
        }
    }
}
