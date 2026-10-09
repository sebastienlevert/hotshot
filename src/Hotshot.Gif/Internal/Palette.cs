using System.Numerics;
using System.Runtime.CompilerServices;

namespace Hotshot.Gif.Internal;

/// <summary>
/// An indexed color table plus a fast color → index mapper.
/// Mapping: exact palette colors are found through a small hash (so flat UI colors stay exact and are never dithered);
/// other colors go through a lazily-filled 18-bit (RGB666) cache whose entry stores the nearest color, an optional second
/// color and the mix ratio used by ordered (Bayer 8x8) dithering. Ordered dithering depends only on the pixel position,
/// so identical source pixels map identically in every frame (no "crawling" noise, transparency optimization stays effective).
/// </summary>
internal sealed class Palette
{
    private const int CacheBits = 18;

    // Squared RGB distance below which a color is mapped to its nearest entry without dithering
    // (absorbs video compression noise around flat colors).
    private const int DeadZone = 27;

    // Weight of the visual noise (variance) introduced by mixing two colors, relative to the mean color error.
    private const double SparseMixPenalty = 0.15;
    private const double DenseMixPenalty = 0.85;

    private readonly uint[] _colors;
    private readonly uint[] _exactKeys;
    private readonly byte[] _exactValues;
    private readonly int _exactMask;
    private readonly int _exactShift;
    private readonly uint[] _cache = new uint[1 << CacheBits];

    public Palette(ReadOnlySpan<uint> colors, bool reserveTransparent, double baselineError)
    {
        if (colors.IsEmpty)
        {
            colors = [0u];
        }

        int maxReal = reserveTransparent ? 255 : 256;
        if (colors.Length > maxReal)
        {
            throw new ArgumentException($"A palette can hold at most {maxReal} colors.", nameof(colors));
        }

        Count = colors.Length;
        int needed = Count + (reserveTransparent ? 1 : 0);
        int size = 2;
        while (size < needed)
        {
            size <<= 1;
        }

        TableSize = size;
        MinCodeSize = Math.Max(2, BitOperations.Log2((uint)size));
        TransparentIndex = reserveTransparent ? Count : -1;
        BaselineError = baselineError;
        _colors = new uint[size];
        for (int i = 0; i < colors.Length; i++)
        {
            _colors[i] = colors[i] & 0xFFFFFFu;
        }

        int hashSize = 64;
        while (hashSize < Count * 4)
        {
            hashSize <<= 1;
        }

        _exactKeys = new uint[hashSize];
        _exactValues = new byte[hashSize];
        _exactMask = hashSize - 1;
        _exactShift = 32 - BitOperations.Log2((uint)hashSize);
        for (int i = 0; i < Count; i++)
        {
            uint key = _colors[i] | 0x80000000u;
            int slot = (int)((_colors[i] * 0x9E3779B1u) >> _exactShift);
            while (_exactKeys[slot] != 0 && _exactKeys[slot] != key)
            {
                slot = (slot + 1) & _exactMask;
            }

            if (_exactKeys[slot] == 0)
            {
                _exactKeys[slot] = key;
                _exactValues[slot] = (byte)i;
            }
        }
    }

    /// <summary>Bayer 8x8 ordered dither thresholds (0..63).</summary>
    internal static ReadOnlySpan<byte> Bayer8 =>
    [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    /// <summary>Number of real (opaque) colors.</summary>
    public int Count { get; }

    /// <summary>GIF color table size (power of two, ≥ 2), including the transparency slot and padding.</summary>
    public int TableSize { get; }

    public int MinCodeSize { get; }

    /// <summary>Index reserved for transparency, or -1.</summary>
    public int TransparentIndex { get; }

    /// <summary>Estimated mean squared error (sum over channels) of the image this palette was built from.</summary>
    public double BaselineError { get; }

    /// <summary>Error level at which a rebuild was attempted and did not help (avoids rebuilding every frame).</summary>
    public double RejectedError { get; set; }

    /// <summary>Colors as 0x00RRGGBB (same memory layout as a BGRA pixel with alpha cleared). Length = <see cref="TableSize"/>.</summary>
    public ReadOnlySpan<uint> Table => _colors;

    public uint this[int index] => _colors[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindExact(uint rgb)
    {
        uint key = rgb | 0x80000000u;
        int slot = (int)((rgb * 0x9E3779B1u) >> _exactShift);
        while (true)
        {
            uint k = _exactKeys[slot];
            if (k == key)
            {
                return _exactValues[slot];
            }

            if (k == 0)
            {
                return -1;
            }

            slot = (slot + 1) & _exactMask;
        }
    }

    /// <summary>Maps a 0x00RRGGBB color to a palette index, optionally with position-stable ordered dithering.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Map(uint rgb, int x, int y, bool dither)
    {
        int exact = FindExact(rgb);
        if (exact >= 0)
        {
            return exact;
        }

        uint e = Lookup(rgb);
        if (dither && (int)((e >> 16) & 0xFF) > Bayer8[((y & 7) << 3) | (x & 7)])
        {
            return (int)((e >> 8) & 0xFF);
        }

        return (int)(e & 0xFF);
    }

    /// <summary>Nearest palette index (no dithering).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Nearest(uint rgb)
    {
        int exact = FindExact(rgb);
        return exact >= 0 ? exact : (int)(Lookup(rgb) & 0xFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Lookup(uint rgb)
    {
        int key = (int)(((rgb >> 6) & 0x3F000u) | ((rgb >> 4) & 0xFC0u) | ((rgb >> 2) & 0x3Fu));
        uint e = _cache[key];
        return e != 0 ? e : ComputeEntry(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DistanceSquared(uint a, uint b)
    {
        int dr = (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
        int dg = (int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF);
        int db = (int)(a & 0xFF) - (int)(b & 0xFF);
        return (dr * dr) + (dg * dg) + (db * db);
    }

    private uint ComputeEntry(int key)
    {
        int r6 = key >> 12, g6 = (key >> 6) & 63, b6 = key & 63;
        int r = (r6 << 2) | (r6 >> 4);
        int g = (g6 << 2) | (g6 >> 4);
        int b = (b6 << 2) | (b6 >> 4);

        int best = 0;
        int bestD = int.MaxValue;
        for (int i = 0; i < Count; i++)
        {
            uint c = _colors[i];
            int dr = r - (int)((c >> 16) & 0xFF);
            int dg = g - (int)((c >> 8) & 0xFF);
            int db = b - (int)(c & 0xFF);
            int d = (dr * dr) + (dg * dg) + (db * db);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        int second = best;
        int t64 = 0;
        if (bestD > DeadZone && Count > 1)
        {
            uint pc = _colors[best];
            int pr = (int)((pc >> 16) & 0xFF), pg = (int)((pc >> 8) & 0xFF), pb = (int)(pc & 0xFF);
            int er = r - pr, eg = g - pg, eb = b - pb;
            double bestErr = bestD;
            for (int i = 0; i < Count; i++)
            {
                if (i == best)
                {
                    continue;
                }

                uint qc = _colors[i];
                int dr = (int)((qc >> 16) & 0xFF) - pr;
                int dg = (int)((qc >> 8) & 0xFF) - pg;
                int db = (int)(qc & 0xFF) - pb;
                int dd = (dr * dr) + (dg * dg) + (db * db);
                int dot = (er * dr) + (eg * dg) + (eb * db);
                if (dd == 0 || dot <= 0)
                {
                    continue;
                }

                double t = Math.Min(1.0, (double)dot / dd);
                double mr = er - (t * dr), mg = eg - (t * dg), mb = eb - (t * db);
                double penalty = Count <= 2 ? SparseMixPenalty : DenseMixPenalty;
                double err = (mr * mr) + (mg * mg) + (mb * mb) + (penalty * t * (1 - t) * dd);
                if (err < bestErr)
                {
                    bestErr = err;
                    second = i;
                    t64 = (int)Math.Round(t * 64);
                }
            }

            if (t64 <= 0)
            {
                second = best;
                t64 = 0;
            }
        }

        uint entry = 0x80000000u | ((uint)t64 << 16) | ((uint)second << 8) | (uint)best;
        _cache[key] = entry;
        return entry;
    }
}
