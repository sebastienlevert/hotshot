using Hotshot.Gif.Internal;
using Hotshot.Gif.Tests.Support;

namespace Hotshot.Gif.Tests;

public sealed class LzwEncoderTests
{
    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(2, 37, 13)]
    [InlineData(4, 300, 200)]
    [InlineData(8, 641, 359)]
    [InlineData(8, 1000, 1000)]
    public void RandomData_RoundTrips(int minCodeSize, int width, int height)
    {
        var random = new Random(minCodeSize * 7919 + width);
        int alphabet = 1 << minCodeSize;
        var indices = new byte[width * height];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (byte)random.Next(alphabet);
        }

        AssertRoundTrip(indices, width, height, minCodeSize);
    }

    [Fact]
    public void LowEntropyData_WithLongStrings_RoundTrips()
    {
        // Runs and repeated patterns produce long dictionary strings, code width growth to 12 bits and table resets.
        const int width = 2000, height = 700;
        var random = new Random(42);
        var indices = new byte[width * height];
        for (int i = 0; i < indices.Length;)
        {
            int run = random.Next(1, 400);
            byte value = (byte)(random.Next(3) == 0 ? random.Next(256) : random.Next(4));
            for (int k = 0; k < run && i < indices.Length; k++)
            {
                indices[i++] = (byte)(value ^ (k % 3 == 0 ? 0 : 1));
            }
        }

        AssertRoundTrip(indices, width, height, 8);
    }

    [Fact]
    public void UniformData_RoundTrips()
    {
        var indices = new byte[1920 * 1080];
        AssertRoundTrip(indices, 1920, 1080, 2);
    }

    [Fact]
    public void SubRectangle_WithStride_RoundTrips()
    {
        const int stride = 50;
        var random = new Random(7);
        var full = new byte[stride * 40];
        random.NextBytes(full);
        var buffer = new GifByteBuffer();
        new LzwEncoder().Encode(full.AsSpan((3 * stride) + 5), stride, 17, 11, 8, buffer);
        byte[] decoded = Decode(buffer.Span, out int minCodeSize, 17 * 11);
        Assert.Equal(8, minCodeSize);
        for (int y = 0; y < 11; y++)
        {
            Assert.Equal(full.AsSpan(((3 + y) * stride) + 5, 17).ToArray(), decoded.AsSpan(y * 17, 17).ToArray());
        }
    }

    [Fact]
    public void Output_UsesSubBlocksOfAtMost255Bytes()
    {
        var random = new Random(3);
        var indices = new byte[100_000];
        random.NextBytes(indices);
        var buffer = new GifByteBuffer();
        new LzwEncoder().Encode(indices, 1000, 1000, 100, 8, buffer);
        ReadOnlySpan<byte> data = buffer.Span;
        int pos = 1;
        int blocks = 0;
        while (data[pos] != 0)
        {
            Assert.InRange(data[pos], 1, 255);
            pos += data[pos] + 1;
            blocks++;
        }

        Assert.Equal(data.Length - 1, pos);
        Assert.True(blocks > 1);
    }

    private static void AssertRoundTrip(byte[] indices, int width, int height, int minCodeSize)
    {
        var buffer = new GifByteBuffer();
        new LzwEncoder().Encode(indices, width, width, height, minCodeSize, buffer);
        byte[] decoded = Decode(buffer.Span, out int decodedMin, indices.Length);
        Assert.Equal(minCodeSize, decodedMin);
        Assert.True(indices.AsSpan().SequenceEqual(decoded), "LZW round trip mismatch.");
    }

    private static byte[] Decode(ReadOnlySpan<byte> encoded, out int minCodeSize, int pixelCount)
    {
        minCodeSize = encoded[0];
        var data = new List<byte>();
        int pos = 1;
        while (encoded[pos] != 0)
        {
            int size = encoded[pos];
            data.AddRange(encoded.Slice(pos + 1, size));
            pos += size + 1;
        }

        return ParsedGif.LzwDecode(data.ToArray(), minCodeSize, pixelCount);
    }
}
