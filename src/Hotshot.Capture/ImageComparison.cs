namespace Hotshot.Capture;

public sealed record ImageComparisonResult(CapturedImage Difference, long ChangedPixels, double ChangedPercent, PixelRect? ChangedBounds);

public static class ImageComparison
{
    public static ImageComparisonResult Compare(CapturedImage before, CapturedImage after, int tolerance = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tolerance, 255);
        if (before.Width != after.Width || before.Height != after.Height)
            throw new ArgumentException("Visual comparison requires screenshots of identical dimensions.");
        if ((long)before.Width * before.Height > 64_000_000) throw new ArgumentException("Comparison exceeds 64 megapixels.");
        var pixels = new byte[checked(before.Width * before.Height * 4)];
        long changed = 0;
        var left = before.Width; var top = before.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < before.Height; y++)
        for (var x = 0; x < before.Width; x++)
        {
            var i = (y * before.Width + x) * 4;
            var different = Math.Abs(before.Pixels[i] - after.Pixels[i]) > tolerance ||
                Math.Abs(before.Pixels[i + 1] - after.Pixels[i + 1]) > tolerance ||
                Math.Abs(before.Pixels[i + 2] - after.Pixels[i + 2]) > tolerance;
            pixels[i] = different ? (byte)80 : (byte)(after.Pixels[i] / 3);
            pixels[i + 1] = different ? (byte)40 : (byte)(after.Pixels[i + 1] / 3);
            pixels[i + 2] = different ? (byte)255 : (byte)(after.Pixels[i + 2] / 3);
            pixels[i + 3] = 255;
            if (!different) continue;
            changed++;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return new ImageComparisonResult(new CapturedImage(before.Width, before.Height, pixels), changed,
            changed * 100d / (before.Width * (long)before.Height),
            changed == 0 ? null : new PixelRect(left, top, right - left + 1, bottom - top + 1));
    }
}
