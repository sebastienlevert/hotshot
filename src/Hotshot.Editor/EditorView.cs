using System.Numerics;
using Hotshot.Editor.Model;
using Hotshot.Editor.Rendering;
using Hotshot.Editor.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Hotshot.Editor;

/// <summary>Code-only, reusable annotation surface for the capture/history workspace.</summary>
public sealed class EditorView : UserControl, IDisposable
{
    private readonly CanvasControl _canvas = new();
    private readonly RenderTargetPair _targets = new();
    private readonly TaskCompletionSource<CanvasDevice> _deviceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ComboBox _tools = new() { ItemsSource = Enum.GetValues<EditorTool>(), SelectedItem = EditorTool.Arrow, MinWidth = 110 };
    private readonly ComboBox _colors = new() { ItemsSource = RgbaColor.Palette.Select(p => p.Name).ToArray(), SelectedIndex = 0, MinWidth = 92 };
    private readonly ComboBox _widths = new() { ItemsSource = StylePresets.StrokeNames, SelectedIndex = 1, MinWidth = 90 };
    private readonly TextBox _text = new() { PlaceholderText = "Annotation text", MinWidth = 150, Visibility = Visibility.Collapsed };
    private readonly AppBarButton _undo = new() { Label = "Undo", Icon = Icons.Glyph(Icons.Undo), IsEnabled = false };
    private readonly AppBarButton _redo = new() { Label = "Redo", Icon = Icons.Glyph(Icons.Redo), IsEnabled = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private EditorOptions? _options;
    private EditorState? _state;
    private DocumentRenderer? _renderer;
    private CanvasBitmap? _bitmap;
    private byte[]? _sourceBytes;
    private Annotation? _drawing;
    private Vector2 _anchor;
    private Vector2 _previous;
    private bool _moving;
    private bool _cropping;
    private PixelRect _cropViewport;
    private float _scale = 1;
    private Vector2 _offset;
    private PixelRect _viewport;
    private bool _updating;
    private bool _busy;
    private bool _disposed;

    public EditorView()
    {
        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true };
        AutomationProperties.SetName(_tools, "Annotation tool");
        AutomationProperties.SetName(_colors, "Annotation color");
        AutomationProperties.SetName(_widths, "Stroke thickness");
        AutomationProperties.SetName(_text, "Annotation text");
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _tools });
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _colors });
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _widths });
        commands.PrimaryCommands.Add(_undo);
        commands.PrimaryCommands.Add(_redo);
        var delete = new AppBarButton { Label = "Delete annotation", Icon = Icons.Glyph(Icons.Delete) };
        delete.Click += (_, _) => _state?.DeleteSelected();
        commands.SecondaryCommands.Add(delete);
        var resetCrop = new AppBarButton { Label = "Reset crop", Icon = Icons.Glyph(Icons.ClearCrop) };
        resetCrop.Click += (_, _) => _state?.Document.SetCrop(null);
        commands.SecondaryCommands.Add(resetCrop);
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _text });
        root.Children.Add(commands);
        Grid.SetRow(_canvas, 1);
        root.Children.Add(_canvas);
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);
        Content = root;
        AutomationProperties.SetName(_canvas, "Annotation canvas");
        AutomationProperties.SetAutomationId(_canvas, "AnnotationCanvas");
        _canvas.CreateResources += (sender, args) =>
        {
            _deviceReady.TrySetResult(sender.Device);
            if (_renderer is not null && _renderer.Device != sender.Device)
                args.TrackAsyncAction(ReloadDeviceAsync(sender.Device).AsAsyncAction());
        };
        _canvas.Draw += Draw;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += (_, _) => CancelGesture();
        _canvas.PointerCaptureLost += (_, _) => CancelGesture();
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
        _tools.SelectionChanged += (_, _) =>
        {
            if (!_updating && _state is { } state && _tools.SelectedItem is EditorTool tool)
            {
                state.Select(null);
                state.Tool = tool;
            }
        };
        _colors.SelectionChanged += (_, _) =>
        {
            if (!_updating && _colors.SelectedIndex >= 0) _state?.SetColor(RgbaColor.Palette[_colors.SelectedIndex].Color);
        };
        _widths.SelectionChanged += (_, _) =>
        {
            if (!_updating && _widths.SelectedIndex >= 0) _state?.SetStrokeWidth(StylePresets.StrokeWidths[_widths.SelectedIndex]);
        };
        _text.TextChanged += (_, _) =>
        {
            if (!_updating && _state?.Selected is TextAnnotation selected)
                _state.Document.Update(selected.Id, a => ((TextAnnotation)a).Text = _text.Text);
        };
        _undo.Click += (_, _) => _state?.Document.Undo();
        _redo.Click += (_, _) => _state?.Document.Redo();
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, () => _state?.Document.Undo());
        AddAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, () => _state?.Document.Redo());
        AddAccelerator(VirtualKey.Delete, VirtualKeyModifiers.None, () => _state?.DeleteSelected());
    }

    public event EventHandler<EditorSavedEventArgs>? Saved;
    public event Action<string>? Failed;
    public bool HasImage => _renderer is not null && _state is not null;
    public bool IsDirty => _state?.Document.IsDirty ?? false;
    public bool IsBusy => _busy;

    public async Task LoadAsync(EditorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_busy) throw new InvalidOperationException("The editor is busy.");
        SetBusy(true);
        try
        {
            ReleaseDocument();
            await _deviceReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var device = _canvas.Device;
            var hasDocument = File.Exists(options.OriginalBackupPath) && File.Exists(options.AnnotationsPath);
            var source = hasDocument ? options.OriginalBackupPath : options.ImagePath;
            byte[] bytes;
            using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
            using (var buffer = new MemoryStream())
            {
                await file.CopyToAsync(buffer);
                bytes = buffer.ToArray();
            }
            var bitmap = await ImageExporter.LoadBitmapAsync(device, bytes);
            AnnotationDocument document;
            try
            {
                document = hasDocument
                    ? AnnotationDocument.FromJson(await File.ReadAllTextAsync(options.AnnotationsPath))
                    : new AnnotationDocument((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
                if (document.ImageWidth != bitmap.SizeInPixels.Width || document.ImageHeight != bitmap.SizeInPixels.Height)
                    throw new InvalidDataException("The annotation document does not match its original image.");
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
            _options = options;
            _sourceBytes = bytes;
            _bitmap = bitmap;
            _renderer = new DocumentRenderer(bitmap);
            _state = new EditorState(document);
            _state.Changed += StateChanged;
            Refresh();
        }
        catch (Exception ex)
        {
            Error($"Could not open this screenshot: {ex.Message}");
            throw;
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (_busy || _state is not { } state || _renderer is not { } renderer || _options is not { } options)
            return false;
        CancelGesture();
        SetBusy(true);
        _status.Text = "Saving image...";
        try
        {
            var png = await Task.Run(() => ImageExporter.EncodeAsync(renderer, state.Document));
            var json = state.Document.ToJson();
            var original = _sourceBytes!;
            long fileSize = png.LongLength;
            await Task.Run(async () =>
            {
                if (!File.Exists(options.OriginalBackupPath))
                    await ImageExporter.WriteFileAtomicAsync(options.OriginalBackupPath, original);
                if (options.SaveImageAsync is { } save) fileSize = await save(png);
                else await ImageExporter.WriteFileAtomicAsync(options.ImagePath, png);
                await ImageExporter.WriteFileAtomicAsync(options.AnnotationsPath, System.Text.Encoding.UTF8.GetBytes(json));
            });
            state.Document.MarkSaved();
            var crop = CropMath.OutputRect(state.Document.Crop, renderer.Width, renderer.Height);
            Saved?.Invoke(this, new EditorSavedEventArgs
            {
                Path = options.ImagePath, IsSaveAs = false, Width = crop.Width, Height = crop.Height, FileSize = fileSize,
            });
            _status.Text = "Image saved. Your original and editable annotations are kept.";
            return true;
        }
        catch (Exception ex)
        {
            Error($"Could not save this screenshot: {ex.Message}");
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task CopyAsync()
    {
        if (_busy || _state is not { } state || _renderer is not { } renderer || _options?.CopyPngToClipboard is not { } copy)
            throw new InvalidOperationException("No screenshot is ready to copy.");
        CancelGesture();
        SetBusy(true);
        try
        {
            var png = await Task.Run(() => ImageExporter.EncodeAsync(renderer, state.Document));
            await copy(png);
            _status.Text = "Edited image copied to the clipboard.";
        }
        catch (Exception ex)
        {
            Error($"Could not copy this screenshot: {ex.Message}");
        }
        finally { SetBusy(false); }
    }

    public void Clear()
    {
        if (_busy) throw new InvalidOperationException("The editor is busy.");
        ReleaseDocument();
        _canvas.Invalidate();
    }

    private async Task ReloadDeviceAsync(CanvasDevice device)
    {
        if (_sourceBytes is null || _renderer is null || _busy) return;
        var bitmap = await ImageExporter.LoadBitmapAsync(device, _sourceBytes);
        _targets.Dispose();
        _renderer.ReplaceBaseImage(bitmap);
        _bitmap?.Dispose();
        _bitmap = bitmap;
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 24, 24, 24) : Color.FromArgb(255, 235, 235, 235));
        if (_busy || _renderer is not { } renderer || _state is not { } state) return;
        try
        {
            _viewport = _cropping ? _cropViewport : CropMath.OutputRect(state.Document.Crop, renderer.Width, renderer.Height);
            _scale = Math.Max(0.01f, Math.Min((float)(sender.ActualWidth - 32) / _viewport.Width, (float)(sender.ActualHeight - 32) / _viewport.Height));
            _offset = new Vector2((float)(sender.ActualWidth - _viewport.Width * _scale) / 2, (float)(sender.ActualHeight - _viewport.Height * _scale) / 2);
            var rendered = renderer.Render(state.Document, _targets);
            ds.DrawImage(rendered,
                new Windows.Foundation.Rect(_offset.X, _offset.Y, _viewport.Width * _scale, _viewport.Height * _scale),
                new Windows.Foundation.Rect(_viewport.X, _viewport.Y, _viewport.Width, _viewport.Height), 1, CanvasImageInterpolation.Linear);
            ds.Transform = Matrix3x2.CreateScale(_scale) * Matrix3x2.CreateTranslation(_offset - new Vector2(_viewport.X, _viewport.Y) * _scale);
            var accent = Color.FromArgb(255, 10, 132, 255);
            if (_cropping && state.Document.Crop is { } crop) ds.DrawRectangle(crop.ToRect(), accent, 2 / _scale);
            if (state.Selected is { } selected) ds.DrawRectangle(selected.GetVisualBounds().ToRect(), accent, 1.5f / _scale);
        }
        catch (Exception ex) { Error($"The editor could not render this image: {ex.Message}"); }
    }

    private Vector2 ImagePoint(PointerRoutedEventArgs args)
    {
        var p = args.GetCurrentPoint(_canvas).Position;
        var point = (new Vector2((float)p.X, (float)p.Y) - _offset) / _scale + new Vector2(_viewport.X, _viewport.Y);
        return _state is { } state ? Vector2.Clamp(point, Vector2.Zero, new Vector2(state.Document.ImageWidth, state.Document.ImageHeight)) : point;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_busy || _state is not { } state || !args.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        var point = ImagePoint(args);
        _anchor = _previous = point;
        _canvas.Focus(FocusState.Pointer);
        if (state.Tool == EditorTool.Select)
        {
            var selected = state.Document.HitTest(point, 6 / _scale);
            state.Select(selected?.Id);
            if (selected is null) return;
            state.Document.BeginTransaction();
            _drawing = selected;
            _moving = true;
        }
        else if (state.Tool == EditorTool.Crop)
        {
            _cropViewport = _viewport;
            _cropping = true;
            state.Document.BeginTransaction();
        }
        else
        {
            var annotation = state.CreateAnnotation(state.Tool, point);
            if (annotation is null) return;
            if (annotation is TextAnnotation text) text.Text = string.IsNullOrWhiteSpace(_text.Text) ? "Text" : _text.Text;
            state.Document.BeginTransaction();
            state.Document.AddTransient(annotation);
            _drawing = annotation;
            state.Select(annotation.Id);
        }
        _canvas.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_state is not { } state || !state.Document.InTransaction) return;
        var point = ImagePoint(args);
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        if (_cropping) state.Document.SetCropTransient(RectF.FromPoints(_anchor, point));
        else if (_drawing is { } annotation)
        {
            if (_moving) annotation.Translate(point - _previous);
            else switch (annotation)
            {
                case SegmentAnnotation segment: segment.End = shift ? GeometryMath.SnapAngle(_anchor, point) : point; break;
                case BoxAnnotation box: box.Rect = RectF.FromPoints(_anchor, shift ? GeometryMath.ConstrainSquare(_anchor, point) : point); break;
                case PenAnnotation pen: pen.Points.Add(point); break;
            }
            state.Document.ReplaceTransient(annotation);
        }
        _previous = point;
        args.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_state is not { } state || !state.Document.InTransaction) return;
        OnPointerMoved(sender, args);
        state.Document.CommitTransaction();
        _drawing = null;
        _moving = _cropping = false;
        _canvas.ReleasePointerCapture(args.Pointer);
        _canvas.Invalidate();
        args.Handled = true;
    }

    private void CancelGesture()
    {
        _state?.Document.CancelTransaction();
        _drawing = null;
        _moving = _cropping = false;
    }

    private void StateChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        if (_state is not { } state) return;
        _updating = true;
        _tools.SelectedItem = state.Tool;
        var style = state.CurrentStyle;
        _colors.SelectedIndex = RgbaColor.Palette.ToList().FindIndex(p => p.Color == style.Color);
        _widths.SelectedIndex = StylePresets.NearestStrokeIndex(style.StrokeWidth);
        _text.Visibility = state.Tool == EditorTool.Text || state.Selected is TextAnnotation ? Visibility.Visible : Visibility.Collapsed;
        if (state.Selected is TextAnnotation text) _text.Text = text.Text;
        _undo.IsEnabled = state.Document.CanUndo;
        _redo.IsEnabled = state.Document.CanRedo;
        _updating = false;
        _status.Text = $"{state.Document.ImageWidth} x {state.Document.ImageHeight} pixels{(IsDirty ? " - unsaved changes" : string.Empty)}";
        _canvas.Invalidate();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        IsEnabled = !busy;
        if (!busy && !_disposed) _canvas.Invalidate();
    }

    private void Error(string message)
    {
        _status.Text = message;
        Failed?.Invoke(message);
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            if (_busy || _text.FocusState != FocusState.Unfocused) return;
            action();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private void ReleaseDocument()
    {
        CancelGesture();
        if (_state is not null) _state.Changed -= StateChanged;
        _state = null;
        _options = null;
        _sourceBytes = null;
        _targets.Dispose();
        _renderer?.Dispose();
        _renderer = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseDocument();
        _canvas.RemoveFromVisualTree();
    }
}
