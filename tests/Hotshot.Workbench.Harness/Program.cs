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

var baselinePixels = Enumerable.Range(0, 80 * 100).SelectMany(i => new byte[]
    { (byte)((i * 29) % 251), (byte)((i * 17 + i / 80 * 11) % 251), (byte)((i * 7) % 251), 255 }).ToArray();
var baseline = new CapturedImage(80, 100, baselinePixels);
var modifiedPixels = baselinePixels.ToArray();
modifiedPixels[(25 * 80 + 17) * 4] ^= 255;
var comparison = ImageComparison.Compare(baseline, new CapturedImage(80, 100, modifiedPixels), tolerance: 0);
if (comparison.ChangedPixels != 1 || comparison.ChangedBounds != new Hotshot.Capture.PixelRect(17, 25, 1, 1) ||
    Math.Abs(comparison.ChangedPercent - 100d / 8000) > 1e-9) throw new InvalidDataException("Pixel diff thresholds/geometry are wrong.");
if (ImageComparison.Compare(baseline, baseline).ChangedBounds is not null) throw new InvalidDataException("Identical images differ.");
var scroll = new ScrollingImage(baseline.Crop(new Hotshot.Capture.PixelRect(0, 0, 80, 70)));
if (scroll.Append(baseline.Crop(new Hotshot.Capture.PixelRect(0, 30, 80, 70))) != 40 ||
    scroll.Image.Height != 100 || !scroll.Image.Pixels.SequenceEqual(baseline.Pixels))
    throw new InvalidDataException("Scrolling overlap did not preserve exact pixels.");
try
{
    var uniform = new CapturedImage(80, 70, new byte[80 * 70 * 4]);
    new ScrollingImage(uniform).Append(uniform);
    throw new InvalidDataException("Ambiguous scrolling frames were accepted.");
}
catch (InvalidOperationException) { }
Console.WriteLine("PASS exact changed-pixel count/bounds/percentage, image crops, scrolling overlap and ambiguous-frame rejection.");

if (args.Contains("--video"))
{
    var directory = Path.Combine(Path.GetTempPath(), "Hotshot-video-edit-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var composition = new Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(Windows.Media.Editing.MediaClip.CreateFromColor(Color.FromArgb(255, 255, 69, 0), TimeSpan.FromSeconds(2)));
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync("generated.mp4");
        var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(Windows.Media.MediaProperties.VideoEncodingQuality.Vga);
        if (await composition.RenderToFileAsync(file, Windows.Media.Editing.MediaTrimmingPreference.Precise, profile) != Windows.Media.Transcoding.TranscodeFailureReason.None)
            throw new InvalidDataException("Could not generate synthetic video.");
        var emit = Array.IndexOf(args, "--emit-video");
        if (emit >= 0)
        {
            if (emit + 1 >= args.Length) throw new ArgumentException("--emit-video requires a test fixture path.");
            File.Copy(file.Path, args[emit + 1], overwrite: false);
        }
        var frame = await Hotshot.Recording.VideoEditing.FrameAsync(file.Path, TimeSpan.FromSeconds(0.5));
        if (frame.Pixels[2] < 240 || frame.Pixels[1] is < 55 or > 85) throw new InvalidDataException("Extracted frame differs from generated color.");
        var output = Path.Combine(directory, "trimmed.mp4");
        await Hotshot.Recording.VideoEditing.TrimAsync(file.Path, output, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(output));
        if (Math.Abs(clip.OriginalDuration.TotalSeconds - 1) > 0.15) throw new InvalidDataException("Trim output duration is incorrect.");
        var journal = Path.Combine(directory, "pending.json");
        await Hotshot.Recording.RecordingRecovery.BeginAsync(journal, new Hotshot.Recording.PendingRecording(output, frame.Width, frame.Height));
        var recovery = await Hotshot.Recording.RecordingRecovery.InspectAsync(journal);
        if (recovery?.Path != output || recovery.Duration <= TimeSpan.Zero) throw new InvalidDataException("Playable recording recovery failed.");
        Hotshot.Recording.RecordingRecovery.Complete(journal);
        Console.WriteLine("PASS synthetic MP4 playback metadata, frame extraction, precise trim duration and playable-recording recovery.");
    }
    finally { Directory.Delete(directory, true); }
}
