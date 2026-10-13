using System.Numerics;
using System.Runtime.InteropServices;
using Hotshot.Editor.Model;
using Hotshot.Editor.Rendering;
using Hotshot.Editor.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Hotshot.Editor;

/// <summary>Code-only, reusable annotation surface for the capture/history workspace.</summary>
public sealed class EditorView : UserControl, IDisposable
{
    private readonly CanvasControl _canvas = new() { IsTabStop = true, UseSystemFocusVisuals = true };
    private readonly RenderTargetPair _targets = new();
    private readonly TaskCompletionSource<CanvasDevice> _deviceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ComboBox _tools = new() { ItemsSource = Enum.GetValues<EditorTool>(), SelectedItem = EditorTool.Arrow, MinWidth = 110 };
    private readonly DropDownButton _colors = new() { MinWidth = 64, VerticalAlignment = VerticalAlignment.Center };
    private readonly ColorPicker _colorPicker = new()
    {
        Color = DocumentRenderer.ToColor(RgbaColor.Red), Width = 280,
        IsAlphaEnabled = true, IsAlphaSliderVisible = true, IsAlphaTextInputVisible = true,
        IsHexInputVisible = true, IsColorChannelTextInputVisible = true,
    };
    private readonly Border _colorSwatch = new() { Width = 24, Height = 20, CornerRadius = new CornerRadius(3) };
    private readonly ToolPreview _preview = new();
    private readonly Dictionary<InputSystemCursorShape, InputSystemCursor> _cursors = [];
    private readonly ComboBox _widths = new() { ItemsSource = StylePresets.StrokeNames, SelectedIndex = 1, MinWidth = 90 };
    private readonly TextBox _text = new() { PlaceholderText = "Annotation text", MinWidth = 150, Visibility = Visibility.Collapsed };
    private readonly ComboBox _layers = new() { PlaceholderText = "Select annotation", MinWidth = 140, MaxWidth = 200 };
    private readonly CheckBox _filled = new() { Content = "Filled", Visibility = Visibility.Collapsed };
    private readonly NumberBox _fontSize = new() { Minimum = 8, Maximum = 144, Value = 24, MinWidth = 100, Visibility = Visibility.Collapsed };
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
    private HandleKind? _resizeHandle;
    private bool _cropping;
    private PixelRect _cropViewport;
    private float _scale = 1;
    private Vector2 _offset;
    private readonly EditorViewport _navigation = new();
    private readonly TextBlock _zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Windows.UI.ViewManagement.UISettings _systemColors = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibility = new();
    private readonly Windows.Foundation.TypedEventHandler<Windows.UI.ViewManagement.UISettings, object> _colorsChanged;
    private readonly Windows.Foundation.TypedEventHandler<Windows.UI.ViewManagement.AccessibilitySettings, object> _contrastChanged;
    private readonly bool _contrastNotificationsAvailable;
    private Control? _focusBeforeBusy;
    private bool _panning;
    private Vector2 _panPoint;
    private PixelRect _viewport;
    private bool _updating;
    private bool _busy;
    private bool _hostBusy;
    private bool _disposed;
    private bool _pointerInCanvas;
    private Vector2? _hoverPoint;

    public EditorView()
    {
        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true };
        AutomationProperties.SetAutomationId(commands, "EditorCommands");
        AutomationProperties.SetName(commands, "Annotation commands");
        AutomationProperties.SetAutomationId(_zoomLabel, "EditorZoomStatus");
        AutomationProperties.SetAutomationId(_status, "EditorStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_tools, "Annotation tool");
        AutomationProperties.SetAutomationId(_tools, "EditorTool");
        AutomationProperties.SetName(_colors, "Annotation color");
        AutomationProperties.SetAutomationId(_colors, "EditorColor");
        AutomationProperties.SetAutomationId(_colorPicker, "EditorColorPicker");
        AutomationProperties.SetName(_colorPicker, "Choose annotation color");
        AutomationProperties.SetAutomationId(_preview, "EditorToolPreview");
        var swatch = new Grid { Width = 24, Height = 20 };
        swatch.ColumnDefinitions.Add(new ColumnDefinition());
        swatch.ColumnDefinitions.Add(new ColumnDefinition());
        swatch.RowDefinitions.Add(new RowDefinition());
        swatch.RowDefinitions.Add(new RowDefinition());
        for (var index = 0; index < 4; index++)
        {
            var cell = new Border { Background = new SolidColorBrush(Color.FromArgb(255,
                (byte)(index is 0 or 3 ? 248 : 200), (byte)(index is 0 or 3 ? 248 : 200), (byte)(index is 0 or 3 ? 248 : 200))) };
            Grid.SetRow(cell, index / 2);
            Grid.SetColumn(cell, index % 2);
            swatch.Children.Add(cell);
        }
        Grid.SetRowSpan(_colorSwatch, 2);
        Grid.SetColumnSpan(_colorSwatch, 2);
        swatch.Children.Add(_colorSwatch);
        _colors.Content = swatch;
        _colors.Flyout = new Flyout { Content = _colorPicker };
        ToolTipService.SetToolTip(_colors, "Choose any color");
        AutomationProperties.SetName(_widths, "Stroke thickness");
        AutomationProperties.SetAutomationId(_widths, "EditorStroke");
        AutomationProperties.SetName(_text, "Annotation text");
        AutomationProperties.SetAutomationId(_text, "EditorText");
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _tools });
        ToolTipService.SetToolTip(_tools, "Select moves your marks. Redact hides details in saved copies; private originals are kept.");
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _colors });
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _widths });
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _preview });
        commands.PrimaryCommands.Add(_undo);
        commands.PrimaryCommands.Add(_redo);
        var fit = new AppBarButton { Label = "Fit image", Icon = new SymbolIcon(Symbol.ZoomOut) };
        fit.Click += (_, _) => { _navigation.Reset(); _canvas.Invalidate(); };
        var actual = new AppBarButton { Label = "100% zoom", Icon = new SymbolIcon(Symbol.ZoomIn) };
        actual.Click += (_, _) => ZoomTo((float)(1 / (XamlRoot?.RasterizationScale ?? 1)));
        commands.PrimaryCommands.Add(fit);
        commands.PrimaryCommands.Add(actual);
        AutomationProperties.SetName(_layers, "Annotation layers");
        AutomationProperties.SetAutomationId(_layers, "EditorLayers");
        AutomationProperties.SetName(_filled, "Fill selected shape");
        AutomationProperties.SetAutomationId(_filled, "EditorFilled");
        AutomationProperties.SetName(_fontSize, "Annotation font size");
        AutomationProperties.SetAutomationId(_fontSize, "EditorFontSize");
        commands.SecondaryCommands.Add(new AppBarElementContainer { Content = _layers });
        commands.SecondaryCommands.Add(new AppBarElementContainer { Content = _filled });
        commands.SecondaryCommands.Add(new AppBarElementContainer { Content = _fontSize });
        var duplicate = new AppBarButton { Label = "Duplicate annotation", Icon = new SymbolIcon(Symbol.Copy) };
        duplicate.Click += (_, _) => DuplicateSelected();
        commands.SecondaryCommands.Add(duplicate);
        foreach (var (label, front) in new[] { ("Bring to front", true), ("Send to back", false) })
        {
            var layer = new AppBarButton { Label = label, Icon = new SymbolIcon(Symbol.Sort) };
            layer.Click += (_, _) =>
            {
                if (_state?.SelectedId is { } id)
                    _state.Document.MoveLayer(id, front ? _state.Document.Annotations.Count - 1 : 0);
            };
            commands.SecondaryCommands.Add(layer);
        }
        var delete = new AppBarButton { Label = "Delete annotation", Icon = Icons.Glyph(Icons.Delete) };
        delete.Click += (_, _) => _state?.DeleteSelected();
        commands.SecondaryCommands.Add(delete);
        var resetCrop = new AppBarButton { Label = "Reset crop", Icon = Icons.Glyph(Icons.ClearCrop) };
        resetCrop.Click += (_, _) => _state?.Document.SetCrop(null);
        commands.SecondaryCommands.Add(resetCrop);
        commands.PrimaryCommands.Add(new AppBarElementContainer { Content = _text });
        foreach (var container in commands.PrimaryCommands.Concat(commands.SecondaryCommands).OfType<AppBarElementContainer>())
        {
            container.Margin = new Thickness(0, 0, 8, 0);
            container.MinHeight = 48;
            container.VerticalAlignment = VerticalAlignment.Center;
            if (container.Content is FrameworkElement control) control.VerticalAlignment = VerticalAlignment.Center;
        }
        foreach (var command in commands.PrimaryCommands.Concat(commands.SecondaryCommands).OfType<AppBarButton>())
            AutomationProperties.SetName(command, command.Label);
        AutomationProperties.SetAutomationId(_undo, "EditorUndo");
        AutomationProperties.SetAutomationId(_redo, "EditorRedo");
        AutomationProperties.SetAcceleratorKey(_undo, "Ctrl+Z");
        AutomationProperties.SetAcceleratorKey(_redo, "Ctrl+Y");
        root.Children.Add(commands);
        Grid.SetRow(_canvas, 1);
        root.Children.Add(_canvas);
        var statusPanel = new StackPanel { Spacing = 2 };
        statusPanel.Children.Add(_status);
        statusPanel.Children.Add(_zoomLabel);
        Grid.SetRow(statusPanel, 2);
        root.Children.Add(statusPanel);
        Content = root;
        AutomationProperties.SetName(_canvas, "Annotation canvas");
        AutomationProperties.SetAutomationId(_canvas, "AnnotationCanvas");
        AutomationProperties.SetHelpText(_canvas,
            "Insert adds the chosen annotation or a crop. Page Up and Page Down select annotations. " +
            "Arrow keys move; Ctrl+arrows resize; Shift uses 10 pixels. Alt+arrows pan; plus and minus zoom. " +
            "Ctrl+0 fits; Ctrl+1 shows actual pixels. Escape cancels a gesture or clears selection.");
        AutomationProperties.GetDescribedBy(_canvas).Add(_status);
        AutomationProperties.GetControlledPeers(_layers).Add(_canvas);
        _canvas.CreateResources += (sender, args) =>
        {
            _deviceReady.TrySetResult(sender.Device);
            if (_renderer is not null && _renderer.Device != sender.Device)
                args.TrackAsyncAction(ReloadDeviceAsync(sender.Device).AsAsyncAction());
        };
        _canvas.Draw += Draw;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerEntered += (_, args) => UpdateCanvasPointer(true, ImagePoint(args));
        _canvas.PointerExited += (_, _) => UpdateCanvasPointer(false);
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += (_, _) => CancelGesture();
        _canvas.PointerCaptureLost += (_, _) => CancelGesture();
        _canvas.PointerWheelChanged += (_, args) =>
        {
            if (_busy || !HasImage) return;
            var pointer = args.GetCurrentPoint(_canvas);
            _navigation.Zoom(_scale * MathF.Pow(1.2f, pointer.Properties.MouseWheelDelta / 120f),
                new Vector2((float)pointer.Position.X, (float)pointer.Position.Y));
            _canvas.Invalidate();
            args.Handled = true;
        };
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();
        _canvas.KeyDown += (_, args) =>
        {
            var modifiers = VirtualKeyModifiers.None;
            if (IsKeyDown(VirtualKey.Control)) modifiers |= VirtualKeyModifiers.Control;
            if (IsKeyDown(VirtualKey.Shift)) modifiers |= VirtualKeyModifiers.Shift;
            if (IsKeyDown(VirtualKey.Menu)) modifiers |= VirtualKeyModifiers.Menu;
            args.Handled = ProcessCanvasKey(args.Key, modifiers);
        };
        ActualThemeChanged += (_, _) => UpdateAppearance();
        _tools.SelectionChanged += (_, _) =>
        {
            if (!_updating && _state is { } state && _tools.SelectedItem is EditorTool tool)
            {
                state.Select(null);
                state.Tool = tool;
            }
        };
        _colorPicker.ColorChanged += (_, args) =>
        {
            if (!_updating) _state?.SetColor(new RgbaColor(args.NewColor.R, args.NewColor.G, args.NewColor.B, args.NewColor.A));
        };
        _preview.Failed += Error;
        _widths.SelectionChanged += (_, _) =>
        {
            if (!_updating && _widths.SelectedIndex >= 0) _state?.SetStrokeWidth(StylePresets.StrokeWidths[_widths.SelectedIndex]);
        };
        _text.TextChanged += (_, _) =>
        {
            if (!_updating && _state?.Selected is TextAnnotation selected)
                _state.Document.Update(selected.Id, a => ((TextAnnotation)a).Text = _text.Text);
        };
        _layers.SelectionChanged += (_, _) =>
        {
            if (_updating || _state is null || _layers.SelectedItem is not ComboBoxItem { Tag: Guid id }) return;
            _state.Tool = EditorTool.Select;
            _state.Select(id);
            AnnounceStatus(SelectionDescription());
        };
        _filled.Checked += (_, _) => { if (!_updating) _state?.SetFilled(true); };
        _filled.Unchecked += (_, _) => { if (!_updating) _state?.SetFilled(false); };
        _fontSize.ValueChanged += (_, args) =>
        {
            if (!_updating && double.IsFinite(args.NewValue)) _state?.SetFontSize((float)args.NewValue);
        };
        _undo.Click += (_, _) => _state?.Document.Undo();
        _redo.Click += (_, _) => _state?.Document.Redo();
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, () => _state?.Document.Undo());
        AddAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, () => _state?.Document.Redo());
        AddAccelerator(VirtualKey.Delete, VirtualKeyModifiers.None, () => _state?.DeleteSelected());
        AddAccelerator(VirtualKey.D, VirtualKeyModifiers.Control, DuplicateSelected);
        AddAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, () => { _navigation.Reset(); _canvas.Invalidate(); });
        AddAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control, () => ZoomTo((float)(1 / (XamlRoot?.RasterizationScale ?? 1))));
        foreach (var (key, tool) in new[] { (VirtualKey.V, EditorTool.Select), (VirtualKey.A, EditorTool.Arrow),
            (VirtualKey.R, EditorTool.Rectangle), (VirtualKey.T, EditorTool.Text), (VirtualKey.C, EditorTool.Crop),
            (VirtualKey.P, EditorTool.Pen) })
            AddAccelerator(key, VirtualKeyModifiers.None, () => { if (_state is { } state) state.Tool = tool; });
        _colorsChanged = (_, _) => _canvas.DispatcherQueue.TryEnqueue(UpdateAppearance);
        _contrastChanged = (_, _) => _canvas.DispatcherQueue.TryEnqueue(UpdateAppearance);
        _systemColors.ColorValuesChanged += _colorsChanged;
        try
        {
            _accessibility.HighContrastChanged += _contrastChanged;
            _contrastNotificationsAvailable = true;
        }
        catch (COMException)
        {
            // UISettings color notifications still refresh the current contrast palette on these desktops.
            _contrastNotificationsAvailable = false;
        }
        UpdateAppearance();
    }

    public event EventHandler<EditorSavedEventArgs>? Saved;
    public event Action<string>? Failed;
    public bool HasImage => _renderer is not null && _state is not null;
    public bool IsDirty => _state?.Document.IsDirty ?? false;
    public bool IsBusy => _busy;
    public bool HasRedactions => _state?.Document.Annotations.Any(annotation => annotation is RedactionAnnotation) == true;
    internal bool HighContrastNotificationsAvailable => _contrastNotificationsAvailable;
    internal InputSystemCursorShape? EditingCursorShape => (ProtectedCursor as InputSystemCursor)?.CursorShape;

    internal void UpdateCanvasPointer(bool isInside, Vector2? point = null)
    {
        _pointerInCanvas = isInside;
        _hoverPoint = isInside ? point : null;
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (_disposed || !_pointerInCanvas || _state is not { } state)
        {
            ProtectedCursor = null;
            return;
        }
        var shape = _busy || _hostBusy ? InputSystemCursorShape.Wait :
            _panning ? InputSystemCursorShape.Hand :
            state.Tool == EditorTool.Text ? InputSystemCursorShape.IBeam :
            state.Tool == EditorTool.Select ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.Cross;
        var handle = _resizeHandle ?? (_hoverPoint is { } point
            ? state.Selected?.GetHandles().FirstOrDefault(candidate => Vector2.Distance(candidate.Position, point) <= 8 / _scale).Kind
            : null);
        if (state.Tool == EditorTool.Select && !_busy && !_hostBusy && !_panning)
            shape = handle switch
            {
                HandleKind.TopLeft or HandleKind.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
                HandleKind.TopRight or HandleKind.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
                HandleKind.Top or HandleKind.Bottom => InputSystemCursorShape.SizeNorthSouth,
                HandleKind.Left or HandleKind.Right => InputSystemCursorShape.SizeWestEast,
                _ => shape,
            };
        if (!_cursors.TryGetValue(shape, out var cursor))
        {
            cursor = InputSystemCursor.Create(shape);
            _cursors.Add(shape, cursor);
        }
        ProtectedCursor = cursor;
    }

    internal bool ProcessCanvasKey(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (_disposed || _busy || _hostBusy || _state is not { } state) return false;
        if (key == VirtualKey.Escape)
        {
            CancelGesture();
            _canvas.ReleasePointerCaptures();
            state.Select(null);
            _canvas.Invalidate();
            AnnounceStatus("Gesture canceled; no annotation selected.");
            return true;
        }
        if (key is VirtualKey.PageUp or VirtualKey.PageDown)
        {
            var count = state.Document.Annotations.Count;
            if (count == 0) return false;
            var current = state.SelectedId is { } selectedId ? state.Document.IndexOf(selectedId) : -1;
            var next = key == VirtualKey.PageDown ? (current + 1) % count : (current <= 0 ? count - 1 : current - 1);
            state.Tool = EditorTool.Select;
            state.Select(state.Document.Annotations[next].Id);
            AnnounceStatus(SelectionDescription());
            return true;
        }
        if (key is VirtualKey.Add or VirtualKey.Subtract or (VirtualKey)187 or (VirtualKey)189)
        {
            var zoomIn = key is VirtualKey.Add or (VirtualKey)187;
            ZoomTo(_navigation.Scale * (zoomIn ? 1.2f : 1 / 1.2f));
            AnnounceStatus(zoomIn ? "Zoomed in." : "Zoomed out.");
            return true;
        }
        if (key == VirtualKey.Insert)
        {
            var viewport = CropMath.OutputRect(state.Document.Crop, state.Document.ImageWidth, state.Document.ImageHeight);
            if (state.Tool == EditorTool.Crop)
            {
                state.Document.SetCrop(new RectF(viewport.X + viewport.Width / 4f, viewport.Y + viewport.Height / 4f,
                    Math.Max(1, viewport.Width / 2f), Math.Max(1, viewport.Height / 2f)));
                AnnounceStatus(SelectionDescription());
                return true;
            }
            var point = new Vector2(viewport.X + viewport.Width / 2f, viewport.Y + viewport.Height / 2f);
            var annotation = state.CreateAnnotation(state.Tool, point);
            switch (annotation)
            {
                case BoxAnnotation box: box.Rect = new RectF(point.X, point.Y, Math.Min(100, viewport.Width / 2f), Math.Min(60, viewport.Height / 2f)); break;
                case SegmentAnnotation line: line.End = point + new Vector2(Math.Min(100, viewport.Width / 2f), Math.Min(60, viewport.Height / 2f)); break;
                case TextAnnotation text: text.Text = string.IsNullOrWhiteSpace(_text.Text) ? "Text" : _text.Text; break;
                case PenAnnotation pen: pen.Points.Add(point + new Vector2(Math.Min(100, viewport.Width / 2f), Math.Min(60, viewport.Height / 2f))); break;
            }
            if (annotation is null) return false;
            state.Document.Add(annotation);
            state.Tool = EditorTool.Select;
            state.Select(annotation.Id);
            AnnounceStatus(SelectionDescription());
            return true;
        }
        var delta = key switch
        {
            VirtualKey.Left => -Vector2.UnitX, VirtualKey.Right => Vector2.UnitX,
            VirtualKey.Up => -Vector2.UnitY, VirtualKey.Down => Vector2.UnitY, _ => Vector2.Zero,
        };
        if (delta == Vector2.Zero) return false;
        if (modifiers.HasFlag(VirtualKeyModifiers.Shift)) delta *= 10;
        if (modifiers.HasFlag(VirtualKeyModifiers.Menu))
        {
            _navigation.Pan(delta * 20);
            _canvas.Invalidate();
            return true;
        }
        var resize = modifiers.HasFlag(VirtualKeyModifiers.Control);
        if (state.Tool == EditorTool.Crop && state.Document.Crop is { } crop)
        {
            state.Document.SetCrop(resize
                ? crop with
                {
                    Width = Math.Clamp(crop.Width + delta.X, 1, state.Document.ImageWidth - crop.X),
                    Height = Math.Clamp(crop.Height + delta.Y, 1, state.Document.ImageHeight - crop.Y),
                }
                : crop with
                {
                    X = Math.Clamp(crop.X + delta.X, 0, state.Document.ImageWidth - crop.Width),
                    Y = Math.Clamp(crop.Y + delta.Y, 0, state.Document.ImageHeight - crop.Height),
                });
        }
        else if (state is { Tool: EditorTool.Select, SelectedId: { } id })
        {
            state.Document.Update(id, annotation =>
            {
                if (resize && annotation is BoxAnnotation box)
                    box.Rect = box.Rect with { Width = Math.Max(1, box.Rect.Width + delta.X), Height = Math.Max(1, box.Rect.Height + delta.Y) };
                else if (resize && annotation is SegmentAnnotation segment) segment.End += delta;
                else annotation.Translate(delta);
            });
        }
        else return false;
        AnnounceStatus(SelectionDescription());
        return true;
    }

    private string SelectionDescription()
    {
        if (_state is not { } state) return "No image loaded.";
        if (state.Tool == EditorTool.Crop && state.Document.Crop is { } crop)
            return $"Crop: X {crop.X:0}, Y {crop.Y:0}, width {crop.Width:0}, height {crop.Height:0} pixels.";
        if (state.Selected is not { } selected) return "No annotation selected.";
        var bounds = selected.GetBounds();
        return $"{selected.Kind} selected: X {bounds.X:0}, Y {bounds.Y:0}, width {bounds.Width:0}, height {bounds.Height:0} pixels.";
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    public async Task<byte[]> ExportPngAsync() => await ExportAsync(new ImageExportOptions());

    public async Task<byte[]> ExportAsync(ImageExportOptions options)
    {
        if (_busy || _renderer is not { } renderer || _state is not { } state)
            throw new InvalidOperationException("No screenshot is ready to export.");
        CancelGesture();
        SetBusy(true);
        try { return await Task.Run(() => ImageExporter.EncodeAsync(renderer, state.Document, options)); }
        finally { SetBusy(false); }
    }

    private void DuplicateSelected()
    {
        if (_state?.SelectedId is { } id) _state.Select(_state.Document.Duplicate(id));
    }

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
            AnnounceStatus(_status.Text);
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
        AnnounceStatus("Saving image...");
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
                if (options.SaveImageAsync is { } save) fileSize = await save(png, HasRedactions);
                else await ImageExporter.WriteFileAtomicAsync(options.ImagePath, png);
                await ImageExporter.WriteFileAtomicAsync(options.AnnotationsPath, System.Text.Encoding.UTF8.GetBytes(json));
            });
            state.Document.MarkSaved();
            var crop = CropMath.OutputRect(state.Document.Crop, renderer.Width, renderer.Height);
            Saved?.Invoke(this, new EditorSavedEventArgs
            {
                Path = options.ImagePath, IsSaveAs = false, Width = crop.Width, Height = crop.Height, FileSize = fileSize,
            });
            AnnounceStatus("Saved. Your original and editable marks are kept.");
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
            AnnounceStatus("Copied. Ready to paste.");
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
        var highContrast = _accessibility.HighContrast;
        ds.Clear(highContrast ? _systemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background) :
            ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 24, 24, 24) : Color.FromArgb(255, 235, 235, 235));
        if (_busy || _renderer is not { } renderer || _state is not { } state) return;
        try
        {
            _viewport = _cropping ? _cropViewport : CropMath.OutputRect(state.Document.Crop, renderer.Width, renderer.Height);
            _navigation.Layout(new Vector2((float)sender.ActualWidth, (float)sender.ActualHeight), new Vector2(_viewport.Width, _viewport.Height));
            _scale = _navigation.Scale;
            _offset = _navigation.Offset;
            _zoomLabel.Text = $"{_scale * (XamlRoot?.RasterizationScale ?? 1):P0} - +/- to zoom; Alt+arrows to pan; Ctrl+0 to fit";
            var rendered = renderer.Render(state.Document, _targets);
            ds.DrawImage(rendered,
                new Windows.Foundation.Rect(_offset.X, _offset.Y, _viewport.Width * _scale, _viewport.Height * _scale),
                new Windows.Foundation.Rect(_viewport.X, _viewport.Y, _viewport.Width, _viewport.Height), 1, CanvasImageInterpolation.Linear);
            ds.Transform = Matrix3x2.CreateScale(_scale) * Matrix3x2.CreateTranslation(_offset - new Vector2(_viewport.X, _viewport.Y) * _scale);
            var accent = highContrast ? _systemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground) : Color.FromArgb(255, 10, 132, 255);
            if (_cropping && state.Document.Crop is { } crop) ds.DrawRectangle(crop.ToRect(), accent, 2 / _scale);
            if (state.Selected is { } selected)
            {
                ds.DrawRectangle(selected.GetVisualBounds().ToRect(), accent, 1.5f / _scale);
                if (state.Tool == EditorTool.Select)
                    foreach (var handle in selected.GetHandles())
                        ds.FillRectangle(handle.Position.X - 3 / _scale, handle.Position.Y - 3 / _scale, 6 / _scale, 6 / _scale, accent);
            }
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
        var pointer = args.GetCurrentPoint(_canvas);
        if (!_busy && HasImage && (pointer.Properties.IsMiddleButtonPressed ||
            pointer.Properties.IsLeftButtonPressed && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Space) & CoreVirtualKeyStates.Down) != 0))
        {
            _panning = true;
            UpdateCursor();
            _panPoint = new Vector2((float)pointer.Position.X, (float)pointer.Position.Y);
            _canvas.CapturePointer(args.Pointer);
            _canvas.Focus(FocusState.Pointer);
            args.Handled = true;
            return;
        }
        if (_busy || _state is not { } state || !args.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        var point = ImagePoint(args);
        _anchor = _previous = point;
        _canvas.Focus(FocusState.Pointer);
        if (state.Tool == EditorTool.Select)
        {
            var handle = state.Selected?.GetHandles().FirstOrDefault(handle => Vector2.Distance(handle.Position, point) <= 8 / _scale);
            _resizeHandle = handle is { Kind: not HandleKind.None } found && Vector2.Distance(found.Position, point) <= 8 / _scale ? found.Kind : null;
            var selected = _resizeHandle is not null ? state.Selected : state.Document.HitTest(point, 6 / _scale);
            state.Select(selected?.Id);
            if (selected is null) return;
            state.Document.BeginTransaction();
            _drawing = selected;
            _moving = _resizeHandle is null;
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
        UpdateCursor();
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerInCanvas) UpdateCanvasPointer(true, ImagePoint(args));
        if (_panning)
        {
            var position = args.GetCurrentPoint(_canvas).Position;
            var panPosition = new Vector2((float)position.X, (float)position.Y);
            _navigation.Pan(panPosition - _panPoint);
            _panPoint = panPosition;
            _canvas.Invalidate();
            args.Handled = true;
            return;
        }
        if (_state is not { } state || !state.Document.InTransaction) return;
        var point = ImagePoint(args);
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        if (_cropping) state.Document.SetCropTransient(RectF.FromPoints(_anchor, point));
        else if (_drawing is { } annotation)
        {
            if (_resizeHandle is { } handle) annotation.MoveHandle(handle, point, shift);
            else if (_moving) annotation.Translate(point - _previous);
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
        if (_panning)
        {
            _panning = false;
            UpdateCursor();
            _canvas.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
            return;
        }
        if (_state is not { } state || !state.Document.InTransaction) return;
        OnPointerMoved(sender, args);
        state.Document.CommitTransaction();
        _drawing = null;
        _moving = _cropping = false;
        _resizeHandle = null;
        _canvas.ReleasePointerCapture(args.Pointer);
        _canvas.Invalidate();
        UpdateCursor();
        args.Handled = true;
    }

    private void CancelGesture()
    {
        _panning = false;
        _state?.Document.CancelTransaction();
        _drawing = null;
        _moving = _cropping = false;
        _resizeHandle = null;
        UpdateCursor();
    }

    private void StateChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        if (_state is not { } state) return;
        var showText = state.Tool == EditorTool.Text || state.Selected is TextAnnotation;
        var showFill = state.StyleKind is { } kind && StyleTraits.UsesFill(kind);
        var showFont = state.StyleKind is { } fontKind && StyleTraits.UsesFontSize(fontKind);
        var recoverFocus = !showText && HasFocusWithin(_text) || !showFill && HasFocusWithin(_filled) || !showFont && HasFocusWithin(_fontSize);
        _updating = true;
        _tools.SelectedItem = state.Tool;
        var style = state.CurrentStyle;
        _colorPicker.Color = DocumentRenderer.ToColor(style.Color);
        _colorSwatch.Background = new SolidColorBrush(DocumentRenderer.ToColor(style.Color));
        ToolTipService.SetToolTip(_colors, "Color " + style.Color.ToHex());
        _widths.SelectedIndex = StylePresets.NearestStrokeIndex(style.StrokeWidth);
        _text.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
        if (state.Selected is TextAnnotation text) _text.Text = text.Text;
        _undo.IsEnabled = state.Document.CanUndo;
        _redo.IsEnabled = state.Document.CanRedo;
        _filled.Visibility = showFill ? Visibility.Visible : Visibility.Collapsed;
        _filled.IsChecked = style.Filled;
        _fontSize.Visibility = showFont ? Visibility.Visible : Visibility.Collapsed;
        _fontSize.Value = style.FontSize;
        // Keep the native ComboBox items stable while editing so its open menu and keyboard focus survive.
        if (!_layers.Items.OfType<ComboBoxItem>().Select(item => item.Tag).SequenceEqual(state.Document.Annotations.Select(annotation => (object)annotation.Id)))
        {
            _layers.Items.Clear();
            foreach (var annotation in state.Document.Annotations)
                _layers.Items.Add(new ComboBoxItem { Tag = annotation.Id });
        }
        ComboBoxItem? selectedItem = null;
        for (var i = 0; i < state.Document.Annotations.Count; i++)
        {
            var annotation = state.Document.Annotations[i];
            var item = (ComboBoxItem)_layers.Items[i];
            item.Content = $"{i + 1}. {annotation.Kind}";
            AutomationProperties.SetName(item, $"{i + 1}. {annotation.Kind}");
            if (annotation.Id == state.SelectedId) selectedItem = item;
        }
        _layers.SelectedItem = selectedItem;
        _updating = false;
        _preview.Update(state);
        UpdateCursor();
        _status.Text = $"{state.Document.ImageWidth} x {state.Document.ImageHeight} pixels{(IsDirty ? " - unsaved changes" : string.Empty)}";
        if (recoverFocus) _canvas.Focus(FocusState.Keyboard);
        _canvas.Invalidate();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateEnabled();
        if (!busy && !_disposed) _canvas.Invalidate();
    }

    public void SetHostBusy(bool busy)
    {
        _hostBusy = busy;
        UpdateEnabled();
    }

    private void Error(string message)
    {
        AnnounceStatus(message);
        Failed?.Invoke(message);
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            if (!CanInvokeShortcut(modifiers)) return;
            action();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    internal bool CanInvokeShortcut(VirtualKeyModifiers modifiers)
    {
        if (_busy || _hostBusy || _disposed || !IsEnabled || XamlRoot is null) return false;
        if (modifiers == VirtualKeyModifiers.None) return HasFocusWithin(_canvas);
        for (var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
            if (focused is TextBox or NumberBox or AutoSuggestBox or RichEditBox or PasswordBox or ColorPicker) return false;
        return true;
    }

    private bool HasFocusWithin(DependencyObject element)
    {
        if (XamlRoot is null) return false;
        for (var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
            if (ReferenceEquals(focused, element)) return true;
        return false;
    }

    private void UpdateEnabled()
    {
        UpdateCursor();
        var enabled = !_busy && !_hostBusy;
        if (IsEnabled && !enabled && HasFocusWithin(this))
            _focusBeforeBusy = FocusManager.GetFocusedElement(XamlRoot) as Control;
        IsEnabled = enabled;
        if (enabled && _focusBeforeBusy is { } previous)
        {
            _focusBeforeBusy = null;
            if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is null)
                previous.Focus(FocusState.Keyboard);
        }
    }

    private void AnnounceStatus(string message)
    {
        _status.Text = message;
        if (_status.IsLoaded)
            FrameworkElementAutomationPeer.CreatePeerForElement(_status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void UpdateAppearance()
    {
        if (_disposed) return;
        _status.Opacity = _accessibility.HighContrast ? 1 : 0.75;
        _canvas.Invalidate();
        _preview.Canvas.Invalidate();
    }

    private void ReleaseDocument()
    {
        _navigation.Reset();
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
        UpdateCanvasPointer(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _systemColors.ColorValuesChanged -= _colorsChanged;
        if (_contrastNotificationsAvailable) _accessibility.HighContrastChanged -= _contrastChanged;
        ReleaseDocument();
        _colors.Flyout.Hide();
        ProtectedCursor = null;
        foreach (var cursor in _cursors.Values) cursor.Dispose();
        _cursors.Clear();
        _preview.Dispose();
        _canvas.RemoveFromVisualTree();
    }

    private void ZoomTo(float scale)
    {
        if (!HasImage) return;
        _navigation.Zoom(scale, new Vector2((float)_canvas.ActualWidth / 2, (float)_canvas.ActualHeight / 2));
        _canvas.Invalidate();
    }
}
