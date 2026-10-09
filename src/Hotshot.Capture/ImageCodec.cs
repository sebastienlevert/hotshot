using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Hotshot.Capture;

public static class ImageCodec
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic",
    };

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static async Task<byte[]> EncodePngAsync(CapturedImage image)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)image.Width, (uint)image.Height, 96, 96, image.Pixels);
        await encoder.FlushAsync();
        return await ReadAllAsync(stream);
    }

    /// <summary>Decodes an image file to BGRA8 (straight alpha), optionally fitting it inside <paramref name="maxSize"/>.</summary>
    public static async Task<CapturedImage> LoadAsync(string path, int? maxSize = null)
    {
        using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
        return await DecodeAsync(stream, maxSize);
    }

    public static async Task<CapturedImage> DecodeAsync(IRandomAccessStream stream, int? maxSize = null)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        uint width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
        if (maxSize is { } max && (width > max || height > max))
        {
            var scale = Math.Min((double)max / width, (double)max / height);
            width = Math.Max(1u, (uint)Math.Round(width * scale));
            height = Math.Max(1u, (uint)Math.Round(height * scale));
            transform.ScaledWidth = width;
            transform.ScaledHeight = height;
        }

        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return new CapturedImage((int)width, (int)height, data.DetachPixelData());
    }

    public static async Task<CapturedImage> DecodePngAsync(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        return await DecodeAsync(stream);
    }

    public static async Task<(int Width, int Height)> GetSizeAsync(string path)
    {
        using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
    }

    public static async Task SavePngAsync(CapturedImage image, string path)
    {
        var bytes = await EncodePngAsync(image);
        await WriteAtomicAsync(path, bytes);
    }

    public static async Task WriteAtomicAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Writes a small PNG thumbnail for an in-memory capture.</summary>
    public static Task CreateThumbnailAsync(CapturedImage image, string thumbnailPath, int maxSize = 320) =>
        SavePngAsync(image.Downscale(maxSize), thumbnailPath);

    /// <summary>Writes a small PNG thumbnail for an image or video file.</summary>
    public static async Task<bool> CreateThumbnailAsync(string sourcePath, string thumbnailPath, int maxSize = 320)
    {
        try
        {
            if (IsImageFile(sourcePath))
            {
                var image = await LoadAsync(sourcePath, maxSize);
                FlattenOnWhite(image);
                await SavePngAsync(image, thumbnailPath);
                return true;
            }

            var file = await StorageFile.GetFileFromPathAsync(sourcePath);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)maxSize, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Size == 0)
            {
                return false;
            }

            var decoded = await DecodeAsync(thumbnail, maxSize);
            FlattenOnWhite(decoded);
            await SavePngAsync(decoded, thumbnailPath);
            return true;
        }
        catch (Exception)
        {
            // Thumbnails are best effort; the history shows a placeholder instead.
            return false;
        }
    }

    private static void FlattenOnWhite(CapturedImage image)
    {
        var p = image.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            if (a == 255)
            {
                continue;
            }

            var inv = 255 - a;
            p[i] = (byte)((p[i] * a + 255 * inv) / 255);
            p[i + 1] = (byte)((p[i + 1] * a + 255 * inv) / 255);
            p[i + 2] = (byte)((p[i + 2] * a + 255 * inv) / 255);
            p[i + 3] = 255;
        }
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
    {
        var size = (uint)stream.Size;
        var bytes = new byte[size];
        using var input = stream.GetInputStreamAt(0);
        using var reader = new DataReader(input);
        await reader.LoadAsync(size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
