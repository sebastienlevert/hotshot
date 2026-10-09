using Hotshot.Editor.Model;
using Microsoft.Graphics.Canvas;
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
    {
        var device = renderer.Device;
        var output = CropMath.OutputRect(document.Crop, renderer.Width, renderer.Height);
        using var targets = new RenderTargetPair();
        var composite = renderer.Render(document, targets);
        using var final = new CanvasRenderTarget(device, output.Width, output.Height, 96f, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var ds = final.CreateDrawingSession())
        {
            // JPEG has no alpha: flatten onto white.
            ds.Clear(format == ExportFormat.Jpeg ? Color.FromArgb(255, 255, 255, 255) : default);
            ds.DrawImage(
                composite,
                new Windows.Foundation.Rect(0, 0, output.Width, output.Height),
                new Windows.Foundation.Rect(output.X, output.Y, output.Width, output.Height),
                1f,
                CanvasImageInterpolation.NearestNeighbor);
        }

        using var stream = new InMemoryRandomAccessStream();
        if (format == ExportFormat.Jpeg)
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
