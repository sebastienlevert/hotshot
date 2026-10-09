using System.Runtime.InteropServices;
using Hotshot.Gif.Internal;
using Hotshot.Gif.Tests.Support;

namespace Hotshot.Gif.Tests;

public sealed class QuantizerTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(200, false)]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void FewColors_GiveExactPalette(int colorCount, bool reserveTransparent)
    {
        var random = new Random(colorCount);
        var colors = new HashSet<uint>();
        while (colors.Count < colorCount)
        {
            colors.Add((uint)random.Next(0x1000000));
        }

        var histogram = new ColorHistogram();
        foreach (uint c in colors)
        {
            histogram.Add(c, random.Next(1, 1000));
        }

        Palette palette = histogram.Build(256, reserveTransparent);
        Assert.Equal(colorCount, palette.Count);
        Assert.Equal(0, palette.BaselineError);
        Assert.Equal(reserveTransparent ? colorCount : -1, palette.TransparentIndex);
        foreach (uint c in colors)
        {
            int index = palette.Map(c, 3, 5, dither: true);
            Assert.Equal(c, palette[index]);
            Assert.Equal(c, palette[palette.Nearest(c)]);
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(16, false)]
    [InlineData(64, true)]
    [InlineData(256, false)]
    [InlineData(256, true)]
    public void ManyColors_RespectMaxColors(int maxColors, bool reserveTransparent)
    {
        uint[] pixels = TestImages.Gradient(300, 200);
        var histogram = new ColorHistogram();
        histogram.AddPixels(pixels);
        Assert.True(histogram.DistinctColors > 1000);

        Palette palette = histogram.Build(maxColors, reserveTransparent);
        int maxReal = maxColors - (reserveTransparent ? 1 : 0);
        Assert.InRange(palette.Count, 1, maxReal);
        Assert.True(palette.Count >= maxReal * 3 / 4, $"Only {palette.Count} of {maxReal} colors used.");
        Assert.True(palette.TableSize <= Math.Max(2, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)maxColors)));
        Assert.Equal(palette.Table.Length, palette.TableSize);
        Assert.Equal(palette.Count, palette.Table[..palette.Count].ToArray().Distinct().Count());
    }

    [Fact]
    public void DominantExactColors_AreKeptExact()
    {
        // Typical screen content: a flat background plus anti-aliased/gradient content.
        uint[] pixels = TestImages.Gradient(256, 256);
        for (int i = 0; i < pixels.Length; i++)
        {
            if (i % 5 != 0)
            {
                pixels[i] = i % 3 == 0 ? 0xFFF3F3F3u : 0xFF1E1E1Eu;
            }
        }

        var histogram = new ColorHistogram();
        histogram.AddPixels(pixels);
        Palette palette = histogram.Build(64, reserveTransparent: true);
        Assert.Contains(0xF3F3F3u, palette.Table[..palette.Count].ToArray());
        Assert.Contains(0x1E1E1Eu, palette.Table[..palette.Count].ToArray());
    }

    [Fact]
    public void CachedNearestLookup_IsCloseToBruteForce()
    {
        var histogram = new ColorHistogram();
        histogram.AddPixels(TestImages.Gradient(300, 200));
        Palette palette = histogram.Build(128, reserveTransparent: false);
        var random = new Random(5);
        long cached = 0, brute = 0;
        for (int i = 0; i < 20000; i++)
        {
            uint c = (uint)random.Next(0x1000000);
            cached += Palette.DistanceSquared(c, palette[palette.Nearest(c)]);
            int best = int.MaxValue;
            for (int k = 0; k < palette.Count; k++)
            {
                best = Math.Min(best, Palette.DistanceSquared(c, palette[k]));
            }

            brute += best;
        }

        Assert.True(cached <= brute * 1.1 + 20000, $"cached={cached} brute={brute}");
    }

    [Fact]
    public void OrderedDither_ReproducesIntermediateColorsOnAverage_AndIsPositionStable()
    {
        var palette = new Palette([0x000000u, 0xFFFFFFu], reserveTransparent: false, baselineError: 0);
        foreach (int level in new[] { 64, 96, 128, 200 })
        {
            uint gray = (uint)((level << 16) | (level << 8) | level);
            double sum = 0;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int index = palette.Map(gray, x, y, dither: true);
                    Assert.Equal(index, palette.Map(gray, x + 8, y + 16, dither: true));
                    sum += palette[index] & 0xFF;
                }
            }

            Assert.InRange(sum / 64, level - 12, level + 12);
            Assert.Equal(level < 128 ? 0 : 1, palette.Map(gray, 0, 0, dither: false) == 0 ? 0 : 1);
        }
    }

    [Fact]
    public void Histogram_CountsRunsAndWeights()
    {
        var histogram = new ColorHistogram();
        uint[] pixels = [0xFF000001, 0xFF000001, 0x00000001, 0xFF000002, 0xFF000001];
        histogram.AddPixels(pixels, weight: 2);
        histogram.AddImage(MemoryMarshal.AsBytes(pixels.AsSpan()), 20, 5, 1);
        Assert.Equal(2, histogram.DistinctColors);
        Palette palette = histogram.Build(256, false);
        Assert.Equal(0x000001u, palette[0]);
        Assert.Equal(0x000002u, palette[1]);
    }
}
