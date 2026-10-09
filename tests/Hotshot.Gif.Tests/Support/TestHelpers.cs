using System.Runtime.InteropServices;

namespace Hotshot.Gif.Tests.Support;

internal static class TestPaths
{
    /// <summary>Per-run artifacts folder under the test output directory (temp folders are not used).</summary>
    public static string Artifacts { get; } = CreateArtifacts();

    public static string File(string name) => Path.Combine(Artifacts, name);

    public static string Unique(string prefix, string extension) => File($"{prefix}-{Guid.NewGuid():N}{extension}");

    private static string CreateArtifacts()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

internal static class TestImages
{
    public static byte[] Solid(int width, int height, uint color)
    {
        var pixels = new uint[width * height];
        pixels.AsSpan().Fill(color);
        return MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
    }

    /// <summary>Smooth 2D gradient with many distinct colors (photo/gradient-like content).</summary>
    public static uint[] Gradient(int width, int height, int phase = 0)
    {
        var pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int r = ((x + phase) * 255 / Math.Max(1, width - 1)) & 0xFF;
                int g = y * 255 / Math.Max(1, height - 1);
                int b = (int)(127.5 + (127.5 * Math.Sin((x + y + phase) * 0.05)));
                pixels[(y * width) + x] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
            }
        }

        return pixels;
    }

    public static byte[] Bytes(uint[] pixels) => MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();

    public static (int R, int G, int B) Rgb(uint c) => ((int)((c >> 16) & 0xFF), (int)((c >> 8) & 0xFF), (int)(c & 0xFF));

    public static int MaxChannelDiff(uint a, uint b)
    {
        var (r1, g1, b1) = Rgb(a);
        var (r2, g2, b2) = Rgb(b);
        return Math.Max(Math.Abs(r1 - r2), Math.Max(Math.Abs(g1 - g2), Math.Abs(b1 - b2)));
    }

    /// <summary>Peak signal-to-noise ratio (dB) over RGB channels.</summary>
    public static double Psnr(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var (r1, g1, b1) = Rgb(a[i]);
            var (r2, g2, b2) = Rgb(b[i]);
            sum += ((r1 - r2) * (r1 - r2)) + ((g1 - g2) * (g1 - g2)) + ((b1 - b2) * (b1 - b2));
        }

        double mse = sum / (a.Length * 3.0);
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}

/// <summary>Synchronous <see cref="IProgress{T}"/> (Progress&lt;T&gt; posts asynchronously).</summary>
internal sealed class SyncProgress(Action<double> callback) : IProgress<double>
{
    public void Report(double value) => callback(value);
}
