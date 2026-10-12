using System.Text.Json;
using Hotshot.Capture;

namespace Hotshot.Views;

internal static class ComparisonExport
{
    /// <summary>Creates a new local report directory; never overwrites existing files or uploads.</summary>
    public static async Task<string> SaveAsync(string folder, CapturedImage before, CapturedImage after,
        ImageComparisonResult result, ComparisonOptions options, CancellationToken token = default)
    {
        var name = $"Hotshot-comparison-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var destination = Path.Combine(folder, name);
        var files = new[] { "before.png", "after.png", "difference.png", "report.json" };
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("The export destination already exists. Choose Export again to generate a new name.");
        Directory.CreateDirectory(destination);
        try
        {
            // Sequential encodes cap peak memory for 64 MP inputs. Source arrays are never flattened in place.
            token.ThrowIfCancellationRequested();
            await ImageCodec.SavePngAsync(Flatten(before), Path.Combine(destination, files[0]));
            token.ThrowIfCancellationRequested();
            await ImageCodec.SavePngAsync(Flatten(after), Path.Combine(destination, files[1]));
            token.ThrowIfCancellationRequested();
            await ImageCodec.SavePngAsync(Flatten(result.Difference), Path.Combine(destination, files[2]));
            var report = new
            {
                SchemaVersion = 1,
                CreatedUtc = DateTimeOffset.UtcNow,
                CoordinateSpace = "Before image physical pixels. Positive offsets move the after image right/down.",
                SizePolicy = "Only aligned overlap is compared; unmatched and ignored pixels are excluded.",
                AlphaPolicy = "RGB channels compared; alpha ignored. Exported PNGs are opaque, flattened on white.",
                DifferenceLegend = "Red: changed; dim: compared unchanged; neutral gray: not compared.",
                Before = new { before.Width, before.Height, File = files[0] },
                After = new { after.Width, after.Height, File = files[1] },
                Difference = new { result.Difference.Width, result.Difference.Height, File = files[2] },
                result.Tolerance,
                options.OffsetX,
                options.OffsetY,
                IgnoredRegions = options.IgnoredRegions.Select(region => new { region.X, region.Y, region.Width, region.Height }),
                result.ComparedBounds,
                result.ComparedPixels,
                result.ChangedPixels,
                result.ChangedPercent,
                result.ChangedBounds,
                ImageComparison.MaximumPixels,
            };
            await File.WriteAllTextAsync(Path.Combine(destination, files[3]), JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }), token);
            return destination;
        }
        catch
        {
            foreach (var file in files)
            {
                TryDelete(Path.Combine(destination, file));
                TryDelete(Path.Combine(destination, file + ".tmp"));
            }
            try { Directory.Delete(destination); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { Log.Error("Comparison report cleanup failed", cleanup); }
            throw;
        }
    }

    private static CapturedImage Flatten(CapturedImage image)
    {
        var count = checked(image.Width * image.Height * 4);
        var pixels = image.Pixels.AsSpan(0, count).ToArray();
        for (var i = 0; i < count; i += 4)
        {
            var alpha = pixels[i + 3];
            var white = 255 * (255 - alpha);
            pixels[i] = (byte)((pixels[i] * alpha + white) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * alpha + white) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * alpha + white) / 255);
            pixels[i + 3] = 255;
        }
        return new CapturedImage(image.Width, image.Height, pixels);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Comparison report temporary-file cleanup failed", ex); }
    }
}
