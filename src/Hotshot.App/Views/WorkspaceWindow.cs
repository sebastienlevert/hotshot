using Hotshot.Capture;
using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Editor;
using Hotshot.Shell;
using Hotshot.CaptureFlow;
using Microsoft.Windows.Storage.Pickers;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Hotshot.Views;

internal sealed class WorkspaceWindow : Window
{
    private readonly AppServices _app;
    private readonly CaptureActions _actions;
    private readonly Action<AppCommand> _execute;
    private readonly ListView _history = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _search = new() { PlaceholderText = "Search filename, tags or text" };
    private readonly ToggleSwitch _favoritesOnly = new() { Header = "Favorites only", OnContent = "", OffContent = "" };
    private readonly ComboBox _typeFilter = new() { ItemsSource = new[] { "All captures", "Screenshots", "Recordings", "GIFs" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _dateFilter = new() { ItemsSource = new[] { "Any time", "Today", "Last 7 days", "Last 30 days" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly EditorView _editor = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _empty = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { Text = "Editor and history", FontSize = 18, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly InfoBar _error = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly AppBarButton _save = new() { Label = "Save", Icon = new SymbolIcon(Symbol.Save), IsEnabled = false };
    private readonly AppBarButton _copy = new() { Label = "Copy", Icon = new SymbolIcon(Symbol.Copy), IsEnabled = false };
    private readonly AppBarButton _open = new() { Label = "Open file", Icon = new SymbolIcon(Symbol.OpenFile), IsEnabled = false };
    private readonly AppBarButton _folder = new() { Label = "Show in folder", Icon = new SymbolIcon(Symbol.Folder), IsEnabled = false };
    private readonly AppBarButton _convert = new() { Label = "Convert to GIF", Icon = new SymbolIcon(Symbol.Video), IsEnabled = false };
    private readonly AppBarButton _favorite = new() { Label = "Favorite", Icon = new SymbolIcon(Symbol.OutlineStar), IsEnabled = false };
    private readonly AppBarButton _details = new() { Label = "Details and tags", Icon = new SymbolIcon(Symbol.Tag), IsEnabled = false };
    private readonly AppBarButton _text = new() { Label = "Extract text", Icon = new SymbolIcon(Symbol.Document), IsEnabled = false };
    private readonly AppBarButton _export = new() { Label = "Export...", Icon = new SymbolIcon(Symbol.SaveLocal), IsEnabled = false };
    private HistoryItem? _current;
    private HistoryItem? _pendingHistorySelection;
    private EditorSavedEventArgs? _lastSave;
    private Task? _operation;
    private bool _rendering;
    private bool _closed;
    private bool _allowClose;
    private bool _closing;

    public WorkspaceWindow(AppServices app, CaptureActions actions, Action<AppCommand> execute)
    {
        _app = app;
        _actions = actions;
        _execute = execute;
        Title = "Hotshot editor";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        var root = new Grid { Padding = new Thickness(16), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true };
        AutomationProperties.SetAutomationId(commands, "WorkspaceCommands");
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Logo.png"))), Width = 28, Height = 28 });
        brand.Children.Add(_title);
        commands.Content = brand;
        var capture = new AppBarButton { Label = "Capture", Icon = new SymbolIcon(Symbol.Camera) };
        var menu = new MenuFlyout();
        foreach (var (label, command) in new[]
                 {
                     ("Capture region", AppCommand.Region), ("Capture monitor", AppCommand.Monitor),
                     ("Capture all monitors", AppCommand.AllMonitors), ("Record screen", AppCommand.Record),
                 })
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => _ = RunAsync(async () =>
            {
                if (!await ConfirmDiscardAsync()) return;
                AppWindow.Hide();
                _execute(command);
            });
            menu.Items.Add(item);
        }
        capture.Flyout = menu;
        commands.PrimaryCommands.Add(capture);
        commands.PrimaryCommands.Add(_save);
        commands.PrimaryCommands.Add(_copy);
        commands.PrimaryCommands.Add(_favorite);
        commands.PrimaryCommands.Add(_export);
        commands.SecondaryCommands.Add(_text);
        commands.SecondaryCommands.Add(_details);
        var import = new AppBarButton { Label = "Import image...", Icon = new SymbolIcon(Symbol.OpenFile) };
        import.Click += (_, _) => _ = RunAsync(ImportAsync);
        commands.SecondaryCommands.Add(import);
        commands.SecondaryCommands.Add(_open);
        commands.SecondaryCommands.Add(_folder);
        commands.SecondaryCommands.Add(_convert);
        var settings = new AppBarButton { Label = "Settings", Icon = new SymbolIcon(Symbol.Setting) };
        settings.Click += (_, _) => _execute(AppCommand.Settings);
        commands.PrimaryCommands.Add(settings);
        root.Children.Add(commands);
        Grid.SetRow(_error, 1);
        root.Children.Add(_error);

        var main = new Grid { ColumnSpacing = 8 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220), MinWidth = 170, MaxWidth = 420 });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
        var splitter = new GridSplitter { Width = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1);
        AutomationProperties.SetName(splitter, "Resize capture list");
        main.Children.Add(splitter);
        var historyPane = new Grid { RowSpacing = 12 };
        historyPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        historyPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        historyPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        historyPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        historyPane.Children.Add(new TextBlock { Text = "Recent captures", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        AutomationProperties.SetName(_search, "Search captures");
        Grid.SetRow(_search, 1);
        historyPane.Children.Add(_search);
        AutomationProperties.SetName(_history, "Capture history");
        AutomationProperties.SetAutomationId(_history, "CaptureHistory");
        var filters = new StackPanel { Spacing = 6 };
        AutomationProperties.SetName(_typeFilter, "Capture type filter");
        AutomationProperties.SetName(_dateFilter, "Capture date filter");
        AutomationProperties.SetName(_favoritesOnly, "Favorites only");
        AutomationProperties.SetAutomationId(_favoritesOnly, "CaptureFavoritesFilter");
        AutomationProperties.SetAutomationId(_typeFilter, "CaptureTypeFilter");
        AutomationProperties.SetAutomationId(_dateFilter, "CaptureDateFilter");
        filters.Children.Add(_typeFilter);
        filters.Children.Add(_dateFilter);
        filters.Children.Add(_favoritesOnly);
        Grid.SetRow(filters, 2);
        historyPane.Children.Add(filters);
        Grid.SetRow(_history, 3);
        historyPane.Children.Add(_history);
        main.Children.Add(historyPane);
        var surface = new Grid();
        _empty.Children.Add(new SymbolIcon(Symbol.Edit) { Width = 40, Height = 40 });
        _empty.Children.Add(new TextBlock { Text = "Choose a capture to edit", FontSize = 22, TextWrapping = TextWrapping.Wrap });
        _empty.Children.Add(UiStyles.Description("Select a screenshot from history to annotate, crop, save or copy it."));
        surface.Children.Add(_empty);
        surface.Children.Add(_editor);
        Grid.SetColumn(surface, 2);
        main.Children.Add(surface);
        Grid.SetRow(main, 2);
        root.Children.Add(main);
        Content = root;
        UiStyles.ApplyTheme(this, app.Settings.General.Theme);
        _search.TextChanged += (_, _) => RenderHistory();
        _favoritesOnly.Toggled += (_, _) => RenderHistory();
        _typeFilter.SelectionChanged += (_, _) => RenderHistory();
        _dateFilter.SelectionChanged += (_, _) => RenderHistory();
        _history.SelectionChanged += (_, _) =>
        {
            if (_rendering || _history.SelectedItem is not ListViewItem { Tag: HistoryItem item }) return;
            _pendingHistorySelection = item;
            _ = RunAsync(LoadHistorySelectionAsync, keepHistoryEnabled: true);
        };
        _save.Click += (_, _) => _ = RunAsync(async () => await SaveCurrentAsync());
        _favorite.Click += (_, _) => _ = RunAsync(async () =>
        {
            if (_current is not { } item) return;
            await Task.Run(() => _app.History.UpdateOrganization(item.Id, !item.IsFavorite, item.Tags ?? []));
        });
        _details.Click += (_, _) => _ = RunAsync(ShowDetailsAsync);
        _text.Click += (_, _) => _ = RunAsync(ExtractTextAsync);
        _export.Click += (_, _) => _ = RunAsync(ExportAsync);
        _copy.Click += (_, _) => _ = RunAsync(async () =>
        {
            if (_current is not { } item) return;
            if (item.IsVideo) await _actions.CopyAsync(item); else await _editor.CopyAsync();
        });
        _open.Click += (_, _) => { if (_current is { } item) _actions.Open(item); };
        _folder.Click += (_, _) => { if (_current is { } item) _actions.ShowInFolder(item); };
        _convert.Click += (_, _) => _ = RunAsync(async () =>
        {
            if (_current is { } item) await _actions.ConvertToGifAsync(item);
        });
        _editor.Saved += (_, args) => _lastSave = args;
        _editor.Failed += ShowError;
        app.History.Changed += HistoryChanged;
        var saveShortcut = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.S, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        saveShortcut.Invoked += (sender, args) => { _ = RunAsync(async () => await SaveCurrentAsync()); args.Handled = true; };
        root.KeyboardAccelerators.Add(saveShortcut);
        var copyShortcut = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.C, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        copyShortcut.Invoked += (sender, args) =>
        {
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) is TextBox || !_copy.IsEnabled) return;
            _ = RunAsync(async () =>
            {
                if (_current is { IsVideo: true } item) await _actions.CopyAsync(item);
                else await _editor.CopyAsync();
            });
            args.Handled = true;
        };
        root.KeyboardAccelerators.Add(copyShortcut);
        AppWindow.Closing += async (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            await Task.Yield();
            if (await PrepareToCloseAsync()) { _allowClose = true; Close(); }
            else _closing = false;
        };
        Closed += (_, _) =>
        {
            _closed = true;
            app.History.Changed -= HistoryChanged;
            _editor.Dispose();
        };
        RenderHistory();
        WindowTools.Configure(this, 1120, 780);
    }

    public void Show()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    public bool HasUnsavedEdits => _editor.IsDirty;
    public bool IsBusy => _operation is not null || _editor.IsBusy;

    public async Task SelectAsync(HistoryItem item)
    {
        if (_operation is { } operation) await operation;
        await RunAsync(async () =>
        {
            if (!await ConfirmDiscardAsync()) return;
            await LoadAsync(item);
            RenderHistory(restoreCurrent: true);
        });
    }

    private async Task LoadHistorySelectionAsync()
    {
        while (_pendingHistorySelection is { } item && !_closed)
        {
            _pendingHistorySelection = null;
            if (_current?.Id == item.Id) continue;
            if (!await ConfirmDiscardAsync())
            {
                _pendingHistorySelection = null;
                RenderHistory(restoreCurrent: true);
                return;
            }
            if (_pendingHistorySelection is not null) continue;
            await LoadAsync(item);
        }
    }

    public async Task<bool> PrepareToCloseAsync()
    {
        if (_operation is { } operation) await operation;
        return await ConfirmDiscardAsync();
    }

    public void CloseForShutdown() { _allowClose = true; Close(); }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_editor.IsDirty) return true;
        var dialog = new ContentDialog
        {
            Title = "Save your changes?", Content = "This screenshot has unsaved annotations or a crop.",
            PrimaryButtonText = "Save", SecondaryButtonText = "Discard", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = ((FrameworkElement)Content).XamlRoot,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await SaveCurrentAsync(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    private async Task LoadAsync(HistoryItem item)
    {
        if (!File.Exists(item.Path)) throw new FileNotFoundException("This capture was moved or deleted.", item.Path);
        _error.IsOpen = false;
        _current = item;
        _title.Text = item.FileName;
        ToolTipService.SetToolTip(_title, item.FileName);
        if (item.IsVideo)
        {
            _editor.Clear();
            _editor.Visibility = Visibility.Collapsed;
            _empty.Visibility = Visibility.Visible;
            _empty.Children.Clear();
            _empty.Children.Add(new SymbolIcon(Symbol.Video));
            _empty.Children.Add(new TextBlock { Text = item.FileName, FontSize = 20, TextWrapping = TextWrapping.Wrap });
            _empty.Children.Add(UiStyles.Description($"{item.Width} x {item.Height} - open or copy this recording using the toolbar."));
        }
        else
        {
            _empty.Visibility = Visibility.Collapsed;
            _editor.Visibility = Visibility.Visible;
            await _editor.LoadAsync(new EditorOptions
            {
                ImagePath = item.Path,
                OriginalBackupPath = Path.Combine(_app.Paths.OriginalsDirectory, item.Id + ".png"),
                AnnotationsPath = Path.Combine(_app.Paths.OriginalsDirectory, item.Id + ".json"),
                SaveImageAsync = _app.Descriptions is { } descriptions
                    ? (png, redacted) => descriptions.SaveEditedImageAsync(item, png, preserveMetadata: !redacted) : null,
                CopyPngToClipboard = async png =>
                {
                    var image = await Task.Run(() => ImageCodec.DecodePngAsync(png));
                    if (!await ClipboardService.SetImageAsync(_app.MessageWindowHandle, image, png))
                        throw new IOException("The clipboard is being used by another application.");
                },
            });
        }
        UpdateActions();
    }

    private async Task<bool> SaveCurrentAsync()
    {
        if (_current is not { } item || item.IsVideo) return false;
        _lastSave = null;
        if (!await _editor.SaveAsync() || _lastSave is not { } saved) return false;
        item.Width = saved.Width;
        item.Height = saved.Height;
        item.FileSize = saved.FileSize;
        var thumbnail = Path.Combine(_app.Paths.ThumbnailsDirectory, item.Id + ".png");
        if (await Task.Run(() => ImageCodec.CreateThumbnailAsync(item.Path, thumbnail))) item.ThumbnailPath = thumbnail;
        item.DescriptionPending = _app.Settings.General.DescribeScreenshots;
        await Task.Run(() => _app.History.Update(item));
        if (_app.Settings.General.DescribeScreenshots && _app.Descriptions is { } descriptions)
            _ = descriptions.EnqueueAsync(item);
        if (_app.Settings.General.RecognizeScreenshotText && _app.TextIndex is { } textIndex)
            textIndex.Enqueue(item, await File.ReadAllBytesAsync(item.Path));
        return true;
    }

    private async Task ImportAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            Title = "Import image into Hotshot",
            FileTypeFilter = { ".png", ".jpg", ".jpeg", ".bmp" },
        };
        var selected = await picker.PickSingleFileAsync();
        if (selected is null || _closed) return;
        var image = await Task.Run(() => ImageCodec.LoadAsync(selected.Path));
        var output = _app.Output ?? throw new InvalidOperationException("Capture output is not initialized.");
        var item = await output.SaveScreenshotAsync(image, CaptureKind.Region,
            CaptureSource.Now("Imported image", Path.GetFileName(selected.Path)), copyToClipboard: false);
        if (item is not null) { await LoadAsync(item); RenderHistory(restoreCurrent: true); }
    }

    private async Task ShowDetailsAsync()
    {
        if (_current is not { } item) return;
        var tags = new TextBox { Header = "Tags (comma-separated)", Text = string.Join(", ", item.Tags ?? []) };
        var favorite = new CheckBox { Content = "Keep as a favorite", IsChecked = item.IsFavorite };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"{item.Width} x {item.Height}  |  {item.Kind.DisplayName()}  |  {item.FileSize / 1024d:N1} KB", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(favorite);
        content.Children.Add(tags);
        content.Children.Add(new TextBox
        {
            Header = "AI summary and description", IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 160,
            Text = item.Summary is null ? item.DescriptionError ?? "No AI description." : $"{item.Summary}\n{item.Description}",
        });
        content.Children.Add(new TextBox
        {
            Header = "Recognized text (local)", IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 160,
            Text = item.RecognizedText ?? item.TextRecognitionError ?? "Use Extract text to recognize text without uploading the image.",
        });
        var dialog = new ContentDialog
        {
            Title = item.FileName, Content = new ScrollViewer { Content = content, MaxHeight = 470 },
            PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var normalized = CaptureLibrary.NormalizeTags(tags.Text.Split(','));
            await Task.Run(() => _app.History.UpdateOrganization(item.Id, favorite.IsChecked == true, normalized));
        }
    }

    private async Task ExtractTextAsync()
    {
        if (_current is not { IsVideo: false } item) return;
        var png = await _editor.ExportPngAsync();
        var text = await Task.Run(() => TextRecognition.RecognizeAsync(png));
        if (!_editor.IsDirty) await Task.Run(() => _app.History.UpdateRecognizedText(item.Id, text, null));
        var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 400 };
        var dialog = new ContentDialog
        {
            Title = string.IsNullOrWhiteSpace(text) ? "No text found" : "Recognized text (local)",
            Content = box, PrimaryButtonText = "Copy text", CloseButtonText = "Close",
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(text), XamlRoot = ((FrameworkElement)Content).XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
            !await ClipboardService.SetTextAsync(_app.MessageWindowHandle, box.Text))
            throw new IOException("The clipboard is being used by another application.");
    }

    private async Task ExportAsync()
    {
        if (_current is not { IsVideo: false } item) return;
        var format = new ComboBox { Header = "File format", ItemsSource = new[] { "PNG", "JPEG" }, SelectedIndex = 0 };
        var width = new NumberBox { Header = "Image width (0 = original)", Minimum = 0, Maximum = 16384, Value = 0 };
        var padding = new NumberBox { Header = "Padding (pixels)", Minimum = 0, Maximum = 400, Value = 24 };
        var radius = new NumberBox { Header = "Rounded corners (pixels)", Minimum = 0, Maximum = 400, Value = 12 };
        var background = new ComboBox { Header = "Background", ItemsSource = Enum.GetValues<ExportBackground>(), SelectedItem = ExportBackground.Charcoal };
        var shadow = new CheckBox { Content = "Drop shadow", IsChecked = true };
        var body = new StackPanel { Spacing = 10 };
        foreach (var control in new UIElement[] { format, width, padding, radius, background, shadow }) body.Children.Add(control);
        body.Children.Add(UiStyles.Description("Exports a separate flattened copy. Originals and editable annotations are unchanged; AI/OCR metadata is not included."));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "Export image", Content = new ScrollViewer { Content = body, MaxHeight = 500 },
            PrimaryButtonText = "Choose destination", CloseButtonText = "Cancel", XamlRoot = ((FrameworkElement)Content).XamlRoot,
        };
        ImageExportOptions? options = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (!double.IsFinite(width.Value) || !double.IsFinite(padding.Value) || !double.IsFinite(radius.Value))
                    throw new ArgumentException("Enter valid export dimensions.");
                options = new ImageExportOptions(format.SelectedIndex == 1 ? ImageExportFormat.Jpeg : ImageExportFormat.Png,
                    (int)width.Value, (int)padding.Value, (int)radius.Value, shadow.IsChecked == true,
                    (ExportBackground)background.SelectedItem);
                options.GetLayout(item.Width, item.Height);
            }
            catch (ArgumentException ex) { args.Cancel = true; error.Text = ex.Message; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || options is null) return;
        var picker = new FileSavePicker(AppWindow.Id)
        {
            Title = "Export screenshot", SuggestedFileName = Path.GetFileNameWithoutExtension(item.Path) + "-export",
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeChoices.Add(options.Format == ImageExportFormat.Jpeg ? "JPEG image" : "PNG image",
            [options.Format == ImageExportFormat.Jpeg ? ".jpg" : ".png"]);
        var selected = await picker.PickSaveFileAsync();
        if (selected is null || _closed) return;
        if (string.Equals(Path.GetFullPath(selected.Path), Path.GetFullPath(item.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a different export filename to preserve the editable capture.");
        var bytes = await _editor.ExportAsync(options);
        await Task.Run(() => ImageCodec.WriteAtomicAsync(selected.Path, bytes));
    }

    private async Task RunAsync(Func<Task> action, bool keepHistoryEnabled = false)
    {
        if (_operation is not null) return;
        _history.IsEnabled = keepHistoryEnabled;
        _save.IsEnabled = _copy.IsEnabled = _convert.IsEnabled = _favorite.IsEnabled = _details.IsEnabled = _text.IsEnabled = _export.IsEnabled = false;
        try
        {
            _operation = action();
            await _operation;
        }
        catch (Exception ex)
        {
            Log.Error("Editor workspace action failed", ex);
            ShowError(ex.Message);
        }
        finally
        {
            _operation = null;
            if (!_closed)
            {
                _history.IsEnabled = true;
                UpdateActions();
                if (_pendingHistorySelection is not null)
                    _ = RunAsync(LoadHistorySelectionAsync, keepHistoryEnabled: true);
            }
        }
    }

    private void UpdateActions()
    {
        var ready = _operation is null && !_editor.IsBusy;
        _save.IsEnabled = ready && _current is { IsVideo: false } && _editor.HasImage;
        _copy.IsEnabled = ready && _current is not null && (_current.IsVideo || _editor.HasImage);
        _open.IsEnabled = _folder.IsEnabled = _current is not null;
        _convert.IsEnabled = ready && _current is { } item && _actions.CanConvertToGif(item);
        _favorite.IsEnabled = _details.IsEnabled = ready && _current is not null;
        _text.IsEnabled = _export.IsEnabled = ready && _current is { IsVideo: false } && _editor.HasImage;
        _favorite.Icon = new SymbolIcon(_current?.IsFavorite == true ? Symbol.SolidStar : Symbol.OutlineStar);
        _favorite.Label = _current?.IsFavorite == true ? "Unfavorite" : "Favorite";
    }

    private void ShowError(string message)
    {
        if (_closed) return;
        _error.Title = "Hotshot needs your attention";
        _error.Message = message;
        _error.IsOpen = true;
    }

    private void HistoryChanged(object? sender, EventArgs args) => _app.Post(() => { if (!_closed) RenderHistory(); });

    private void RenderHistory(bool restoreCurrent = false)
    {
        var selectedId = restoreCurrent ? _current?.Id
            : (_history.SelectedItem as ListViewItem)?.Tag is HistoryItem selected ? selected.Id : _current?.Id;
        _rendering = true;
        _history.Items.Clear();
        var filter = new CaptureLibraryFilter(_search.Text, _favoritesOnly.IsOn,
            (CaptureTypeFilter)Math.Max(0, _typeFilter.SelectedIndex), (CaptureDateRange)Math.Max(0, _dateFilter.SelectedIndex));
        foreach (var item in _app.History.Items.Where(i => filter.Matches(i, DateTimeOffset.Now)))
        {
            var body = new TextBlock { Text = item.FileName, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            var row = new ListViewItem { Content = body, Tag = item, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(row, $"Capture {item.FileName}");
            var details = item.DescriptionError is { } error ? $"AI description failed: {error}"
                : item.DescriptionPending ? "AI description is pending."
                : item.Summary is { } summary ? $"{summary}\n{item.Description}" : null;
            ToolTipService.SetToolTip(row, details is null ? item.Path : $"{item.Path}\n{details}");
            _history.Items.Add(row);
            if (selectedId == item.Id) _history.SelectedItem = row;
        }
        AutomationProperties.SetHelpText(_history, $"{_history.Items.Count} captures");
        _rendering = false;
    }
}
