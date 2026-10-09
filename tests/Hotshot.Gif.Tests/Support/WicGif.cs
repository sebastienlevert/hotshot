using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Hotshot.Gif.Tests.Support;

/// <summary>Decodes GIFs with the Windows Imaging Component GIF decoder (through the WinRT BitmapDecoder).</summary>
internal sealed class WicGif
{
    public required uint FrameCount { get; init; }

    public required int LogicalWidth { get; init; }

    public required int LogicalHeight { get; init; }

    public byte[]? ApplicationId { get; init; }

    public byte[]? ApplicationData { get; init; }

    public required List<WicFrame> Frames { get; init; }

    public static async Task<WicGif> DecodeAsync(string path)
    {
        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, stream);
        var container = decoder.BitmapContainerProperties;
        var frames = new List<WicFrame>();
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            BitmapFrame frame = await decoder.GetFrameAsync(i);
            PixelDataProvider pixels = await frame.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            frames.Add(new WicFrame
            {
                PixelWidth = (int)frame.PixelWidth,
                PixelHeight = (int)frame.PixelHeight,
                Pixels = pixels.DetachPixelData(),
                Delay = Convert.ToInt32(await TryGetAsync(frame.BitmapProperties, "/grctlext/Delay") ?? -1),
                Left = Convert.ToInt32(await TryGetAsync(frame.BitmapProperties, "/imgdesc/Left") ?? -1),
                Top = Convert.ToInt32(await TryGetAsync(frame.BitmapProperties, "/imgdesc/Top") ?? -1),
                DescriptorWidth = Convert.ToInt32(await TryGetAsync(frame.BitmapProperties, "/imgdesc/Width") ?? -1),
                DescriptorHeight = Convert.ToInt32(await TryGetAsync(frame.BitmapProperties, "/imgdesc/Height") ?? -1),
            });
        }

        return new WicGif
        {
            FrameCount = decoder.FrameCount,
            LogicalWidth = Convert.ToInt32(await TryGetAsync(container, "/logscrdesc/Width") ?? -1),
            LogicalHeight = Convert.ToInt32(await TryGetAsync(container, "/logscrdesc/Height") ?? -1),
            ApplicationId = await TryGetAsync(container, "/appext/Application") as byte[],
            ApplicationData = await TryGetAsync(container, "/appext/Data") as byte[],
            Frames = frames,
        };
    }

    public static async Task SavePngAsync(string path, byte[] bgra, int width, int height)
    {
        using var memory = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        var bytes = new byte[memory.Size];
        memory.Seek(0);
        await memory.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static async Task<object?> TryGetAsync(BitmapPropertiesView properties, string name)
    {
        try
        {
            BitmapPropertySet set = await properties.GetPropertiesAsync([name]);
            return set.TryGetValue(name, out BitmapTypedValue? value) ? value.Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal sealed class WicFrame
{
    public required int PixelWidth { get; init; }

    public required int PixelHeight { get; init; }

    public required byte[] Pixels { get; init; }

    public required int Delay { get; init; }

    public required int Left { get; init; }

    public required int Top { get; init; }

    public required int DescriptorWidth { get; init; }

    public required int DescriptorHeight { get; init; }

    /// <summary>Pixel as 0xAARRGGBB.</summary>
    public uint this[int x, int y] => BitConverter.ToUInt32(Pixels, ((y * PixelWidth) + x) * 4);
}
