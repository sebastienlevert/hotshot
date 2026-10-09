using System.Numerics;
using System.Runtime.InteropServices;

namespace Hotshot.Gif.Internal;

/// <summary>
/// High-quality separable area-averaging (box filter) downscaler for BGRA32 images. Each output pixel is the exact
/// coverage-weighted mean of the source pixels it covers, which avoids aliasing on text and UI edges. Weights are
/// precomputed in 12-bit fixed point; rows are processed in parallel with no per-pixel allocations.
/// </summary>
internal sealed unsafe class ImageScaler
{
    private const int WeightBits = 12;
    private const int WeightOne = 1 << WeightBits;

    private readonly int _srcWidth;
    private readonly int _srcHeight;
    private readonly Taps _horizontal;
    private readonly Taps _vertical;

    public ImageScaler(int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(srcWidth), "Image dimensions must be positive.");
        }

        _srcWidth = srcWidth;
        _srcHeight = srcHeight;
        DstWidth = dstWidth;
        DstHeight = dstHeight;
        _horizontal = new Taps(srcWidth, dstWidth);
        _vertical = new Taps(srcHeight, dstHeight);
    }

    public int DstWidth { get; }

    public int DstHeight { get; }

    public void Scale(ReadOnlySpan<byte> src, int srcStride, Span<byte> dst)
    {
        if (srcStride < _srcWidth * 4 || src.Length < ((long)srcStride * (_srcHeight - 1)) + (_srcWidth * 4))
        {
            throw new ArgumentException("Source buffer is too small.", nameof(src));
        }

        if (dst.Length < DstWidth * DstHeight * 4)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(dst));
        }

        fixed (byte* s = src)
        fixed (byte* d = dst)
        {
            Scale((nint)s, srcStride, (nint)d);
        }
    }

    /// <summary>
    /// Scales from <paramref name="srcTopLeft"/> (first pixel of the top row) using a signed row pitch (negative for
    /// bottom-up memory layouts) into a tightly packed BGRA destination with alpha forced to 255.
    /// </summary>
    public void Scale(nint srcTopLeft, int srcPitch, nint dst)
    {
        int dstWidth = DstWidth;
        if (_srcWidth == dstWidth && _srcHeight == DstHeight)
        {
            Parallel.For(0, DstHeight, y =>
            {
                var srcRow = new ReadOnlySpan<uint>((void*)(srcTopLeft + ((nint)y * srcPitch)), dstWidth);
                var dstRow = new Span<uint>((void*)(dst + ((nint)y * dstWidth * 4)), dstWidth);
                CopyOpaque(srcRow, dstRow);
            });
            return;
        }

        Taps h = _horizontal, v = _vertical;
        int chunk = Math.Max(1, DstHeight / (Environment.ProcessorCount * 4));
        int chunks = (DstHeight + chunk - 1) / chunk;
        Parallel.For(0, chunks, () => (new int[dstWidth * 3], new int[dstWidth * 3]), (ci, _, buffers) =>
        {
            var (acc, rowTmp) = buffers;
            int yEnd = Math.Min(DstHeight, (ci + 1) * chunk);
            for (int oy = ci * chunk; oy < yEnd; oy++)
            {
                Array.Clear(acc);
                int vStart = v.Start[oy], vCount = v.Count[oy], vOffset = v.Offset[oy];
                for (int t = 0; t < vCount; t++)
                {
                    int wy = v.Weights[vOffset + t];
                    byte* row = (byte*)(srcTopLeft + ((nint)(vStart + t) * srcPitch));
                    HorizontalPass(row, h, rowTmp);
                    for (int i = 0; i < acc.Length; i++)
                    {
                        acc[i] += rowTmp[i] * wy;
                    }
                }

                byte* outRow = (byte*)(dst + ((nint)oy * dstWidth * 4));
                for (int ox = 0, i = 0; ox < dstWidth; ox++, i += 3)
                {
                    outRow[(ox * 4) + 0] = (byte)Math.Min(255, (acc[i] + (1 << 19)) >> 20);
                    outRow[(ox * 4) + 1] = (byte)Math.Min(255, (acc[i + 1] + (1 << 19)) >> 20);
                    outRow[(ox * 4) + 2] = (byte)Math.Min(255, (acc[i + 2] + (1 << 19)) >> 20);
                    outRow[(ox * 4) + 3] = 255;
                }
            }

            return buffers;
        }, static _ => { });
    }

    // Result per channel is the 12-bit weighted sum scaled down by 4 bits (fits 16 bits), so the vertical
    // pass (another 12-bit weight) stays within int range.
    private static void HorizontalPass(byte* row, Taps h, int[] output)
    {
        int[] starts = h.Start, counts = h.Count, offsets = h.Offset, weights = h.Weights;
        for (int ox = 0, o = 0; ox < starts.Length; ox++, o += 3)
        {
            byte* p = row + (starts[ox] * 4);
            int n = counts[ox], wo = offsets[ox];
            int b = 0, g = 0, r = 0;
            for (int t = 0; t < n; t++, p += 4)
            {
                int w = weights[wo + t];
                b += p[0] * w;
                g += p[1] * w;
                r += p[2] * w;
            }

            output[o] = (b + 8) >> 4;
            output[o + 1] = (g + 8) >> 4;
            output[o + 2] = (r + 8) >> 4;
        }
    }

    private static void CopyOpaque(ReadOnlySpan<uint> src, Span<uint> dst)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && src.Length >= Vector<uint>.Count)
        {
            var alpha = new Vector<uint>(0xFF000000u);
            var srcV = MemoryMarshal.Cast<uint, Vector<uint>>(src);
            var dstV = MemoryMarshal.Cast<uint, Vector<uint>>(dst);
            for (int k = 0; k < srcV.Length; k++)
            {
                dstV[k] = srcV[k] | alpha;
            }

            i = srcV.Length * Vector<uint>.Count;
        }

        for (; i < src.Length; i++)
        {
            dst[i] = src[i] | 0xFF000000u;
        }
    }

    /// <summary>Area-coverage filter taps for one axis; weights of each output sample sum to exactly 4096.</summary>
    private sealed class Taps
    {
        public readonly int[] Start;
        public readonly int[] Count;
        public readonly int[] Offset;
        public readonly int[] Weights;

        public Taps(int srcSize, int dstSize)
        {
            Start = new int[dstSize];
            Count = new int[dstSize];
            Offset = new int[dstSize];
            var weights = new List<int>(dstSize * 4);
            double scale = (double)srcSize / dstSize;
            Span<double> cover = stackalloc double[(int)Math.Ceiling(scale) + 2];
            for (int o = 0; o < dstSize; o++)
            {
                double start = o * scale, end = Math.Min(srcSize, (o + 1) * scale);
                int first = Math.Min(srcSize - 1, (int)Math.Floor(start));
                int last = Math.Max(first, Math.Min(srcSize - 1, (int)Math.Ceiling(end) - 1));
                int n = last - first + 1;
                double total = 0;
                for (int j = 0; j < n; j++)
                {
                    int s = first + j;
                    cover[j] = Math.Max(0, Math.Min(end, s + 1) - Math.Max(start, s));
                    total += cover[j];
                }

                // Drop negligible edge taps (floating point slivers).
                while (n > 1 && cover[n - 1] < total * 1e-6)
                {
                    total -= cover[--n];
                }

                int skip = 0;
                while (n - skip > 1 && cover[skip] < total * 1e-6)
                {
                    total -= cover[skip++];
                }

                Start[o] = first + skip;
                Count[o] = n - skip;
                Offset[o] = weights.Count;
                int sum = 0, largest = -1, largestIndex = 0;
                for (int j = skip; j < n; j++)
                {
                    int w = (int)Math.Round(cover[j] / total * WeightOne);
                    weights.Add(w);
                    sum += w;
                    if (w > largest)
                    {
                        largest = w;
                        largestIndex = weights.Count - 1;
                    }
                }

                weights[largestIndex] += WeightOne - sum;
            }

            Weights = weights.ToArray();
        }
    }
}
