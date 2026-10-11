using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Hotshot.Core.Agents;

namespace Hotshot.Capture;

/// <summary>Windows OCR only: image pixels never leave the device.</summary>
public static class TextRecognition
{
    public static async Task<AgentText> RecognizeWithBoundsAsync(CapturedImage original, CancellationToken token = default)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Install an OCR-supported Windows language to recognize text.");
        var image = original.Downscale((int)OcrEngine.MaxImageDimension);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(image.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            image.Width, image.Height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(token);
        var sx = original.Width / (double)image.Width; var sy = original.Height / (double)image.Height;
        var words = result.Lines.SelectMany(line => line.Words).Take(10000).Select(word =>
        {
            var rect = word.BoundingRect;
            return new AgentWord(word.Text, new AgentRect((int)Math.Round(rect.X * sx), (int)Math.Round(rect.Y * sy),
                Math.Max(1, (int)Math.Round(rect.Width * sx)), Math.Max(1, (int)Math.Round(rect.Height * sy))));
        }).ToArray();
        return new AgentText(original.Width, original.Height, string.Join(Environment.NewLine, result.Lines.Select(line => line.Text)), words);
    }
    public static async Task<string> RecognizeAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR is unavailable. Install an OCR-supported Windows language under Settings > Time & language.");
        var image = await ImageCodec.DecodePngAsync(png);
        cancellationToken.ThrowIfCancellationRequested();
        image = image.Downscale((int)OcrEngine.MaxImageDimension);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(image.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            image.Width, image.Height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text)).Trim();
    }
}
