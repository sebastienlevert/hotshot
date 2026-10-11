using System.Numerics;
using Hotshot.Capture;
using Hotshot.Editor;
using Hotshot.Editor.Model;
using Hotshot.Editor.Rendering;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windows.UI;

WinRT.ComWrappersSupport.InitializeComWrappers();
using var device = new CanvasDevice();
using var bitmap = new CanvasRenderTarget(device, 640, 240, 96);
using (var ds = bitmap.CreateDrawingSession())
{
    ds.Clear(Color.FromArgb(255, 255, 255, 255));
    ds.DrawText("HOTSHOT TEST 1234", new Vector2(24, 64), Color.FromArgb(255, 0, 0, 0),
        new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = 36 });
}
using var renderer = new DocumentRenderer(bitmap);
var document = new AnnotationDocument(640, 240);
var plain = await ImageExporter.EncodeAsync(renderer, document);
if (args.Contains("--ocr"))
{
    var text = await TextRecognition.RecognizeAsync(plain);
    if (!text.Contains("HOTSHOT", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Windows OCR did not recognize the generated text.");
    Console.WriteLine("PASS local Windows OCR recognizes generated text without a network request.");
}
document.Add(new RedactionAnnotation { Rect = new RectF(20, 50, 550, 80) });
document.Add(new RectangleAnnotation { Rect = new RectF(20, 50, 550, 80), Filled = true, Color = RgbaColor.Red });
var redacted = await ImageExporter.EncodeAsync(renderer, document);
var image = await ImageCodec.DecodePngAsync(redacted);
var sample = (70 * image.Width + 60) * 4;
if (image.Pixels[sample] != 0 || image.Pixels[sample + 1] != 0 || image.Pixels[sample + 2] != 0)
    throw new InvalidDataException("Solid redaction did not cover higher annotation layers.");
var options = new ImageExportOptions(Width: 320, Padding: 24, CornerRadius: 16, Shadow: true, Background: ExportBackground.Charcoal);
var styled = await ImageExporter.EncodeAsync(renderer, document, options);
var decoded = await ImageCodec.DecodePngAsync(styled);
if (decoded.Width != 368 || decoded.Height != 168) throw new InvalidDataException("Styled export dimensions are incorrect.");
var jpeg = await ImageExporter.EncodeAsync(renderer, document, options with { Format = ImageExportFormat.Jpeg });
if (jpeg.Length < 2 || jpeg[0] != 0xff || jpeg[1] != 0xd8) throw new InvalidDataException("JPEG export is invalid.");
Console.WriteLine("PASS rendered solid redaction, resized/padded/rounded/shadowed PNG and JPEG exports.");
