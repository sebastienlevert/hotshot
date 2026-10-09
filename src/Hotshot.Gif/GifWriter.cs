using System.Numerics;
using System.Runtime.InteropServices;
using Hotshot.Gif.Internal;

namespace Hotshot.Gif;

/// <summary>
/// Streaming GIF89a encoder. Frames are BGRA32, top-down; alpha is ignored (output is opaque).
/// <para>
/// The first frame's palette becomes the global color table. Later frames reuse the active palette while it represents
/// their changed pixels well and otherwise get a rebuilt local color table. With <see cref="GifOptions.OptimizeFrames"/>,
/// pixels that did not change (within a small tolerance that absorbs video compression noise) become transparent, each
/// frame is cropped to its changed bounds and frames without visible changes are merged into the previous delay.
/// </para>
/// Delays are rounded to centiseconds with error accumulation so the total matches the sum of the given delays.
/// Instances are not thread-safe.
/// </summary>
public sealed class GifWriter : IDisposable
{
    // Per-channel difference treated as "unchanged" when optimizing (H.264 noise; far below the quantization error).
    private const int ChangeTolerance = 4;
    private const double MinErrorLimit = 48;
    private const int ChangedPixelWeight = 3;

    private readonly Stream _output;
    private readonly bool _optimize;
    private readonly bool _useTransparency;
    private readonly bool _dither;
    private readonly int _maxColors;
    private readonly int _loopCount;
    private readonly bool _loop;
    private readonly uint[] _frame;
    private readonly uint[] _ref;
    private readonly uint[] _composed;
    private readonly byte[] _indices;
    private readonly LzwEncoder _lzw = new();
    private readonly GifByteBuffer _headerBuffer = new(1024);
    private GifByteBuffer _pendingData = new();
    private GifByteBuffer _scratchData = new();
    private Palette? _global;
    private Palette? _active;
    private PendingFrame _pending;
    private bool _hasPending;
    private bool _headerWritten;
    private long _exactTicks;
    private long _writtenCentiseconds;
    private int _framesWritten;
    private bool _disposed;

    public GifWriter(Stream output, int width, int height, GifOptions options)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        if (!output.CanWrite)
        {
            throw new ArgumentException("Stream must be writable.", nameof(output));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);
        options.ValidateForWriter();

        _output = output;
        Width = width;
        Height = height;
        _optimize = options.OptimizeFrames;
        _useTransparency = options.OptimizeFrames && options.MaxColors > 2;
        _dither = options.Dither;
        _maxColors = options.MaxColors;
        _loopCount = options.LoopCount;
        _loop = options.Loop;
        int pixels = checked(width * height);
        _frame = new uint[pixels];
        _ref = new uint[pixels];
        _composed = new uint[pixels];
        _indices = new byte[pixels];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Number of GIF frames produced so far (after merging unchanged frames).</summary>
    public int FrameCount => _framesWritten + (_hasPending ? 1 : 0);

    /// <summary>Total delay written so far, in centiseconds (final after <see cref="Dispose"/>).</summary>
    internal long TotalCentiseconds => _writtenCentiseconds;

    /// <summary>Number of local color tables emitted (diagnostics).</summary>
    internal int LocalPaletteCount { get; private set; }

    /// <summary>Adds a BGRA32 top-down frame displayed for <paramref name="delay"/>.</summary>
    public void AddFrame(ReadOnlySpan<byte> bgra, int stride, TimeSpan delay)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stride < Width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "Stride must be at least width * 4.");
        }

        if (bgra.Length < ((long)stride * (Height - 1)) + (Width * 4))
        {
            throw new ArgumentException("Pixel buffer is too small for the frame size and stride.", nameof(bgra));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        LoadFrame(bgra, stride);
        long ticks = delay.Ticks;
        if (_global is null)
        {
            AddFirstFrame(ticks);
        }
        else if (_optimize)
        {
            AddDeltaFrame(ticks);
        }
        else
        {
            AddFullFrame(ticks);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hasPending)
        {
            WritePending();
        }

        if (!_headerWritten)
        {
            WriteHeader(new Palette([0u, 0xFFFFFFu], false, 0));
        }

        _output.WriteByte(0x3B);
        _output.Flush();
    }

    private void LoadFrame(ReadOnlySpan<byte> bgra, int stride)
    {
        int w = Width;
        var mask = new Vector<uint>(0xFFFFFFu);
        for (int y = 0; y < Height; y++)
        {
            ReadOnlySpan<uint> src = MemoryMarshal.Cast<byte, uint>(bgra.Slice(y * stride, w * 4));
            Span<uint> dst = _frame.AsSpan(y * w, w);
            int x = 0;
            if (Vector.IsHardwareAccelerated)
            {
                for (; x <= w - Vector<uint>.Count; x += Vector<uint>.Count)
                {
                    (new Vector<uint>(src.Slice(x)) & mask).CopyTo(dst.Slice(x));
                }
            }

            for (; x < w; x++)
            {
                dst[x] = src[x] & 0xFFFFFFu;
            }
        }
    }

    private void AddFirstFrame(long ticks)
    {
        var histogram = new ColorHistogram();
        histogram.AddPixels(_frame);
        Palette palette = histogram.Build(_maxColors, _useTransparency);
        _global = _active = palette;
        WriteHeader(palette);

        int w = Width;
        for (int y = 0; y < Height; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                uint c = _frame[row + x];
                int idx = palette.Map(c, x, y, _dither);
                _indices[row + x] = (byte)idx;
                _composed[row + x] = palette[idx];
            }
        }

        _frame.CopyTo(_ref, 0);
        QueueFrame(new Rect(0, 0, w, Height), palette, transparent: false, ticks);
    }

    private void AddFullFrame(long ticks)
    {
        var all = new Rect(0, 0, Width, Height);
        Palette palette = ChoosePalette(all, onlyChanged: false);
        int w = Width;
        for (int y = 0; y < Height; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                _indices[row + x] = (byte)palette.Map(_frame[row + x], x, y, _dither);
            }
        }

        QueueFrame(all, palette, transparent: false, ticks);
    }

    private void AddDeltaFrame(long ticks)
    {
        if (!FindChangedBounds(out Rect changed))
        {
            _pending.Ticks += ticks;
            return;
        }

        Palette palette = ChoosePalette(changed, onlyChanged: true);
        int transparent = _useTransparency ? palette.TransparentIndex : -1;
        int w = Width;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = changed.Y; y < changed.Bottom; y++)
        {
            int row = y * w;
            int rowMin = int.MaxValue, rowMax = -1;
            for (int x = changed.X; x < changed.Right; x++)
            {
                int i = row + x;
                uint c = _frame[i];
                if (!Differs(c, _ref[i]))
                {
                    if (transparent >= 0)
                    {
                        _indices[i] = (byte)transparent;
                        continue;
                    }

                    c = _composed[i];
                    int keep = palette.Nearest(c);
                    _indices[i] = (byte)keep;
                    if (palette[keep] != c)
                    {
                        _composed[i] = palette[keep];
                        rowMin = Math.Min(rowMin, x);
                        rowMax = x;
                    }

                    continue;
                }

                int idx = palette.Map(c, x, y, _dither);
                uint color = palette[idx];
                _ref[i] = c;
                if (color == _composed[i])
                {
                    _indices[i] = (byte)(transparent >= 0 ? transparent : idx);
                    continue;
                }

                _indices[i] = (byte)idx;
                _composed[i] = color;
                rowMin = Math.Min(rowMin, x);
                rowMax = x;
            }

            if (rowMax >= 0)
            {
                minX = Math.Min(minX, rowMin);
                maxX = Math.Max(maxX, rowMax);
                minY = Math.Min(minY, y);
                maxY = y;
            }
        }

        if (maxX < 0)
        {
            _pending.Ticks += ticks;
            return;
        }

        // The crop lies inside the changed bounds, so every index in it was assigned above.
        var rect = new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        QueueFrame(rect, palette, transparent: transparent >= 0, ticks);
    }

    /// <summary>Picks the palette for the next frame: active, global, or a rebuilt local palette when colors shifted.</summary>
    private Palette ChoosePalette(Rect area, bool onlyChanged)
    {
        Palette active = _active!;
        double error = MeanError(active, area, onlyChanged);
        Palette global = _global!;
        if (!ReferenceEquals(active, global))
        {
            double globalError = MeanError(global, area, onlyChanged);
            if (globalError <= Limit(global) && globalError < error)
            {
                _active = global;
                return global;
            }

            if (globalError < error)
            {
                active = global;
                error = globalError;
            }
        }

        if (error <= Limit(active))
        {
            return active;
        }

        if (active.RejectedError > 0 && error <= active.RejectedError * 1.5)
        {
            return active;
        }

        var histogram = new ColorHistogram();
        if (onlyChanged && _useTransparency)
        {
            AddChangedPixels(histogram, area);
        }
        else
        {
            histogram.AddPixels(_frame);
        }

        Palette rebuilt = histogram.Build(_maxColors, _useTransparency);
        double rebuiltError = MeanError(rebuilt, area, onlyChanged);
        if (rebuiltError < error * 0.75)
        {
            _active = rebuilt;
            return rebuilt;
        }

        active.RejectedError = error;
        _active = active;
        return active;
    }

    private static double Limit(Palette palette) => Math.Max(MinErrorLimit, (2 * palette.BaselineError) + 32);

    private void AddChangedPixels(ColorHistogram histogram, Rect area)
    {
        int w = Width;
        for (int y = area.Y; y < area.Bottom; y++)
        {
            int row = y * w;
            for (int x = area.X; x < area.Right; x++)
            {
                uint c = _frame[row + x];
                if (Differs(c, _ref[row + x]))
                {
                    histogram.Add(c, ChangedPixelWeight);
                }
            }
        }
    }

    private double MeanError(Palette palette, Rect area, bool onlyChanged)
    {
        int w = Width;
        long sum = 0, count = 0;
        int step = onlyChanged ? 1 : 2;
        for (int y = area.Y; y < area.Bottom; y += step)
        {
            int row = y * w;
            for (int x = area.X; x < area.Right; x += step)
            {
                uint c = _frame[row + x];
                if (onlyChanged && !Differs(c, _ref[row + x]))
                {
                    continue;
                }

                sum += Palette.DistanceSquared(c, palette[palette.Nearest(c)]);
                count++;
            }
        }

        return count == 0 ? 0 : (double)sum / count;
    }

    private static bool Differs(uint a, uint b)
    {
        int dr = (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
        int dg = (int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF);
        int db = (int)(a & 0xFF) - (int)(b & 0xFF);
        return Math.Abs(dr) > ChangeTolerance || Math.Abs(dg) > ChangeTolerance || Math.Abs(db) > ChangeTolerance;
    }

    private bool FindChangedBounds(out Rect bounds)
    {
        int w = Width;
        int minX = int.MaxValue, minY = -1, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
        {
            ReadOnlySpan<uint> a = _frame.AsSpan(y * w, w);
            ReadOnlySpan<uint> b = _ref.AsSpan(y * w, w);
            int first = FirstChanged(a, b);
            if (first < 0)
            {
                continue;
            }

            int last = LastChanged(a, b);
            minX = Math.Min(minX, first);
            maxX = Math.Max(maxX, last);
            if (minY < 0)
            {
                minY = y;
            }

            maxY = y;
        }

        if (maxY < 0)
        {
            bounds = default;
            return false;
        }

        bounds = new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return true;
    }

    private static int FirstChanged(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var tol = new Vector<byte>((byte)ChangeTolerance);
            int n = Vector<uint>.Count;
            for (; x <= a.Length - n; x += n)
            {
                if (ChunkDiffers(a.Slice(x), b.Slice(x), tol))
                {
                    break;
                }
            }
        }

        for (; x < a.Length; x++)
        {
            if (Differs(a[x], b[x]))
            {
                return x;
            }
        }

        return -1;
    }

    private static int LastChanged(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        int end = a.Length;
        if (Vector.IsHardwareAccelerated)
        {
            var tol = new Vector<byte>((byte)ChangeTolerance);
            int n = Vector<uint>.Count;
            while (end >= n && !ChunkDiffers(a.Slice(end - n), b.Slice(end - n), tol))
            {
                end -= n;
            }
        }

        for (int x = end - 1; x >= 0; x--)
        {
            if (Differs(a[x], b[x]))
            {
                return x;
            }
        }

        return -1;
    }

    private static bool ChunkDiffers(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, Vector<byte> tolerance)
    {
        var va = Vector.AsVectorByte(new Vector<uint>(a));
        var vb = Vector.AsVectorByte(new Vector<uint>(b));
        Vector<byte> diff = Vector.Max(va, vb) - Vector.Min(va, vb);
        return Vector.GreaterThanAny(diff, tolerance);
    }

    private void QueueFrame(Rect rect, Palette palette, bool transparent, long ticks)
    {
        _scratchData.Clear();
        _lzw.Encode(_indices.AsSpan((rect.Y * Width) + rect.X), Width, rect.Width, rect.Height, palette.MinCodeSize, _scratchData);
        if (_hasPending)
        {
            WritePending();
        }

        (_pendingData, _scratchData) = (_scratchData, _pendingData);
        _pending = new PendingFrame
        {
            Rect = rect,
            LocalPalette = ReferenceEquals(palette, _global) ? null : palette,
            TransparentIndex = transparent ? palette.TransparentIndex : -1,
            Ticks = ticks,
        };
        _hasPending = true;
    }

    private void WritePending()
    {
        _hasPending = false;
        _exactTicks += _pending.Ticks;
        long target = (_exactTicks + 50_000) / 100_000;
        long cs = Math.Clamp(target - _writtenCentiseconds, 2, ushort.MaxValue);
        _writtenCentiseconds += cs;

        GifByteBuffer b = _headerBuffer;
        b.Clear();
        b.WriteByte(0x21);
        b.WriteByte(0xF9);
        b.WriteByte(4);
        int disposal = 1;
        bool hasTransparency = _pending.TransparentIndex >= 0;
        b.WriteByte((byte)((disposal << 2) | (hasTransparency ? 1 : 0)));
        b.WriteUInt16((int)cs);
        b.WriteByte((byte)(hasTransparency ? _pending.TransparentIndex : 0));
        b.WriteByte(0);

        Rect r = _pending.Rect;
        b.WriteByte(0x2C);
        b.WriteUInt16(r.X);
        b.WriteUInt16(r.Y);
        b.WriteUInt16(r.Width);
        b.WriteUInt16(r.Height);
        Palette? local = _pending.LocalPalette;
        if (local is null)
        {
            b.WriteByte(0);
        }
        else
        {
            b.WriteByte((byte)(0x80 | (BitOperations.Log2((uint)local.TableSize) - 1)));
            WriteColorTable(b, local);
            LocalPaletteCount++;
        }

        _output.Write(b.Span);
        _output.Write(_pendingData.Span);
        _framesWritten++;
    }

    private void WriteHeader(Palette palette)
    {
        GifByteBuffer b = _headerBuffer;
        b.Clear();
        b.Write("GIF89a"u8);
        b.WriteUInt16(Width);
        b.WriteUInt16(Height);
        int sizeBits = BitOperations.Log2((uint)palette.TableSize) - 1;
        b.WriteByte((byte)(0x80 | (7 << 4) | sizeBits));
        b.WriteByte(0);
        b.WriteByte(0);
        WriteColorTable(b, palette);

        if (_loop)
        {
            b.WriteByte(0x21);
            b.WriteByte(0xFF);
            b.WriteByte(11);
            b.Write("NETSCAPE2.0"u8);
            b.WriteByte(3);
            b.WriteByte(1);
            b.WriteUInt16(_loopCount);
            b.WriteByte(0);
        }

        _output.Write(b.Span);
        _headerWritten = true;
    }

    private static void WriteColorTable(GifByteBuffer b, Palette palette)
    {
        foreach (uint c in palette.Table)
        {
            b.WriteByte((byte)(c >> 16));
            b.WriteByte((byte)(c >> 8));
            b.WriteByte((byte)c);
        }
    }

    private struct PendingFrame
    {
        public Rect Rect;
        public Palette? LocalPalette;
        public int TransparentIndex;
        public long Ticks;
    }

    private readonly record struct Rect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;
    }
}
