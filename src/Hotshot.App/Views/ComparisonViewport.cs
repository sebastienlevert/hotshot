using Hotshot.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace Hotshot.Views;

internal enum ComparisonDisplayMode { Before, After, Difference, Swipe }

/// <summary>One physical-pixel canvas and transform shared by all comparison display modes.</summary>
internal sealed class ComparisonViewport : Grid
{
    private readonly int _width;
    private readonly int _height;
    private readonly Canvas _stage;
    private readonly Image _before;
    private readonly Image _after;
    private readonly Image _difference;
    private readonly Canvas _afterLayer;
    private readonly Canvas _regions;
    private readonly Rectangle _divider = new() { Fill = new SolidColorBrush(Colors.White), IsHitTestVisible = false };
    private readonly Rectangle _selection = new()
    {
        Stroke = new SolidColorBrush(Colors.Orange),
        Fill = new SolidColorBrush(Color.FromArgb(50, 255, 165, 0)),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };
    private readonly CompositeTransform _transform = new();
    private ComparisonDisplayMode _mode = ComparisonDisplayMode.Difference;
    private double _swipe = .5;
    private double _scale = 1, _x, _y;
    private bool _fit = true;
    private uint? _pointer;
    private bool _drawing;
    private Point _start, _last, _startPixel;
    private double _rasterScale = 1;
    private XamlRoot? _observedRoot;
    private readonly Windows.UI.ViewManagement.UISettings _systemColors = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibility = new();
    private readonly Windows.Foundation.TypedEventHandler<Windows.UI.ViewManagement.UISettings, object> _colorsChanged;

    public event Action<PixelRect>? RegionDrawn;
    public event Action<double>? ZoomChanged;
    public bool DrawIgnoredRegions { get; set; }

    public ComparisonViewport(int beforeWidth, int beforeHeight, int afterWidth, int afterHeight)
    {
        _width = beforeWidth; _height = beforeHeight;
        IsTabStop = true;
        _colorsChanged = (_, _) => DispatcherQueue.TryEnqueue(UpdateTheme);
        ActualThemeChanged += (_, _) => UpdateTheme();
        _stage = new Canvas
        {
            Width = _width, Height = _height, RenderTransform = _transform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        _stage.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _width, _height) };
        _before = MakeImage(_width, _height);
        _after = MakeImage(afterWidth, afterHeight);
        _difference = MakeImage(_width, _height);
        _afterLayer = new Canvas { Width = _width, Height = _height, IsHitTestVisible = false };
        _afterLayer.Children.Add(_after);
        _regions = new Canvas { Width = _width, Height = _height, IsHitTestVisible = false };
        _stage.Children.Add(_before);
        _stage.Children.Add(_afterLayer);
        _stage.Children.Add(_difference);
        _stage.Children.Add(_regions);
        _stage.Children.Add(_divider);
        _stage.Children.Add(_selection);
        Children.Add(_stage);
        UpdateTheme();
        UpdateMode();

        SizeChanged += (_, _) =>
        {
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)) };
            if (_fit) Fit(); else UpdateTransform();
        };
        Loaded += (_, _) =>
        {
            _observedRoot = XamlRoot;
            if (_observedRoot is not null) _observedRoot.Changed += RootChanged;
            _systemColors.ColorValuesChanged += _colorsChanged;
            _rasterScale = XamlRoot?.RasterizationScale ?? 1;
            if (_fit) Fit(); else UpdateTransform();
        };
        Unloaded += (_, _) =>
        {
            if (_observedRoot is not null) _observedRoot.Changed -= RootChanged;
            _observedRoot = null;
            _systemColors.ColorValuesChanged -= _colorsChanged;
            CancelGesture();
        };
        PointerWheelChanged += (_, args) =>
        {
            var point = args.GetCurrentPoint(this);
            _fit = false;
            ZoomAt(_scale * Math.Pow(1.2, point.Properties.MouseWheelDelta / 120d), point.Position);
            args.Handled = true;
        };
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += Released;
        PointerCanceled += (_, _) => CancelGesture();
        PointerCaptureLost += (_, _) => CancelGesture();
        var fitShortcut = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Number0, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        fitShortcut.Invoked += (_, args) => { Fit(); args.Handled = true; };
        KeyboardAccelerators.Add(fitShortcut);
        var actualShortcut = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Number1, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        actualShortcut.Invoked += (_, args) => { ActualPixels(); args.Handled = true; };
        KeyboardAccelerators.Add(actualShortcut);
    }

    public void SetSources(BitmapImage before, BitmapImage after, BitmapImage difference)
    {
        _before.Source = before;
        _after.Source = after;
        _difference.Source = difference;
    }

    public void SetResult(BitmapImage difference, ComparisonOptions options)
    {
        _difference.Source = difference;
        Canvas.SetLeft(_after, options.OffsetX);
        Canvas.SetTop(_after, options.OffsetY);
        _regions.Children.Clear();
        foreach (var region in options.IgnoredRegions)
        {
            var l = Math.Max(0L, region.X); var t = Math.Max(0L, region.Y);
            var r = Math.Min(_width, (long)region.X + region.Width);
            var b = Math.Min(_height, (long)region.Y + region.Height);
            if (r <= l || b <= t) continue;
            var rectangle = new Rectangle
            {
                Width = r - l, Height = b - t,
                Stroke = new SolidColorBrush(_accessibility.HighContrast ? _systemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground) : Colors.Orange),
                Fill = new SolidColorBrush(Color.FromArgb(45, 255, 165, 0)),
                StrokeThickness = 1 / _scale,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rectangle, l); Canvas.SetTop(rectangle, t);
            _regions.Children.Add(rectangle);
        }
    }

    public void SetMode(ComparisonDisplayMode mode) { _mode = mode; UpdateMode(); }
    public void SetSwipe(double fraction) { _swipe = Math.Clamp(fraction, 0, 1); UpdateMode(); }

    private void UpdateTheme()
    {
        var contrast = _accessibility.HighContrast;
        Background = new SolidColorBrush(contrast ? _systemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background) :
            ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 48, 48, 48) : Color.FromArgb(255, 230, 230, 230));
        var foreground = contrast ? _systemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground) : Colors.Orange;
        _selection.Stroke = new SolidColorBrush(foreground);
        _divider.Fill = new SolidColorBrush(contrast ? foreground : Colors.White);
        foreach (var rectangle in _regions.Children.OfType<Rectangle>()) rectangle.Stroke = new SolidColorBrush(foreground);
    }

    public void Fit()
    {
        _fit = true;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        _scale = Math.Min(ActualWidth / _width, ActualHeight / _height);
        _x = (ActualWidth - _width * _scale) / 2;
        _y = (ActualHeight - _height * _scale) / 2;
        UpdateTransform();
    }

    public void ActualPixels()
    {
        _fit = false;
        ZoomAt(1 / _rasterScale, new Point(ActualWidth / 2, ActualHeight / 2));
    }

    private static Image MakeImage(int width, int height) => new()
    {
        Width = width, Height = height, Stretch = Stretch.Fill, IsHitTestVisible = false,
    };

    private void UpdateMode()
    {
        _before.Visibility = _mode is ComparisonDisplayMode.Before or ComparisonDisplayMode.Swipe ? Visibility.Visible : Visibility.Collapsed;
        _afterLayer.Visibility = _mode is ComparisonDisplayMode.After or ComparisonDisplayMode.Swipe ? Visibility.Visible : Visibility.Collapsed;
        _difference.Visibility = _mode == ComparisonDisplayMode.Difference ? Visibility.Visible : Visibility.Collapsed;
        _afterLayer.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _mode == ComparisonDisplayMode.Swipe ? _width * _swipe : _width, _height) };
        _divider.Visibility = _mode == ComparisonDisplayMode.Swipe ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_divider, _width * _swipe);
        _divider.Height = _height;
        _divider.Width = 1 / _scale;
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        var scale = sender.RasterizationScale;
        if (Math.Abs(scale - _rasterScale) < .0001) return;
        var previous = _rasterScale;
        _rasterScale = scale;
        if (_fit) Fit();
        else ZoomAt(_scale * previous / scale, new Point(ActualWidth / 2, ActualHeight / 2));
    }

    private void ZoomAt(double scale, Point anchor)
    {
        var fit = Math.Min(ActualWidth / _width, ActualHeight / _height);
        scale = Math.Clamp(scale, Math.Max(.0000001, Math.Min(fit, .01 / _rasterScale)), 32 / _rasterScale);
        var ratio = scale / _scale;
        _x = anchor.X - (anchor.X - _x) * ratio;
        _y = anchor.Y - (anchor.Y - _y) * ratio;
        _scale = scale;
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        var w = _width * _scale; var h = _height * _scale;
        _x = w <= ActualWidth ? (ActualWidth - w) / 2 : Math.Clamp(_x, ActualWidth - w, 0);
        _y = h <= ActualHeight ? (ActualHeight - h) / 2 : Math.Clamp(_y, ActualHeight - h, 0);
        _transform.ScaleX = _transform.ScaleY = _scale;
        _transform.TranslateX = _x; _transform.TranslateY = _y;
        _selection.StrokeThickness = _divider.Width = 1 / _scale;
        foreach (var rectangle in _regions.Children.OfType<Rectangle>()) rectangle.StrokeThickness = 1 / _scale;
        ZoomChanged?.Invoke(_scale * _rasterScale * 100);
    }

    private Point PixelPoint(Point position) => new(
        Math.Clamp((position.X - _x) / _scale, 0, _width),
        Math.Clamp((position.Y - _y) / _scale, 0, _height));

    private void Pressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this);
        if (_pointer is not null || (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed && !point.IsInContact)) return;
        if (!CapturePointer(args.Pointer)) return;
        _pointer = args.Pointer.PointerId;
        _start = _last = point.Position;
        _startPixel = PixelPoint(point.Position);
        _drawing = DrawIgnoredRegions && !point.Properties.IsMiddleButtonPressed;
        if (_drawing) _selection.Visibility = Visibility.Visible;
        args.Handled = true;
    }

    private void Moved(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        var position = args.GetCurrentPoint(this).Position;
        if (_drawing)
        {
            var p = PixelPoint(position);
            Canvas.SetLeft(_selection, Math.Min(_startPixel.X, p.X));
            Canvas.SetTop(_selection, Math.Min(_startPixel.Y, p.Y));
            _selection.Width = Math.Abs(p.X - _startPixel.X);
            _selection.Height = Math.Abs(p.Y - _startPixel.Y);
        }
        else
        {
            _fit = false;
            _x += position.X - _last.X; _y += position.Y - _last.Y;
            UpdateTransform();
        }
        _last = position;
        args.Handled = true;
    }

    private void Released(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        var position = args.GetCurrentPoint(this).Position;
        var end = PixelPoint(position);
        if (_drawing && Math.Abs(position.X - _start.X) >= 2 && Math.Abs(position.Y - _start.Y) >= 2)
        {
            var l = (int)Math.Floor(Math.Min(_startPixel.X, end.X));
            var t = (int)Math.Floor(Math.Min(_startPixel.Y, end.Y));
            var r = (int)Math.Ceiling(Math.Max(_startPixel.X, end.X));
            var b = (int)Math.Ceiling(Math.Max(_startPixel.Y, end.Y));
            if (r > l && b > t) RegionDrawn?.Invoke(new PixelRect(l, t, r - l, b - t));
        }
        CancelGesture();
        ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void CancelGesture()
    {
        _pointer = null;
        _selection.Visibility = Visibility.Collapsed;
        _selection.Width = _selection.Height = 0;
    }
}
