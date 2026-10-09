using System.Numerics;
using Hotshot.Gif.Tests.Support;

namespace Hotshot.Gif.Tests;

public sealed class GifWriterTests
{
    private const uint Red = 0xFFFF0000;
    private const uint Green = 0xFF00FF00;
    private const uint Blue = 0xFF0000FF;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SolidFrames_DecodeWithWic(bool optimize)
    {
        string path = TestPaths.Unique("solid", ".gif");
        var options = new GifOptions { OptimizeFrames = optimize };
        await using (var file = File.Create(path))
        using (var writer = new GifWriter(file, 64, 48, options))
        {
            writer.AddFrame(TestImages.Solid(64, 48, Red), 64 * 4, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(TestImages.Solid(64, 48, Green), 64 * 4, TimeSpan.FromMilliseconds(200));
            writer.AddFrame(TestImages.Solid(64, 48, Blue), 64 * 4, TimeSpan.FromMilliseconds(300));
            Assert.Equal(3, writer.FrameCount);
        }

        WicGif gif = await WicGif.DecodeAsync(path);
        Assert.Equal(3u, gif.FrameCount);
        Assert.Equal(64, gif.LogicalWidth);
        Assert.Equal(48, gif.LogicalHeight);
        Assert.Equal([10, 20, 30], gif.Frames.Select(f => f.Delay));
        uint[] expected = [Red, Green, Blue];
        for (int i = 0; i < 3; i++)
        {
            WicFrame frame = gif.Frames[i];
            Assert.Equal(64, frame.PixelWidth);
            Assert.Equal(48, frame.PixelHeight);
            Assert.Equal(expected[i], frame[0, 0]);
            Assert.Equal(expected[i], frame[63, 47]);
            Assert.Equal(expected[i], frame[31, 20]);
        }

        Assert.Equal("NETSCAPE2.0"u8.ToArray(), gif.ApplicationId);
        Assert.NotNull(gif.ApplicationData);
        Assert.Contains("1,0,0", string.Join(",", gif.ApplicationData!));
        ParsedGif parsed = ParsedGif.Parse(path);
        Assert.Equal(0, parsed.LoopCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(300)]
    public async Task LoopCount_IsWrittenToNetscapeExtension(int loopCount)
    {
        string path = TestPaths.Unique("loop", ".gif");
        await using (var file = File.Create(path))
        using (var writer = new GifWriter(file, 8, 8, new GifOptions { LoopCount = loopCount }))
        {
            writer.AddFrame(TestImages.Solid(8, 8, Red), 32, TimeSpan.FromMilliseconds(50));
            writer.AddFrame(TestImages.Solid(8, 8, Blue), 32, TimeSpan.FromMilliseconds(50));
        }

        ParsedGif parsed = ParsedGif.Parse(path);
        Assert.Equal("NETSCAPE2.0", parsed.ApplicationId);
        Assert.Equal(loopCount, parsed.LoopCount);

        WicGif gif = await WicGif.DecodeAsync(path);
        Assert.Equal(2u, gif.FrameCount);
        Assert.Equal($"1,{loopCount & 0xFF},{loopCount >> 8}", string.Join(",", gif.ApplicationData!.SkipWhile(b => b != 1).Take(3)));
    }

    [Fact]
    public void Delays_AccumulateCentisecondRounding()
    {
        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, 4, 4, new GifOptions { OptimizeFrames = false }))
        {
            for (int i = 0; i < 30; i++)
            {
                writer.AddFrame(TestImages.Solid(4, 4, i % 2 == 0 ? Red : Blue), 16, TimeSpan.FromTicks(10_000_000 / 15));
            }
        }

        ParsedGif parsed = ParsedGif.Parse(stream.ToArray());
        Assert.Equal(30, parsed.Frames.Count);
        Assert.Equal(200, parsed.TotalDelayCentiseconds);
        Assert.All(parsed.Frames, f => Assert.InRange(f.DelayCentiseconds, 6, 7));
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 4)]
    public void IdenticalFrames_AreMergedOnlyWhenOptimizing(bool optimize, int expectedFrames)
    {
        using var stream = new MemoryStream();
        int frameCount;
        using (var writer = new GifWriter(stream, 16, 16, new GifOptions { OptimizeFrames = optimize }))
        {
            byte[] a = TestImages.Bytes(TestImages.Gradient(16, 16));
            writer.AddFrame(a, 64, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(a, 64, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(a, 64, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(TestImages.Solid(16, 16, Green), 64, TimeSpan.FromMilliseconds(100));
            frameCount = writer.FrameCount;
        }

        ParsedGif parsed = ParsedGif.Parse(stream.ToArray());
        Assert.Equal(expectedFrames, frameCount);
        Assert.Equal(expectedFrames, parsed.Frames.Count);
        Assert.Equal(40, parsed.TotalDelayCentiseconds);
        if (optimize)
        {
            Assert.Equal([30, 10], parsed.Frames.Select(f => f.DelayCentiseconds));
        }
    }

    [Fact]
    public async Task SinglePixelChange_ProducesOneByOneSubFrame_OnOddSizedDitheredImage()
    {
        const int width = 641, height = 359;
        uint[] background = TestImages.Gradient(width, height);
        uint[] second = (uint[])background.Clone();
        second[(200 * width) + 100] = 0xFF000000;
        uint[] third = (uint[])second.Clone();
        third[((height - 1) * width) + width - 1] = 0xFF000000;

        string path = TestPaths.Unique("onepixel", ".gif");
        await using (var file = File.Create(path))
        using (var writer = new GifWriter(file, width, height, new GifOptions { Dither = true }))
        {
            writer.AddFrame(TestImages.Bytes(background), width * 4, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(TestImages.Bytes(second), width * 4, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(TestImages.Bytes(third), width * 4, TimeSpan.FromMilliseconds(100));
        }

        ParsedGif parsed = ParsedGif.Parse(path);
        Assert.Equal(3, parsed.Frames.Count);
        Assert.Equal((0, 0, width, height), Rect(parsed.Frames[0]));
        Assert.Equal((100, 200, 1, 1), Rect(parsed.Frames[1]));
        Assert.Equal((width - 1, height - 1, 1, 1), Rect(parsed.Frames[2]));
        Assert.Equal(0xFF000000u, parsed.Frames[1].Canvas[(200 * width) + 100]);
        Assert.Equal(0xFF000000u, parsed.Frames[2].Canvas[^1]);

        // Everything else is untouched by the later frames.
        uint[] diff = parsed.Frames[2].Canvas;
        int changed = 0;
        for (int i = 0; i < diff.Length; i++)
        {
            changed += diff[i] != parsed.Frames[0].Canvas[i] ? 1 : 0;
        }

        Assert.Equal(2, changed);

        WicGif gif = await WicGif.DecodeAsync(path);
        Assert.Equal(3u, gif.FrameCount);
        Assert.Equal(width, gif.LogicalWidth);
        Assert.Equal(height, gif.LogicalHeight);
        Assert.Equal((100, 200, 1, 1), (gif.Frames[1].Left, gif.Frames[1].Top, gif.Frames[1].DescriptorWidth, gif.Frames[1].DescriptorHeight));
        Assert.Equal((width - 1, height - 1, 1, 1), (gif.Frames[2].Left, gif.Frames[2].Top, gif.Frames[2].DescriptorWidth, gif.Frames[2].DescriptorHeight));
    }

    [Fact]
    public void ChangedRegion_IsCroppedToBoundsAndUsesTransparency()
    {
        const int width = 120, height = 80;
        uint[] first = TestImages.Gradient(width, height);
        uint[] second = (uint[])first.Clone();
        for (int y = 30; y < 45; y++)
        {
            for (int x = 50; x < 90; x += 3)
            {
                second[(y * width) + x] = Red;
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, width, height, new GifOptions()))
        {
            writer.AddFrame(TestImages.Bytes(first), width * 4, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(TestImages.Bytes(second), width * 4, TimeSpan.FromMilliseconds(100));
        }

        ParsedGif parsed = ParsedGif.Parse(stream.ToArray());
        ParsedFrame delta = parsed.Frames[1];
        Assert.Equal((50, 30, 89 - 50 + 1, 15), Rect(delta));
        Assert.True(delta.TransparentIndex >= 0);
        Assert.Contains(delta.Indices, i => i == delta.TransparentIndex);
        Assert.Equal(1, delta.Disposal);
        Assert.Equal(Red, delta.Canvas[(30 * width) + 50]);
    }

    [Theory]
    [InlineData(255, true, false)]
    [InlineData(255, true, true)]
    [InlineData(256, false, false)]
    [InlineData(256, false, true)]
    [InlineData(17, true, true)]
    public void ImagesWithFewColors_AreReproducedExactly(int colorCount, bool optimize, bool dither)
    {
        const int width = 97, height = 61;
        var random = new Random(colorCount);
        var colors = new HashSet<uint>();
        while (colors.Count < colorCount)
        {
            colors.Add(0xFF000000u | (uint)random.Next(0x1000000));
        }

        uint[] palette = [.. colors];
        uint[] pixels = new uint[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = palette[i < palette.Length ? i : random.Next(palette.Length)];
        }

        uint[] second = (uint[])pixels.Clone();
        Array.Reverse(second);

        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, width, height, new GifOptions { OptimizeFrames = optimize, Dither = dither, MaxColors = 256 }))
        {
            writer.AddFrame(TestImages.Bytes(pixels), width * 4, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(TestImages.Bytes(second), width * 4, TimeSpan.FromMilliseconds(100));
        }

        ParsedGif parsed = ParsedGif.Parse(stream.ToArray());
        Assert.Equal(pixels, parsed.Frames[0].Canvas);
        Assert.Equal(second, parsed.Frames[1].Canvas);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(16, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    [InlineData(256, true)]
    [InlineData(256, false)]
    public void Palettes_RespectMaxColors(int maxColors, bool optimize)
    {
        const int width = 200, height = 150;
        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, width, height, new GifOptions { MaxColors = maxColors, OptimizeFrames = optimize }))
        {
            writer.AddFrame(TestImages.Bytes(TestImages.Gradient(width, height)), width * 4, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(TestImages.Bytes(TestImages.Gradient(width, height, phase: 60)), width * 4, TimeSpan.FromMilliseconds(100));
            uint[] gray = new uint[width * height];
            for (int i = 0; i < gray.Length; i++)
            {
                uint v = (uint)(i % width * 255 / width);
                gray[i] = 0xFF000000u | (v << 16) | (v << 8) | v;
            }

            writer.AddFrame(TestImages.Bytes(gray), width * 4, TimeSpan.FromMilliseconds(100));
        }

        ParsedGif parsed = ParsedGif.Parse(stream.ToArray());
        Assert.True(parsed.Frames.Count >= 2);
        Assert.True(parsed.GlobalPalette!.Length <= (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, maxColors)));
        foreach (ParsedFrame frame in parsed.Frames)
        {
            Assert.True(frame.Palette.Length <= (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, maxColors)));
            int usedIndices = frame.Indices.Distinct().Count();
            Assert.True(usedIndices <= maxColors, $"{usedIndices} indices used with MaxColors={maxColors}");
            int distinctColors = frame.Canvas.Distinct().Count();
            if (!optimize)
            {
                Assert.True(distinctColors <= maxColors, $"{distinctColors} colors with MaxColors={maxColors}");
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GradientQuality_IsHigh(bool dither)
    {
        const int width = 320, height = 240;
        uint[] source = TestImages.Gradient(width, height);
        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, width, height, new GifOptions { Dither = dither }))
        {
            writer.AddFrame(TestImages.Bytes(source), width * 4, TimeSpan.FromMilliseconds(100));
        }

        ParsedGif parsed = ParsedGif.Parse(stream.ToArray());
        double psnr = TestImages.Psnr(source, parsed.Frames[0].Canvas);
        TestContext.Current.TestOutputHelper?.WriteLine($"dither={dither} PSNR={psnr:F2} dB size={stream.Length}");
        // The oscillating blue channel spreads colors throughout RGB space, unlike a smooth gradient.
        Assert.True(psnr > 29, $"PSNR {psnr:F2} dB");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmoothGradientQuality_IsHigh(bool dither)
    {
        const int width = 320, height = 240;
        var source = new uint[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                source[y * width + x] = 0xFF000080u | (uint)(x * 255 / (width - 1) << 16)
                    | (uint)(y * 255 / (height - 1) << 8);
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, width, height, new GifOptions { Dither = dither }))
        {
            writer.AddFrame(TestImages.Bytes(source), width * 4, TimeSpan.FromMilliseconds(100));
        }

        var parsed = ParsedGif.Parse(stream.ToArray());
        var psnr = TestImages.Psnr(source, parsed.Frames[0].Canvas);
        Assert.True(psnr > 35, $"PSNR {psnr:F2} dB");
    }

    [Fact]
    public void ColorShift_FallsBackToLocalPalette()
    {
        const int width = 160, height = 120;
        uint[] gray = new uint[width * height];
        for (int i = 0; i < gray.Length; i++)
        {
            uint v = (uint)((i % width) * 255 / (width - 1));
            gray[i] = 0xFF000000u | (v << 16) | (v << 8) | v;
        }

        uint[] colorful = TestImages.Gradient(width, height);
        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, width, height, new GifOptions()))
        {
            writer.AddFrame(TestImages.Bytes(gray), width * 4, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(TestImages.Bytes(colorful), width * 4, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(TestImages.Bytes(gray), width * 4, TimeSpan.FromMilliseconds(100));
        }

        ParsedGif parsed = ParsedGif.Parse(stream.ToArray());
        Assert.Equal(3, parsed.Frames.Count);
        Assert.False(parsed.Frames[0].HasLocalPalette);
        Assert.True(parsed.Frames[1].HasLocalPalette);
        Assert.False(parsed.Frames[2].HasLocalPalette);
        Assert.True(TestImages.Psnr(colorful, parsed.Frames[1].Canvas) > 30);
        Assert.True(TestImages.Psnr(gray, parsed.Frames[2].Canvas) > 40);
    }

    [Fact]
    public void TwoColorOptimizedOutput_HasNoTransparencyAndStaysValid()
    {
        const int width = 40, height = 30;
        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, width, height, new GifOptions { MaxColors = 2 }))
        {
            writer.AddFrame(TestImages.Solid(width, height, 0xFFFFFFFF), width * 4, TimeSpan.FromMilliseconds(100));
            uint[] second = new uint[width * height];
            second.AsSpan().Fill(0xFFFFFFFF);
            TestVideo.Fill(second, width, 10, 5, 7, 9, 0xFF000000);
            writer.AddFrame(TestImages.Bytes(second), width * 4, TimeSpan.FromMilliseconds(100));
        }

        ParsedGif parsed = ParsedGif.Parse(stream.ToArray());
        Assert.Equal(2, parsed.Frames.Count);
        Assert.All(parsed.Frames, f => Assert.Equal(-1, f.TransparentIndex));
        Assert.Equal((10, 5, 7, 9), Rect(parsed.Frames[1]));
        Assert.Equal(0xFF000000u, parsed.Frames[1].Canvas[(5 * width) + 10]);
    }

    [Fact]
    public void Dispose_WritesTrailer_AndLeavesStreamOpen()
    {
        var stream = new MemoryStream();
        var writer = new GifWriter(stream, 2, 2, new GifOptions());
        writer.AddFrame(TestImages.Solid(2, 2, Red), 8, TimeSpan.FromMilliseconds(10));
        writer.Dispose();
        writer.Dispose();
        Assert.True(stream.CanWrite);
        Assert.Equal(0x3B, stream.ToArray()[^1]);
        Assert.Throws<ObjectDisposedException>(() => writer.AddFrame(TestImages.Solid(2, 2, Red), 8, TimeSpan.Zero));
    }

    [Fact]
    public void LoopDisabled_OmitsLoopExtension()
    {
        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, 8, 8, new GifOptions { Loop = false }))
        {
            writer.AddFrame(TestImages.Solid(8, 8, Red), 32, TimeSpan.FromMilliseconds(100));
            writer.AddFrame(TestImages.Solid(8, 8, Blue), 32, TimeSpan.FromMilliseconds(100));
        }

        var parsed = ParsedGif.Parse(stream.ToArray());
        Assert.Null(parsed.ApplicationId);
        Assert.Null(parsed.LoopCount);
        Assert.Equal(2, parsed.Frames.Count);
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifWriter(stream, 0, 10, new GifOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifWriter(stream, 10, 10, new GifOptions { MaxColors = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifWriter(stream, 10, 10, new GifOptions { MaxColors = 257 }));
        using var writer = new GifWriter(stream, 10, 10, new GifOptions());
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddFrame(new byte[400], 36, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => writer.AddFrame(new byte[399], 40, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddFrame(new byte[400], 40, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void StridePadding_IsIgnored()
    {
        const int width = 5, height = 3, stride = 32;
        var data = new byte[stride * height];
        new Random(1).NextBytes(data);
        uint[] expected = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint c = BitConverter.ToUInt32(data, (y * stride) + (x * 4));
                expected[(y * width) + x] = c | 0xFF000000u;
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new GifWriter(stream, width, height, new GifOptions { Dither = false }))
        {
            writer.AddFrame(data.AsSpan(0, (stride * (height - 1)) + (width * 4)), stride, TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(expected, ParsedGif.Parse(stream.ToArray()).Frames[0].Canvas);
    }

    private static (int X, int Y, int W, int H) Rect(ParsedFrame f) => (f.Left, f.Top, f.Width, f.Height);
}
