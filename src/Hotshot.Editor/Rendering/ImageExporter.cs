using Hotshot.Editor.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using Windows.UI;

namespace Hotshot.Editor.Rendering;

internal enum ExportFormat
{
    Png,
    Jpeg,
}

/// <summary>Renders the final image (base + annotations, cropped) at native resolution and encodes it. Used by Save, Save as, Copy and the self-test.</summary>
internal static class ImageExporter
{
    public static ExportFormat FormatForPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" ? ExportFormat.Jpeg : ExportFormat.Png;

    public static async Task<byte[]> EncodeAsync(DocumentRenderer renderer, AnnotationDocument document, ExportFormat format = ExportFormat.Png)
        => await EncodeAsync(renderer, document, new ImageExportOptions(format == ExportFormat.Jpeg ? ImageExportFormat.Jpeg : ImageExportFormat.Png));

    public static async Task<byte[]> EncodeAsync(DocumentRenderer renderer, AnnotationDocument document, ImageExportOptions options)
    {
        var device = renderer.Device;
        var output = CropMath.OutputRect(document.Crop, renderer.Width, renderer.Height);
        var layout = options.GetLayout(output.Width, output.Height);
        using var targets = new RenderTargetPair();
        var composite = renderer.Render(document, targets);
        using var final = new CanvasRenderTarget(device, layout.CanvasWidth, layout.CanvasHeight, 96f, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        var destination = new Windows.Foundation.Rect(options.Padding, options.Padding, layout.ImageWidth, layout.ImageHeight);
        var radius = Math.Min(options.CornerRadius, Math.Min(layout.ImageWidth, layout.ImageHeight) / 2f);
        using var clip = CanvasGeometry.CreateRoundedRectangle(device, destination, radius, radius);
        using (var ds = final.CreateDrawingSession())
        {
            var background = options.Background switch
            {
                ExportBackground.White => Color.FromArgb(255, 255, 255, 255),
                ExportBackground.Charcoal => Color.FromArgb(255, 32, 32, 36),
                ExportBackground.Blue => Color.FromArgb(255, 42, 78, 146),
                _ => options.Format == ImageExportFormat.Jpeg ? Color.FromArgb(255, 255, 255, 255) : default,
            };
            ds.Clear(background);
            if (options.Shadow)
            {
                using var mask = new CanvasCommandList(device);
                using (var mds = mask.CreateDrawingSession()) mds.FillGeometry(clip, Color.FromArgb(255, 0, 0, 0));
                using var shadow = new ShadowEffect { Source = mask, BlurAmount = 10, ShadowColor = Color.FromArgb(110, 0, 0, 0) };
                ds.DrawImage(shadow, 0, 6);
            }
            using var layer = ds.CreateLayer(1, clip);
            ds.DrawImage(composite, destination,
                new Windows.Foundation.Rect(output.X, output.Y, output.Width, output.Height),
                1f,
                options.Width == 0 ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.HighQualityCubic);
        }

        using var stream = new InMemoryRandomAccessStream();
        if (options.Format == ImageExportFormat.Jpeg)
        {
            await final.SaveAsync(stream, CanvasBitmapFileFormat.Jpeg, 0.92f);
        }
        else
        {
            await final.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        }

        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Decodes an image file at DPI 96 so that 1 DIP = 1 pixel.</summary>
    public static async Task<CanvasBitmap> LoadBitmapAsync(CanvasDevice device, byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        return await CanvasBitmap.LoadAsync(device, stream, 96f);
    }

    /// <summary>Writes bytes next to the destination first, then swaps it in, so a failure never leaves a half-written file.</summary>
    public static async Task WriteFileAtomicAsync(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
