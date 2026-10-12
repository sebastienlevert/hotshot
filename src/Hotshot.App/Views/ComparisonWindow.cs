using System.Globalization;
using Hotshot.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Hotshot.Views;

internal sealed class ComparisonWindow : Window
{
    private readonly CapturedImage _before;
    private readonly CapturedImage _after;
    private readonly ComparisonViewport _viewport;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _zoom = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _exportStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3, Visibility = Visibility.Collapsed };
    private readonly Button _export = new() { Content = "Export local report…", IsEnabled = false };
    private readonly NumberBox _tolerance = Number("Tolerance / 255", 8, 0, 255);
    private readonly NumberBox _offsetX = Number("After X offset (px)", 0, int.MinValue, int.MaxValue);
    private readonly NumberBox _offsetY = Number("After Y offset (px)", 0, int.MinValue, int.MaxValue);
    private readonly TextBox _ignored = new()
    {
        Header = "Ignored rectangles: x,y,width,height (one per line, before-image pixels)",
        PlaceholderText = "Example: 20,30,100,40",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Height = 100,
        MaxLength = 32_768,
    };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private ImageComparisonResult _result;
    private ComparisonOptions _resultOptions = new();
    private bool _initialized, _busy, _exporting, _closed, _pending;
    private long _revision;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _active;

    private ComparisonWindow(CapturedImage before, CapturedImage after, ImageComparisonResult result)
    {
        _before = before; _after = after; _result = result;
        _tolerance.Value = result.Tolerance;
        _resultOptions = result.Options;
        _offsetX.Value = _resultOptions.OffsetX;
        _offsetY.Value = _resultOptions.OffsetY;
        _ignored.Text = string.Join(Environment.NewLine, _resultOptions.IgnoredRegions.Select(region =>
            string.Create(CultureInfo.InvariantCulture, $"{region.X},{region.Y},{region.Width},{region.Height}")));
        _viewport = new ComparisonViewport(before.Width, before.Height, after.Width, after.Height);
        Title = "Hotshot visual comparison";
        var mode = new ComboBox { ItemsSource = new[] { "Before", "After", "Difference", "Swipe" }, SelectedIndex = 2, MinWidth = 135 };
        AutomationProperties.SetAutomationId(mode, "ComparisonMode");
        mode.SelectionChanged += (_, _) => _viewport.SetMode((ComparisonDisplayMode)Math.Max(0, mode.SelectedIndex));
        var fit = new Button { Content = "Fit" };
        AutomationProperties.SetAutomationId(fit, "ComparisonFit");
        fit.Click += (_, _) => _viewport.Fit();
        var actual = new Button { Content = "100%" };
        AutomationProperties.SetAutomationId(_zoom, "ComparisonZoomStatus");
        var cancel = new Button { Content = "Cancel work" };
        cancel.Click += (_, _) => _active?.Cancel();
        AutomationProperties.SetAutomationId(actual, "ComparisonActualPixels");
        actual.Click += (_, _) => _viewport.ActualPixels();
        ToolTipService.SetToolTip(actual, "One image pixel per physical screen pixel");
        _viewport.ZoomChanged += percent => _zoom.Text = $"{percent:F0}%";
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(mode); toolbar.Children.Add(fit); toolbar.Children.Add(actual);
        toolbar.Children.Add(_zoom); toolbar.Children.Add(_export); toolbar.Children.Add(cancel);
        _export.Click += async (_, _) => await ExportAsync();
        AutomationProperties.SetAutomationId(_export, "ComparisonExport");
        AutomationProperties.SetAutomationId(_summary, "ComparisonSummary");
        AutomationProperties.SetAutomationId(_error, "ComparisonError");
        var swipe = new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 230, Header = "Swipe: after on left, before on right" };
        AutomationProperties.SetAutomationId(swipe, "ComparisonSwipe");
        swipe.ValueChanged += (_, args) => _viewport.SetSwipe(args.NewValue / 100);
        var draw = new CheckBox { Content = "Drag to ignore a rectangle", VerticalAlignment = VerticalAlignment.Bottom };
        draw.Checked += (_, _) => _viewport.DrawIgnoredRegions = true;
        draw.Unchecked += (_, _) => _viewport.DrawIgnoredRegions = false;
        var clear = new Button { Content = "Clear ignored regions", VerticalAlignment = VerticalAlignment.Bottom };
        clear.Click += (_, _) => _ignored.Text = "";
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        tools.Children.Add(swipe); tools.Children.Add(draw); tools.Children.Add(clear);

        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        numbers.Children.Add(_tolerance); numbers.Children.Add(_offsetX); numbers.Children.Add(_offsetY);
        foreach (var box in new[] { _tolerance, _offsetX, _offsetY }) box.ValueChanged += (_, _) => QueueRecompute();
        _ignored.TextChanged += (_, _) => QueueRecompute();
        AutomationProperties.SetAutomationId(_tolerance, "ComparisonTolerance");
        AutomationProperties.SetAutomationId(_offsetX, "ComparisonOffsetX");
        AutomationProperties.SetAutomationId(_offsetY, "ComparisonOffsetY");
        AutomationProperties.SetAutomationId(_ignored, "ComparisonIgnoredRegions");
        AutomationProperties.SetAutomationId(_viewport, "ComparisonViewport");
        AutomationProperties.SetName(_viewport, "Comparison image. Wheel to zoom; drag to pan or draw an ignored region.");
        var options = new StackPanel { Spacing = 8 };
        options.Children.Add(new ScrollViewer { Content = numbers, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        options.Children.Add(_ignored);
        options.Children.Add(new TextBlock
        {
            Text = $"Positive offsets move After right/down. Compare only overlap; mismatched sizes are allowed here. Gray = ignored/unmatched. Up to {ImageComparison.MaximumIgnoredRegions} rectangles; 64 MP per input.",
            TextWrapping = TextWrapping.Wrap,
        });
        var expander = new Expander { Header = "Tolerance, alignment and ignored regions", Content = options, HorizontalAlignment = HorizontalAlignment.Stretch };
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(new ScrollViewer { Content = toolbar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        header.Children.Add(new ScrollViewer { Content = tools, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        header.Children.Add(expander);
        header.Children.Add(_summary);
        header.Children.Add(_exportStatus);
        header.Children.Add(_error);
        header.Children.Add(_progress);
        var root = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(header);
        Grid.SetRow(_viewport, 1); root.Children.Add(_viewport);
        var help = new TextBlock { Text = "Wheel: synchronized zoom • Drag: pan • Fit / 100% preserve alignment • Orange: ignored • Red in Difference: changes", TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(help, 2); root.Children.Add(help);
        Content = root;
        _viewport.RegionDrawn += region =>
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"{region.X},{region.Y},{region.Width},{region.Height}");
            _ignored.Text = string.IsNullOrWhiteSpace(_ignored.Text) ? line : _ignored.Text.TrimEnd() + Environment.NewLine + line;
        };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RecomputeAsync(); };
        Closed += (_, _) => { _closed = true; _debounce.Stop(); _stop.Cancel(); _active?.Cancel(); };
        AppWindow.Closing += (_, args) =>
        {
            if (_exporting)
            {
                args.Cancel = true;
                _exportStatus.Text = "Finishing the local export. You can close this window when it completes.";
            }
        };
        WindowTools.Configure(this, 1120, 820);
        UpdateSummary();
    }

    /// <summary>Compatibility factory; images are loaded at full resolution, never downscaled.</summary>
    public static async Task<ComparisonWindow> CreateAsync(CapturedImage before, CapturedImage after, ImageComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(result);
        var window = new ComparisonWindow(before, after, result);
        window._progress.Visibility = Visibility.Visible;
        try
        {
            if (result.Difference.Width != before.Width || result.Difference.Height != before.Height)
                throw new ArgumentException("The difference image must use the before image's dimensions.");
            var first = await BitmapAsync(before);
            var second = await BitmapAsync(after);
            var difference = await BitmapAsync(result.Difference);
            window._viewport.SetSources(first, second, difference);
            window._viewport.SetResult(difference, window._resultOptions);
            window._initialized = true;
            // Recompute once using explicit overlap semantics; legacy callers can supply a result without metadata.
            window.QueueRecompute();
            window._debounce.Stop();
            await window.RecomputeAsync();
        }
        catch (Exception ex) { window.ShowError(ex); }
        finally { window._progress.Visibility = Visibility.Collapsed; }
        return window;
    }

    private static NumberBox Number(string header, double value, double minimum, double maximum) => new()
    {
        Header = header, Value = value, Minimum = minimum, Maximum = maximum, Width = 180,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        SmallChange = 1, LargeChange = 10,
    };

    private static async Task<BitmapImage> BitmapAsync(CapturedImage image)
    {
        var png = await Task.Run(() => ImageCodec.EncodePngAsync(image));
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = image.Width, DecodePixelHeight = image.Height };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void QueueRecompute()
    {
        if (!_initialized || _closed) return;
        _revision++; _pending = true;
        _export.IsEnabled = false;
        _summary.Text = "Options changed; updating comparison…";
        _debounce.Stop(); _debounce.Start();
    }

    private async Task RecomputeAsync()
    {
        if (_busy || _exporting || _closed || !_initialized) return;
        _busy = true; _pending = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _active = cancellation;
        var revision = _revision;
        _progress.Visibility = Visibility.Visible;
        _error.IsOpen = false;
        try
        {
            var tolerance = Integer(_tolerance, "Tolerance");
            var options = ReadOptions();
            var result = await Task.Run(() => ImageComparison.CompareCancellable(_before, _after, options, tolerance, cancellation.Token), cancellation.Token);
            if (_closed || revision != _revision) return;
            var difference = await BitmapAsync(result.Difference);
            if (_closed || revision != _revision) return;
            _viewport.SetResult(difference, options);
            _result = result; _resultOptions = result.Options;
            UpdateSummary();
            _export.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (!_closed && revision == _revision)
            {
                _summary.Text = "Comparison could not be updated. The last valid image is still shown; export is disabled.";
                ShowError(ex);
            }
        }
        finally
        {
            _busy = false;
            _active = null;
            if (!_closed)
            {
                _progress.Visibility = Visibility.Collapsed;
                if (_pending || revision != _revision) { _debounce.Stop(); _debounce.Start(); }
            }
        }
    }

    private ComparisonOptions ReadOptions()
    {
        var regions = new List<PixelRect>();
        foreach (var line in _ignored.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (regions.Count >= ImageComparison.MaximumIgnoredRegions)
                throw new ArgumentException($"Use at most {ImageComparison.MaximumIgnoredRegions} ignored rectangles.");
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 4 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
                throw new ArgumentException($"Ignored rectangle {regions.Count + 1} must contain four whole numbers: x,y,width,height.");
            regions.Add(new PixelRect(x, y, width, height));
        }
        return new ComparisonOptions { OffsetX = Integer(_offsetX, "X offset"), OffsetY = Integer(_offsetY, "Y offset"), IgnoredRegions = regions.ToArray() };
    }

    private static int Integer(NumberBox box, string name)
    {
        var value = box.Value;
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue)
            throw new ArgumentException($"{name} must be a whole number.");
        return checked((int)value);
    }

    private void UpdateSummary()
    {
        var bounds = _result.ChangedBounds is { } rectangle ? $" Changed bounds: {rectangle}." : "";
        _summary.Text = $"{_result.ChangedPixels:N0} changed / {_result.ComparedPixels:N0} compared pixels ({_result.ChangedPercent:F2}%). Tolerance: {_result.Tolerance}/255 per RGB channel.{bounds}" +
            (_result.ComparedPixels == 0 ? " Nothing is compared: no overlap or all overlap is ignored." : "");
    }

    private async Task ExportAsync()
    {
        if (_busy || _pending || _exporting || !_export.IsEnabled) return;
        _exporting = true; _export.IsEnabled = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _active = cancellation;
        _error.IsOpen = false; _exportStatus.Text = "";
        try
        {
            var folder = await new FolderPicker(AppWindow.Id) { Title = "Export comparison report and flattened PNGs locally" }.PickSingleFolderAsync();
            if (folder is null) return;
            var result = _result; var options = _resultOptions;
            _progress.Visibility = Visibility.Visible;
            var path = await Task.Run(() => ComparisonExport.SaveAsync(folder.Path, _before, _after, result, options, cancellation.Token), cancellation.Token);
            _exportStatus.Text = "Local report saved: " + path;
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _exporting = false;
            _active = null;
            _progress.Visibility = Visibility.Collapsed;
            _export.IsEnabled = !_pending;
            if (_pending) { _debounce.Stop(); _debounce.Start(); }
        }
    }

    private void ShowError(Exception ex)
    {
        Log.Error("Visual comparison failed", ex);
        _error.Title = "Comparison";
        _error.Message = ex.Message;
        _error.IsOpen = true;
    }
}
