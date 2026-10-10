using Hotshot.Capture;
using Hotshot.Core.Descriptions;
using Hotshot.Descriptions;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

var directory = Path.Combine(Path.GetTempPath(), "hotshot-description-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    const int width = 256, height = 160;
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var offset = (y * width + x) * 4;
        pixels[offset] = 40;
        pixels[offset + 1] = (byte)(x > 40 && x < 210 && y > 40 && y < 120 ? 90 : 220);
        pixels[offset + 2] = (byte)(x > 40 && x < 210 && y > 40 && y < 120 ? 240 : 220);
        pixels[offset + 3] = 255;
    }
    var png = await ImageCodec.EncodePngAsync(new CapturedImage(width, height, pixels));
    var description = new CaptureDescription("Synthetic caf\u00e9 image", "A generated colored rectangle; no real screenshot.");
    var tagged = PngDescriptionMetadata.Embed(png, description);
    var path = Path.Combine(directory, "synthetic.png");
    await File.WriteAllBytesAsync(path, tagged);
    var decoded = await ImageCodec.LoadAsync(path);
    if (!pixels.AsSpan().SequenceEqual(decoded.Pixels)) throw new InvalidDataException("Metadata changed image pixels.");
    using (var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read))
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var title = await decoder.BitmapProperties.GetPropertiesAsync(["/iTXt/Keyword", "/iTXt/TextEntry"]);
        var details = await decoder.BitmapProperties.GetPropertiesAsync(["/[1]iTXt/Keyword", "/[1]iTXt/TextEntry"]);
        if ((string)title["/iTXt/Keyword"].Value != "Title" ||
            (string)title["/iTXt/TextEntry"].Value != description.Summary ||
            (string)details["/[1]iTXt/Keyword"].Value != "Description" ||
            (string)details["/[1]iTXt/TextEntry"].Value != description.Description)
            throw new InvalidDataException("Windows image metadata reader could not read the embedded descriptions.");
    }
    Console.WriteLine("PASS Windows PNG decoder reads Unicode Title/Description and pixels remain unchanged.");
    if (args.Contains("--copilot", StringComparer.Ordinal))
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var provider = new CopilotDescriptionProvider(directory);
        var generated = await provider.DescribeAsync(png, timeout.Token);
        if (string.IsNullOrWhiteSpace(generated.Description)) throw new InvalidDataException("Copilot returned no description.");
        Console.WriteLine("PASS real Copilot SDK describes a generated rectangle; isolated session is deleted.");
    }
    else Console.WriteLine("SKIP Copilot network request: pass --copilot to submit only this generated test image.");
}
finally { Directory.Delete(directory, recursive: true); }
