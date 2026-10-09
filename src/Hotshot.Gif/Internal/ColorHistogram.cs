using System.Runtime.InteropServices;

namespace Hotshot.Gif.Internal;

/// <summary>Exact color histogram (run-length accelerated for screen content) that builds a <see cref="Palette"/>.</summary>
internal sealed class ColorHistogram
{
    private readonly Dictionary<uint, int> _counts = new(4096);

    public int DistinctColors => _counts.Count;

    public void AddImage(ReadOnlySpan<byte> bgra, int stride, int width, int height, int weight = 1)
    {
        for (int y = 0; y < height; y++)
        {
            AddPixels(MemoryMarshal.Cast<byte, uint>(bgra.Slice(y * stride, width * 4)), weight);
        }
    }

    public void AddPixels(ReadOnlySpan<uint> bgraPixels, int weight = 1)
    {
        if (bgraPixels.IsEmpty)
        {
            return;
        }

        uint prev = bgraPixels[0] & 0xFFFFFFu;
        int run = 0;
        foreach (uint p in bgraPixels)
        {
            uint c = p & 0xFFFFFFu;
            if (c == prev)
            {
                run++;
                continue;
            }

            Add(prev, run * weight);
            prev = c;
            run = 1;
        }

        Add(prev, run * weight);
    }

    public void Add(uint rgb, int count)
    {
        ref int c = ref CollectionsMarshal.GetValueRefOrAddDefault(_counts, rgb & 0xFFFFFFu, out _);
        c += count;
    }

    /// <summary>
    /// Builds a palette with at most <paramref name="maxColors"/> table entries (one of which is reserved for transparency
    /// when requested). Images with few enough distinct colors get an exact palette; otherwise Wu's quantizer is used and
    /// boxes dominated by a single exact color snap to it (keeps UI backgrounds/text colors exact).
    /// </summary>
    public Palette Build(int maxColors, bool reserveTransparent)
    {
        int maxReal = Math.Max(1, Math.Min(256, maxColors) - (reserveTransparent ? 1 : 0));
        if (_counts.Count == 0)
        {
            return new Palette([0u], reserveTransparent, 0);
        }

        if (_counts.Count <= maxReal)
        {
            var exact = new List<KeyValuePair<uint, int>>(_counts);
            exact.Sort(static (a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));
            var colors = new uint[exact.Count];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = exact[i].Key;
            }

            return new Palette(colors, reserveTransparent, 0);
        }

        return WuQuantizer.Build(_counts, maxReal, reserveTransparent);
    }
}

/// <summary>Xiaolin Wu's greedy orthogonal bipartition color quantizer (5 bits/channel histogram, exact moments).</summary>
internal static class WuQuantizer
{
    private const int Side = 33;
    private const int CellCount = Side * Side * Side;
    private const double SnapShare = 0.4;

    private struct Box
    {
        public int R0, R1, G0, G1, B0, B1, Vol;
    }

    private sealed class Moments
    {
        public readonly double[] Wt = new double[CellCount];
        public readonly double[] Mr = new double[CellCount];
        public readonly double[] Mg = new double[CellCount];
        public readonly double[] Mb = new double[CellCount];
        public readonly double[] M2 = new double[CellCount];
    }

    private static int Ind(int r, int g, int b) => (((r * Side) + g) * Side) + b;

    public static Palette Build(Dictionary<uint, int> counts, int maxColors, bool reserveTransparent)
    {
        var m = new Moments();
        double total = 0;
        foreach (var (rgb, n) in counts)
        {
            int r = (int)((rgb >> 16) & 0xFF), g = (int)((rgb >> 8) & 0xFF), b = (int)(rgb & 0xFF);
            int i = Ind((r >> 3) + 1, (g >> 3) + 1, (b >> 3) + 1);
            m.Wt[i] += n;
            m.Mr[i] += (double)r * n;
            m.Mg[i] += (double)g * n;
            m.Mb[i] += (double)b * n;
            m.M2[i] += (double)((r * r) + (g * g) + (b * b)) * n;
            total += n;
        }

        ComputeCumulativeMoments(m);

        var cubes = new Box[maxColors];
        var vv = new double[maxColors];
        cubes[0] = new Box { R1 = Side - 1, G1 = Side - 1, B1 = Side - 1 };
        int boxCount = maxColors;
        int next = 0;
        for (int i = 1; i < maxColors; i++)
        {
            if (Cut(ref cubes[next], ref cubes[i], m))
            {
                vv[next] = cubes[next].Vol > 1 ? Variance(cubes[next], m) : 0;
                vv[i] = cubes[i].Vol > 1 ? Variance(cubes[i], m) : 0;
            }
            else
            {
                vv[next] = 0;
                i--;
            }

            next = 0;
            double temp = vv[0];
            for (int k = 1; k <= i; k++)
            {
                if (vv[k] > temp)
                {
                    temp = vv[k];
                    next = k;
                }
            }

            if (temp <= 0)
            {
                boxCount = i + 1;
                break;
            }
        }

        var tag = new short[CellCount];
        var colors = new List<uint>(boxCount);
        var boxWeights = new List<double>(boxCount);
        for (int k = 0; k < boxCount; k++)
        {
            ref Box c = ref cubes[k];
            double w = Volume(c, m.Wt);
            if (w <= 0)
            {
                continue;
            }

            short label = (short)colors.Count;
            for (int r = c.R0 + 1; r <= c.R1; r++)
            {
                for (int g = c.G0 + 1; g <= c.G1; g++)
                {
                    for (int b = c.B0 + 1; b <= c.B1; b++)
                    {
                        tag[Ind(r, g, b)] = label;
                    }
                }
            }

            int rr = Math.Clamp((int)Math.Round(Volume(c, m.Mr) / w), 0, 255);
            int gg = Math.Clamp((int)Math.Round(Volume(c, m.Mg) / w), 0, 255);
            int bb = Math.Clamp((int)Math.Round(Volume(c, m.Mb) / w), 0, 255);
            colors.Add((uint)((rr << 16) | (gg << 8) | bb));
            boxWeights.Add(w);
        }

        // Snap boxes dominated by one exact color to that color (e.g. pure white backgrounds stay pure white).
        var bestCount = new int[colors.Count];
        var bestColor = new uint[colors.Count];
        foreach (var (rgb, n) in counts)
        {
            int r = (int)((rgb >> 16) & 0xFF), g = (int)((rgb >> 8) & 0xFF), b = (int)(rgb & 0xFF);
            int label = tag[Ind((r >> 3) + 1, (g >> 3) + 1, (b >> 3) + 1)];
            if (n > bestCount[label])
            {
                bestCount[label] = n;
                bestColor[label] = rgb;
            }
        }

        var fixedColors = new bool[colors.Count];
        for (int k = 0; k < colors.Count; k++)
        {
            if (bestCount[k] >= SnapShare * boxWeights[k])
            {
                colors[k] = bestColor[k];
                fixedColors[k] = true;
            }
        }

        RefineColors(counts, colors, fixedColors);
        colors = colors.Distinct().ToList();

        // Wu cannot split a single histogram cell, so content with many near-identical shades can leave slots unused:
        // fill them with the exact colors that carry the most residual error.
        if (colors.Count < maxColors)
        {
            var residual = new List<(double Error, uint Color)>(counts.Count);
            foreach (var (rgb, n) in counts)
            {
                int best = int.MaxValue;
                foreach (uint c in colors)
                {
                    best = Math.Min(best, Palette.DistanceSquared(rgb, c));
                }

                if (best > 0)
                {
                    residual.Add(((double)best * n, rgb));
                }
            }

            residual.Sort(static (a, b) => b.Error.CompareTo(a.Error));
            for (int i = 0; i < residual.Count && colors.Count < maxColors; i++)
            {
                colors.Add(residual[i].Color);
            }
        }

        var result = new Palette(colors.ToArray(), reserveTransparent, 0);
        double sse = 0;
        foreach (var (rgb, n) in counts)
        {
            sse += (double)Palette.DistanceSquared(rgb, result[result.Nearest(rgb)]) * n;
        }

        return new Palette(colors.ToArray(), reserveTransparent, total > 0 ? sse / total : 0);
    }

    private static void RefineColors(Dictionary<uint, int> counts, List<uint> colors, bool[] fixedColors)
    {
        // Wu boxes seed the palette; nearest-color reassignment removes their axis-aligned boundary error.
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var palette = new Palette(colors.ToArray(), false, 0);
            var weights = new long[colors.Count];
            var reds = new long[colors.Count];
            var greens = new long[colors.Count];
            var blues = new long[colors.Count];
            foreach (var (rgb, n) in counts)
            {
                var index = palette.Nearest(rgb);
                weights[index] += n;
                reds[index] += (long)((rgb >> 16) & 255) * n;
                greens[index] += (long)((rgb >> 8) & 255) * n;
                blues[index] += (long)(rgb & 255) * n;
            }

            var changed = false;
            for (var i = 0; i < colors.Count; i++)
            {
                if (fixedColors[i] || weights[i] == 0) continue;
                var r = (uint)Math.Round((double)reds[i] / weights[i]);
                var g = (uint)Math.Round((double)greens[i] / weights[i]);
                var b = (uint)Math.Round((double)blues[i] / weights[i]);
                var color = (r << 16) | (g << 8) | b;
                changed |= colors[i] != color;
                colors[i] = color;
            }

            if (!changed) break;
        }
    }

    private static void ComputeCumulativeMoments(Moments m)
    {
        var area = new double[Side];
        var areaR = new double[Side];
        var areaG = new double[Side];
        var areaB = new double[Side];
        var area2 = new double[Side];
        for (int r = 1; r < Side; r++)
        {
            Array.Clear(area);
            Array.Clear(areaR);
            Array.Clear(areaG);
            Array.Clear(areaB);
            Array.Clear(area2);
            for (int g = 1; g < Side; g++)
            {
                double line = 0, lineR = 0, lineG = 0, lineB = 0, line2 = 0;
                for (int b = 1; b < Side; b++)
                {
                    int ind1 = Ind(r, g, b);
                    line += m.Wt[ind1];
                    lineR += m.Mr[ind1];
                    lineG += m.Mg[ind1];
                    lineB += m.Mb[ind1];
                    line2 += m.M2[ind1];
                    area[b] += line;
                    areaR[b] += lineR;
                    areaG[b] += lineG;
                    areaB[b] += lineB;
                    area2[b] += line2;
                    int ind2 = ind1 - (Side * Side);
                    m.Wt[ind1] = m.Wt[ind2] + area[b];
                    m.Mr[ind1] = m.Mr[ind2] + areaR[b];
                    m.Mg[ind1] = m.Mg[ind2] + areaG[b];
                    m.Mb[ind1] = m.Mb[ind2] + areaB[b];
                    m.M2[ind1] = m.M2[ind2] + area2[b];
                }
            }
        }
    }

    private static double Volume(in Box c, double[] mm) =>
        mm[Ind(c.R1, c.G1, c.B1)] - mm[Ind(c.R1, c.G1, c.B0)] - mm[Ind(c.R1, c.G0, c.B1)] + mm[Ind(c.R1, c.G0, c.B0)]
        - mm[Ind(c.R0, c.G1, c.B1)] + mm[Ind(c.R0, c.G1, c.B0)] + mm[Ind(c.R0, c.G0, c.B1)] - mm[Ind(c.R0, c.G0, c.B0)];

    private static double Bottom(in Box c, int dir, double[] mm) => dir switch
    {
        0 => -mm[Ind(c.R0, c.G1, c.B1)] + mm[Ind(c.R0, c.G1, c.B0)] + mm[Ind(c.R0, c.G0, c.B1)] - mm[Ind(c.R0, c.G0, c.B0)],
        1 => -mm[Ind(c.R1, c.G0, c.B1)] + mm[Ind(c.R1, c.G0, c.B0)] + mm[Ind(c.R0, c.G0, c.B1)] - mm[Ind(c.R0, c.G0, c.B0)],
        _ => -mm[Ind(c.R1, c.G1, c.B0)] + mm[Ind(c.R1, c.G0, c.B0)] + mm[Ind(c.R0, c.G1, c.B0)] - mm[Ind(c.R0, c.G0, c.B0)],
    };

    private static double Top(in Box c, int dir, int pos, double[] mm) => dir switch
    {
        0 => mm[Ind(pos, c.G1, c.B1)] - mm[Ind(pos, c.G1, c.B0)] - mm[Ind(pos, c.G0, c.B1)] + mm[Ind(pos, c.G0, c.B0)],
        1 => mm[Ind(c.R1, pos, c.B1)] - mm[Ind(c.R1, pos, c.B0)] - mm[Ind(c.R0, pos, c.B1)] + mm[Ind(c.R0, pos, c.B0)],
        _ => mm[Ind(c.R1, c.G1, pos)] - mm[Ind(c.R1, c.G0, pos)] - mm[Ind(c.R0, c.G1, pos)] + mm[Ind(c.R0, c.G0, pos)],
    };

    private static double Variance(in Box c, Moments m)
    {
        double w = Volume(c, m.Wt);
        if (w <= 0)
        {
            return 0;
        }

        double dr = Volume(c, m.Mr), dg = Volume(c, m.Mg), db = Volume(c, m.Mb);
        return Volume(c, m.M2) - (((dr * dr) + (dg * dg) + (db * db)) / w);
    }

    private static double Maximize(in Box c, int dir, int first, int last, out int cut, double wholeR, double wholeG, double wholeB, double wholeW, Moments m)
    {
        double baseR = Bottom(c, dir, m.Mr), baseG = Bottom(c, dir, m.Mg), baseB = Bottom(c, dir, m.Mb), baseW = Bottom(c, dir, m.Wt);
        double max = 0;
        cut = -1;
        for (int i = first; i < last; i++)
        {
            double halfR = baseR + Top(c, dir, i, m.Mr);
            double halfG = baseG + Top(c, dir, i, m.Mg);
            double halfB = baseB + Top(c, dir, i, m.Mb);
            double halfW = baseW + Top(c, dir, i, m.Wt);
            if (halfW <= 0)
            {
                continue;
            }

            double temp = ((halfR * halfR) + (halfG * halfG) + (halfB * halfB)) / halfW;
            halfR = wholeR - halfR;
            halfG = wholeG - halfG;
            halfB = wholeB - halfB;
            halfW = wholeW - halfW;
            if (halfW <= 0)
            {
                continue;
            }

            temp += ((halfR * halfR) + (halfG * halfG) + (halfB * halfB)) / halfW;
            if (temp > max)
            {
                max = temp;
                cut = i;
            }
        }

        return max;
    }

    private static bool Cut(ref Box set1, ref Box set2, Moments m)
    {
        double wholeR = Volume(set1, m.Mr), wholeG = Volume(set1, m.Mg), wholeB = Volume(set1, m.Mb), wholeW = Volume(set1, m.Wt);
        double maxR = Maximize(set1, 0, set1.R0 + 1, set1.R1, out int cutR, wholeR, wholeG, wholeB, wholeW, m);
        double maxG = Maximize(set1, 1, set1.G0 + 1, set1.G1, out int cutG, wholeR, wholeG, wholeB, wholeW, m);
        double maxB = Maximize(set1, 2, set1.B0 + 1, set1.B1, out int cutB, wholeR, wholeG, wholeB, wholeW, m);

        int dir;
        if (maxR >= maxG && maxR >= maxB)
        {
            dir = 0;
            if (cutR < 0)
            {
                return false;
            }
        }
        else if (maxG >= maxR && maxG >= maxB)
        {
            dir = 1;
        }
        else
        {
            dir = 2;
        }

        set2.R1 = set1.R1;
        set2.G1 = set1.G1;
        set2.B1 = set1.B1;
        switch (dir)
        {
            case 0:
                set2.R0 = set1.R1 = cutR;
                set2.G0 = set1.G0;
                set2.B0 = set1.B0;
                break;
            case 1:
                set2.G0 = set1.G1 = cutG;
                set2.R0 = set1.R0;
                set2.B0 = set1.B0;
                break;
            default:
                set2.B0 = set1.B1 = cutB;
                set2.R0 = set1.R0;
                set2.G0 = set1.G0;
                break;
        }

        set1.Vol = (set1.R1 - set1.R0) * (set1.G1 - set1.G0) * (set1.B1 - set1.B0);
        set2.Vol = (set2.R1 - set2.R0) * (set2.G1 - set2.G0) * (set2.B1 - set2.B0);
        return true;
    }
}
