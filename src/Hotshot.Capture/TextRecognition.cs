using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Hotshot.Capture;

/// <summary>Windows OCR only: image pixels never leave the device.</summary>
public static class TextRecognition
{
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
