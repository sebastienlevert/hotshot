using System.Runtime.InteropServices;

namespace Hotshot.Capture;

/// <summary>Alignment and exclusions in the before image's physical-pixel coordinate space.</summary>
public sealed record ComparisonOptions
{
    /// <summary>Moves the after image right (positive) or left (negative).</summary>
    public int OffsetX { get; init; }
    /// <summary>Moves the after image down (positive) or up (negative).</summary>
    public int OffsetY { get; init; }
    /// <summary>
    /// Rectangles are unioned and clipped to the overlap. Empty rectangles are no-ops;
    /// negative dimensions are invalid. Coordinates outside either image are permitted.
    /// </summary>
    public IReadOnlyList<PixelRect> IgnoredRegions { get; init; } = [];
}

public sealed record ImageComparisonResult(CapturedImage Difference, long ChangedPixels, double ChangedPercent, PixelRect? ChangedBounds)
{
    public long ComparedPixels { get; init; }
    /// <summary>Geometric overlap, before subtracting ignored regions; null means no overlap.</summary>
    public PixelRect? ComparedBounds { get; init; }
    public int Tolerance { get; init; } = 8;
    /// <summary>Snapshot of the alignment and exclusions used for this result.</summary>
    public ComparisonOptions Options { get; init; } = new();
}

public static class ImageComparison
{
    public const long MaximumPixels = 64_000_000;
    public const int MaximumIgnoredRegions = 256;

    /// <summary>Compatibility overload: dimensions must match; alpha is not compared.</summary>
    public static ImageComparisonResult Compare(CapturedImage before, CapturedImage after, int tolerance = 8)
    {
        ValidateImage(before, nameof(before));
        ValidateImage(after, nameof(after));
        ValidateTolerance(tolerance);
        if (before.Width != after.Width || before.Height != after.Height)
            throw new ArgumentException("Visual comparison requires screenshots of identical dimensions. Use ComparisonOptions to compare their overlap.");
        return Compare(before, after, new ComparisonOptions(), tolerance);
    }

    /// <summary>
    /// Compares only the aligned overlap, even for different-sized inputs. Unmatched and ignored
    /// pixels are neutral gray in the before-sized difference image and are excluded from the
    /// percentage denominator. No overlap/all ignored returns zero, not NaN. Alpha is not compared.
    /// Geometry uses 64-bit arithmetic before clipping, including offsets at int.Min/MaxValue.
    /// </summary>
    public static ImageComparisonResult Compare(CapturedImage before, CapturedImage after, ComparisonOptions options, int tolerance = 8) =>
        CompareCancellable(before, after, options, tolerance, CancellationToken.None);

    public static ImageComparisonResult CompareCancellable(CapturedImage before, CapturedImage after, ComparisonOptions options, int tolerance,
        CancellationToken cancellationToken)
    {
        ValidateImage(before, nameof(before));
        cancellationToken.ThrowIfCancellationRequested();
        ValidateImage(after, nameof(after));
        ArgumentNullException.ThrowIfNull(options);
        ValidateTolerance(tolerance);
        ArgumentNullException.ThrowIfNull(options.IgnoredRegions);
        if (options.IgnoredRegions.Count > MaximumIgnoredRegions)
            throw new ArgumentException($"Comparison supports at most {MaximumIgnoredRegions} ignored rectangles.", nameof(options));
        options = options with { IgnoredRegions = Array.AsReadOnly(options.IgnoredRegions.ToArray()) };

        var x0 = Math.Max(0L, options.OffsetX);
        var y0 = Math.Max(0L, options.OffsetY);
        var x1 = Math.Min(before.Width, (long)options.OffsetX + after.Width);
        var y1 = Math.Min(before.Height, (long)options.OffsetY + after.Height);
        PixelRect? overlap = x1 > x0 && y1 > y0
            ? new PixelRect(checked((int)x0), checked((int)y0), checked((int)(x1 - x0)), checked((int)(y1 - y0)))
            : null;
        var ignored = new List<PixelRect>(options.IgnoredRegions.Count);
        foreach (var region in options.IgnoredRegions)
        {
            if (region.Width < 0 || region.Height < 0)
                throw new ArgumentException("Ignored rectangle dimensions cannot be negative.", nameof(options));
            if (overlap is not { } bounds || region.IsEmpty) continue;
            var l = Math.Max((long)bounds.Left, region.X);
            var t = Math.Max((long)bounds.Top, region.Y);
            var r = Math.Min((long)bounds.Right, (long)region.X + region.Width);
            var b = Math.Min((long)bounds.Bottom, (long)region.Y + region.Height);
            if (r > l && b > t)
                ignored.Add(new PixelRect(checked((int)l), checked((int)t), checked((int)(r - l)), checked((int)(b - t))));
        }

        var pixels = new byte[checked(before.Width * before.Height * 4)];
        MemoryMarshal.Cast<byte, uint>(pixels.AsSpan()).Fill(0xFF484848);
        long changed = 0, compared = 0;
        var left = before.Width; var top = before.Height; var right = -1; var bottom = -1;
        var intervals = new List<(int Left, int Right)>(ignored.Count);
        if (overlap is { } intersection)
        {
            // Exclusions change only at rectangle edges. Reuse a band's spans instead of
            // sorting/checking every rectangle per row (important for very tall images).
            var edges = new List<int> { intersection.Top, intersection.Bottom };
            foreach (var region in ignored) { edges.Add(region.Top); edges.Add(region.Bottom); }
            edges.Sort();
            var spans = new List<(int Left, int Right)>(ignored.Count + 1);
            for (var band = 0; band < edges.Count - 1; band++)
            {
                var bandTop = edges[band]; var bandBottom = edges[band + 1];
                if (bandTop == bandBottom) continue;
                intervals.Clear();
                foreach (var region in ignored)
                    if (bandTop >= region.Top && bandTop < region.Bottom) intervals.Add((region.Left, region.Right));
                intervals.Sort(static (a, b) => a.Left.CompareTo(b.Left));
                spans.Clear();
                var start = intersection.Left;
                foreach (var interval in intervals)
                {
                    if (interval.Left > start) spans.Add((start, interval.Left));
                    start = Math.Max(start, interval.Right);
                }
                if (start < intersection.Right) spans.Add((start, intersection.Right));
                if (spans.Count == 0) continue;
                for (var y = bandTop; y < bandBottom; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var span in spans) CompareSpan(y, span.Left, span.Right);
                }
            }
        }
        return new ImageComparisonResult(new CapturedImage(before.Width, before.Height, pixels), changed,
            compared == 0 ? 0 : changed * 100d / compared,
            changed == 0 ? null : new PixelRect(left, top, right - left + 1, bottom - top + 1))
        {
            ComparedPixels = compared,
            ComparedBounds = overlap,
            Tolerance = tolerance,
            Options = options,
        };

        void CompareSpan(int y, int start, int end)
        {
            compared += end - start;
            var afterY = checked((int)((long)y - options.OffsetY));
            for (var x = start; x < end; x++)
            {
                var i = checked((y * before.Width + x) * 4);
                var afterX = checked((int)((long)x - options.OffsetX));
                var j = checked((afterY * after.Width + afterX) * 4);
                var different = Math.Abs(before.Pixels[i] - after.Pixels[j]) > tolerance ||
                    Math.Abs(before.Pixels[i + 1] - after.Pixels[j + 1]) > tolerance ||
                    Math.Abs(before.Pixels[i + 2] - after.Pixels[j + 2]) > tolerance;
                pixels[i] = different ? (byte)80 : (byte)(after.Pixels[j] / 3);
                pixels[i + 1] = different ? (byte)40 : (byte)(after.Pixels[j + 1] / 3);
                pixels[i + 2] = different ? (byte)255 : (byte)(after.Pixels[j + 2] / 3);
                pixels[i + 3] = 255;
                if (!different) continue;
                changed++;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        }
    }

    private static void ValidateTolerance(int tolerance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tolerance, 255);
    }

    private static void ValidateImage(CapturedImage image, string parameter)
    {
        ArgumentNullException.ThrowIfNull(image, parameter);
        var count = checked((long)image.Width * image.Height);
        if (image.Width <= 0 || image.Height <= 0 || count > MaximumPixels || image.Pixels.LongLength < checked(count * 4))
            throw new ArgumentException("Comparison requires complete, positive-sized images of at most 64 megapixels.", parameter);
    }
}
