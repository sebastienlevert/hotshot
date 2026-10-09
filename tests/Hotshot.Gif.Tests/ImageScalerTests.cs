using System.Runtime.InteropServices;
using Hotshot.Gif.Internal;
using Hotshot.Gif.Tests.Support;

namespace Hotshot.Gif.Tests;

public sealed unsafe class ImageScalerTests
{
    [Theory]
    [InlineData(641, 359, 320, 179)]
    [InlineData(1920, 1080, 800, 450)]
    [InlineData(7, 5, 3, 2)]
    [InlineData(641, 359, 641, 359)]
    [InlineData(100, 100, 1, 1)]
    public void SolidColor_StaysExact(int sw, int sh, int dw, int dh)
    {
        byte[] src = TestImages.Solid(sw, sh, 0x00A1B2C3);
        var dst = new byte[dw * dh * 4];
        new ImageScaler(sw, sh, dw, dh).Scale(src, sw * 4, dst);
        uint[] pixels = MemoryMarshal.Cast<byte, uint>(dst).ToArray();
        Assert.All(pixels, p => Assert.Equal(0xFFA1B2C3u, p));
    }

    [Fact]
    public void Checkerboard_AveragesToGray()
    {
        const int sw = 64, sh = 48;
        var src = new uint[sw * sh];
        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                src[(y * sw) + x] = ((x + y) & 1) == 0 ? 0xFFFFFFFFu : 0xFF000000u;
            }
        }

        var dst = new byte[32 * 24 * 4];
        new ImageScaler(sw, sh, 32, 24).Scale(TestImages.Bytes(src), sw * 4, dst);
        Assert.All(MemoryMarshal.Cast<byte, uint>(dst).ToArray(), p => Assert.InRange((int)(p & 0xFF), 127, 128));
    }

    [Fact]
    public void AreaWeights_AreExact()
    {
        // 3 → 2: out0 = (0 * 1 + 90 * 0.5) / 1.5 = 30, out1 = (90 * 0.5 + 180 * 1) / 1.5 = 150.
        uint[] src = [0xFF000000, 0xFF00005A, 0xFF0000B4];
        var dst = new byte[2 * 4];
        new ImageScaler(3, 1, 2, 1).Scale(TestImages.Bytes(src), 12, dst);
        Assert.InRange((int)dst[0], 29, 31);
        Assert.InRange((int)dst[4], 149, 151);
    }

    [Fact]
    public void NegativePitch_ReadsBottomUpBuffers()
    {
        const int sw = 641, sh = 359, dw = 320, dh = 179;
        uint[] topDown = TestImages.Gradient(sw, sh);
        uint[] bottomUp = new uint[topDown.Length];
        for (int y = 0; y < sh; y++)
        {
            topDown.AsSpan(y * sw, sw).CopyTo(bottomUp.AsSpan((sh - 1 - y) * sw, sw));
        }

        var scaler = new ImageScaler(sw, sh, dw, dh);
        var expected = new byte[dw * dh * 4];
        scaler.Scale(TestImages.Bytes(topDown), sw * 4, expected);
        var actual = new byte[dw * dh * 4];
        fixed (uint* src = bottomUp)
        fixed (byte* dst = actual)
        {
            scaler.Scale((nint)(src + ((sh - 1) * sw)), -sw * 4, (nint)dst);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Downscale_KeepsGradientStructure()
    {
        const int sw = 641, sh = 359, dw = 320, dh = 179;
        uint[] src = TestImages.Gradient(sw, sh);
        var dst = new byte[dw * dh * 4];
        new ImageScaler(sw, sh, dw, dh).Scale(TestImages.Bytes(src), sw * 4, dst);
        uint[] output = MemoryMarshal.Cast<byte, uint>(dst).ToArray();

        // Each output pixel must be close to the source pixel at the corresponding center.
        for (int y = 0; y < dh; y += 7)
        {
            for (int x = 0; x < dw; x += 7)
            {
                int sx = (int)((x + 0.5) * sw / dw), sy = (int)((y + 0.5) * sh / dh);
                Assert.True(TestImages.MaxChannelDiff(output[(y * dw) + x], src[(sy * sw) + sx]) <= 8, $"({x},{y})");
            }
        }
    }

    [Theory]
    [InlineData(1920, 1080, 1.0, 800, 800, 450)]
    [InlineData(640, 360, 1.0, 800, 640, 360)]
    [InlineData(641, 359, 1.0, 0, 641, 359)]
    [InlineData(641, 359, 1.0, 320, 320, 179)]
    [InlineData(1, 1, 1.0, 800, 1, 1)]
    [InlineData(4000, 10, 1.0, 100, 100, 1)]
    [InlineData(720, 480, 32.0 / 27.0, 0, 720, 405)]
    [InlineData(1440, 1080, 4.0 / 3.0, 800, 800, 450)]
    public void OutputSize_KeepsAspectAndNeverUpscales(int sw, int sh, double par, int maxWidth, int ew, int eh)
    {
        Assert.Equal((ew, eh), GifConverter.ComputeOutputSize(sw, sh, par, maxWidth));
    }
}
