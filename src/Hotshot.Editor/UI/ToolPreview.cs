using Hotshot.Editor.Model;
using Hotshot.Editor.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Hotshot.Editor.UI;

internal sealed class ToolPreview : UserControl, IDisposable
{
    private readonly CanvasControl _canvas = new() { Width = 112, Height = 40, IsHitTestVisible = false };
    private readonly RenderTargetPair _targets = new();
    private CanvasBitmap? _background;
    private DocumentRenderer? _renderer;
    private Annotation? _annotation;
    private EditorTool _tool;
    private bool _disposed;

    public ToolPreview()
    {
        Content = _canvas;
        IsTabStop = false;
        _canvas.Draw += Draw;
    }

    public event Action<string>? Failed;
    internal CanvasControl Canvas => _canvas;
    internal Annotation? Annotation => _annotation;

    public void Update(EditorState state)
    {
        _tool = state.Tool;
        _annotation = AnnotationPreview.Create(state);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this,
            _annotation is { } annotation ? $"{annotation.Kind} preview, {annotation.Color.ToHex()}, {annotation.StrokeWidth:0.#} pixels" : $"{_tool} preview");
        _canvas.Invalidate();
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_disposed) return;
        var drawing = args.DrawingSession;
        drawing.Clear(default(Color));
        try
        {
            Render(drawing, sender.Device);
        }
        catch (Exception exception)
        {
            Failed?.Invoke("Could not show the tool preview: " + exception.Message);
        }
    }

    internal void Render(CanvasDrawingSession drawing, CanvasDevice device)
    {
        if (_annotation is not { } annotation)
        {
            drawing.DrawText(_tool.ToString(), 8, 10,
                ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
            return;
        }
        if (_renderer is null || _renderer.Device != device)
        {
            ReleaseResources();
            var pixels = new byte[512 * 320 * 4];
            for (var y = 0; y < 320; y++)
                for (var x = 0; x < 512; x++)
                {
                    var offset = (y * 512 + x) * 4;
                    var shade = (byte)((x / 12 + y / 12) % 2 == 0 ? 248 : 224);
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = shade;
                    pixels[offset + 3] = 255;
                }
            _background = CanvasBitmap.CreateFromBytes(device, pixels, 512, 320, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            _renderer = new DocumentRenderer(_background);
        }
        var document = new AnnotationDocument(512, 320);
        document.Add(annotation);
        _renderer.MeasureAll(document);
        var rendered = _renderer.Render(document, _targets);
        var bounds = annotation.GetVisualBounds().Inflate(8);
        var scale = Math.Min(112 / bounds.Width, 40 / bounds.Height);
        var width = bounds.Width * scale;
        var height = bounds.Height * scale;
        drawing.DrawImage(rendered, new Windows.Foundation.Rect((112 - width) / 2, (40 - height) / 2, width, height),
            bounds.ToRect(), 1, CanvasImageInterpolation.Linear);
    }

    private void ReleaseResources()
    {
        _targets.Dispose();
        _renderer?.Dispose();
        _renderer = null;
        _background?.Dispose();
        _background = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
        _canvas.RemoveFromVisualTree();
    }
}
