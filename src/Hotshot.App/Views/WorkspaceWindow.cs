using Hotshot.Capture;
using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Editor;
using Hotshot.Shell;
using Hotshot.CaptureFlow;
using Hotshot.Core.Descriptions;
using Hotshot.Core.Settings;
using Windows.Media.Playback;
using Windows.Media.Core;
using Microsoft.Windows.Storage.Pickers;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Hotshot.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "WinUI owns the window lifetime; Closed releases the editor/player and each operation releases its cancellation source in finally.")]
internal sealed partial class WorkspaceWindow : Window
{
    private static readonly System.Text.Json.JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    private static readonly string[] AllProjects = ["All projects"];
    private readonly AppServices _app;
    private readonly CaptureActions _actions;
    private readonly Action<AppCommand> _execute;
    private readonly ListView _history = new() { SelectionMode = ListViewSelectionMode.Extended };
    private readonly ComboBox _projects = new() { PlaceholderText = "All projects", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _savedSearches = new() { PlaceholderText = "Saved searches", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly MediaPlayerElement _video = new() { AreTransportControlsEnabled = true, Visibility = Visibility.Collapsed };
    private MediaPlayer? _player;
    private int _visibleLimit = 200;
    private readonly List<PinnedImageWindow> _pins = [];
    private readonly CommandBar _commands = new() { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true };
    private readonly Dictionary<string, MenuFlyout> _commandGroups = [];
    private bool _fileAvailable;
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, Height = 4 };
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
    private CancellationTokenSource? _operationCancellation;
    private readonly Button _cancelOperation = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private ColumnDefinition? _historyColumn;
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
        var commands = _commands;
        AutomationProperties.SetAutomationId(commands, "WorkspaceCommands");
        AutomationProperties.SetName(commands, "Capture and library commands");
        AutomationProperties.SetAutomationId(_editor, "WorkspaceEditor");
        AutomationProperties.SetAutomationId(_save, "WorkspaceSave");
        AutomationProperties.SetAutomationId(_copy, "WorkspaceCopy");
        AutomationProperties.SetAutomationId(_error, "WorkspaceError");
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
                     ("Scrolling capture", AppCommand.Scrolling),
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
        AddCommand("Pin image", Symbol.Pin, async () =>
        {
            if (_pins.Count >= 8) throw new InvalidOperationException("Close a pinned image before opening another. Hotshot supports up to eight pinned images.");
            var pinned = await PinnedImageWindow.CreateAsync(await _editor.ExportPngAsync());
            _pins.Add(pinned);
            _app.AuxiliaryWindowCount++;
            pinned.Closed += (_, _) => _pins.Remove(pinned);
            pinned.Closed += (_, _) => _app.AuxiliaryWindowCount--;
            pinned.Activate();
        });
        AddCommand("Rename capture", Symbol.Rename, RenameAsync);
        AddCommand("Organize selected", Symbol.Tag, OrganizeSelectedAsync);
        AddCommand("Delete selected", Symbol.Delete, DeleteSelectedAsync);
        AddCommand("Export selected files", Symbol.SaveLocal, ExportSelectedAsync);
        AddCommand("Save this search", Symbol.Find, SaveSearchAsync);
        AddCommand("Storage and privacy", Symbol.Folder, StoragePrivacyAsync);
        AddCommand("Back up library", Symbol.SaveLocal, BackupLibraryAsync);
        AddCommand("Restore library backup", Symbol.OpenFile, RestoreLibraryAsync);
        AddCommand("Find missing file", Symbol.Link, RelinkAsync);
        AddCommand("Set project baseline", Symbol.Pin, SetBaselineAsync);
        AddCommand("Compare with project baseline", Symbol.Switch, CompareBaselineAsync);
        AddCommand("Package screenshots", Symbol.Document, EvidenceBundleAsync);
        AddCommand("Delete saved search", Symbol.Clear, async () =>
        {
            if (_savedSearches.SelectedItem is not SavedCaptureSearch search) throw new InvalidOperationException("Choose a saved search in Filters first.");
            if (await DialogAsync("Delete saved search?", UiStyles.Description(search.Name), "Delete") != ContentDialogResult.Primary) return;
            _app.Settings.Library.SavedSearches.Remove(search);
            await _app.SaveAsync();
            RenderHistory();
        });
        AddCommand("Describe screenshot", Symbol.Document, AnalyzeCurrentAsync);
        AddCommand("View description progress", Symbol.Document, async () =>
        {
            var queued = _app.Descriptions?.QueuedCaptureIds.ToHashSet(StringComparer.Ordinal) ?? [];
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(UiStyles.Description($"{_app.Descriptions?.Status}\n{_app.Descriptions?.ProviderStatus}"));
            foreach (var item in _app.History.Items.Where(item => queued.Contains(item.Id) || item.DescriptionNeedsReview || item.DescriptionError is not null).Take(100))
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = item.FileName, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(UiStyles.Description(item.DescriptionNeedsReview ? "Review and save edits, then choose Describe screenshot."
                    : item.DescriptionError ?? (item.Id == _app.Descriptions?.ActiveCaptureId ? "Analyzing" : "Queued")));
                if (queued.Contains(item.Id))
                {
                    var cancel = new Button { Content = "Cancel analysis" };
                    cancel.Click += (_, _) => { _app.Descriptions?.Cancel(item.Id); cancel.IsEnabled = false; };
                    row.Children.Add(cancel);
                }
                body.Children.Add(row);
            }
            await new ContentDialog { Title = "Screenshot descriptions", Content = new ScrollViewer { Content = body, MaxHeight = 500 },
                CloseButtonText = "Close", XamlRoot = ((FrameworkElement)Content).XamlRoot }.ShowAsync();
        });
        AddCommand("Cancel description", Symbol.Cancel, () =>
        {
            if (_current is { } item) _app.Descriptions?.Cancel(item.Id);
            return Task.CompletedTask;
        });
        AddCommand("Compare selected screenshots", Symbol.Switch, CompareSelectedAsync);
        AddCommand("Extract video frame", Symbol.Camera, ExtractFrameAsync);
        AddCommand("Trim recording", Symbol.Trim, TrimVideoAsync);
        AddCommand("Recover interrupted recording", Symbol.Refresh, async () =>
        {
            var journal = Path.Combine(_app.Paths.DataDirectory, "pending-recording.json");
            var recovered = await Hotshot.Recording.RecordingRecovery.InspectAsync(journal);
            if (recovered is null) throw new InvalidOperationException("No interrupted recording journal was found.");
            if (await DialogAsync("Recover recording?", UiStyles.Description(
                $"A playable recording was preserved ({recovered.Duration:g}). Add it to the library? The file will not be changed."), "Recover") != ContentDialogResult.Primary) return;
            var result = await (_app.Output ?? throw new InvalidOperationException("Output is unavailable.")).AddVideoAsync(
                recovered.Path, CaptureKind.Recording, recovered.Width, recovered.Height, recovered.Duration, false, copyToClipboard: false);
            if (result is not null) await Task.Run(() => Hotshot.Recording.RecordingRecovery.Complete(journal));
        });
        AddCommand("Dismiss recovery notice", Symbol.Clear, async () =>
        {
            if (await DialogAsync("Dismiss recovery notice?", UiStyles.Description(
                "Clears the notice so you can record again. Your recording won't be deleted."), "Dismiss") == ContentDialogResult.Primary)
                await Task.Run(() => Hotshot.Recording.RecordingRecovery.Dismiss(Path.Combine(_app.Paths.DataDirectory, "pending-recording.json")));
        });
        var settings = new AppBarButton { Label = "Settings", Icon = new SymbolIcon(Symbol.Setting) };
        settings.Click += (_, _) => _execute(AppCommand.Settings);
        commands.PrimaryCommands.Add(settings);
        root.Children.Add(commands);
        var notices = new StackPanel { Spacing = 4 };
        notices.Children.Add(_error);
        notices.Children.Add(_progress);
        notices.Children.Add(_cancelOperation);
        _cancelOperation.Click += (_, _) => _operationCancellation?.Cancel();
        AutomationProperties.SetAutomationId(_cancelOperation, "CancelWorkspaceOperation");
        AutomationProperties.SetName(_progress, "Hotshot is working");
        Grid.SetRow(notices, 1);
        root.Children.Add(notices);

        var main = new Grid { ColumnSpacing = 8 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220), MinWidth = 170, MaxWidth = 420 });
        _historyColumn = main.ColumnDefinitions[0];
        _historyColumn.Width = new GridLength(_app.Settings.Layout.HistoryWidth);
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
        filters.Children.Add(_projects);
        filters.Children.Add(_savedSearches);
        AutomationProperties.SetName(_projects, "Project collection");
        AutomationProperties.SetName(_savedSearches, "Saved capture search");
        Grid.SetRow(filters, 2);
        var filterExpander = new Expander { Header = "Filters and saved searches", Content = filters, IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(filterExpander, "CaptureLibraryFilters");
        Grid.SetRow(filterExpander, 2);
        historyPane.Children.Add(filterExpander);
        Grid.SetRow(_history, 3);
        historyPane.Children.Add(_history);
        main.Children.Add(historyPane);
        var surface = new Grid();
        _empty.Children.Add(new SymbolIcon(Symbol.Edit) { Width = 40, Height = 40 });
        _empty.Children.Add(new TextBlock { Text = "Choose a capture to edit", FontSize = 22, TextWrapping = TextWrapping.Wrap });
        _empty.Children.Add(UiStyles.Description("Select a screenshot from history to annotate, crop, save or copy it."));
        surface.Children.Add(_empty);
        surface.Children.Add(_editor);
        surface.Children.Add(_video);
        AutomationProperties.SetAutomationId(_video, "WorkspaceVideoPreview");
        AutomationProperties.SetName(_video, "Recording preview");
        Grid.SetColumn(surface, 2);
        main.Children.Add(surface);
        Grid.SetRow(main, 2);
        root.Children.Add(main);
        Content = root;
        root.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 700;
            main.ColumnDefinitions[0].MinWidth = compact ? 130 : 170;
            if (main.ColumnDefinitions[0].ActualWidth > 420) main.ColumnDefinitions[0].Width = new GridLength(420);
            _title.MaxWidth = compact ? 100 : 180;
        };
        UiStyles.ApplyTheme(this, app.Settings.General.Theme);
        _search.TextChanged += (_, _) => { _visibleLimit = 200; RenderHistory(); };
        _projects.SelectionChanged += (_, _) => { if (!_rendering) RenderHistory(); };
        _savedSearches.SelectionChanged += (_, _) =>
        {
            if (!_rendering && _savedSearches.SelectedItem is SavedCaptureSearch search) _search.Text = search.Query;
        };
        _favoritesOnly.Toggled += (_, _) => RenderHistory();
        _typeFilter.SelectionChanged += (_, _) => RenderHistory();
        _dateFilter.SelectionChanged += (_, _) => RenderHistory();
        _history.SelectionChanged += (_, _) =>
        {
            if (_rendering || _history.SelectedItems.Count != 1 || _history.SelectedItem is not ListViewItem { Tag: HistoryItem item }) return;
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
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        find.Invoked += (_, args) => { _search.Focus(FocusState.Keyboard); args.Handled = true; };
        root.KeyboardAccelerators.Add(find);
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
            _player?.Dispose();
            _app.Settings.Layout.HistoryWidth = Math.Clamp(_historyColumn?.ActualWidth ?? 220, 130, 420);
            _app.SaveSettings();
        };
        RenderHistory();
        WindowTools.Configure(this, 1120, 780, excludeFromCapture: false, app: _app, layoutKey: "workspace");
    }

    public void Show()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    public bool HasUnsavedEdits => _editor.IsDirty;
    public bool IsBusy => _operation is not null || _editor.IsBusy || _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

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
            Title = "Save your changes?", Content = "You have unsaved edits.",
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
        if (!File.Exists(item.Path))
        {
            _fileAvailable = false;
            _current = item;
            _title.Text = item.FileName;
            _editor.Clear();
            _player?.Dispose(); _player = null;
            _editor.Visibility = _video.Visibility = Visibility.Collapsed;
            _empty.Children.Clear();
            _empty.Children.Add(new TextBlock { Text = "File not found", FontSize = 22 });
            _empty.Children.Add(UiStyles.Description("It may have moved. Choose Find missing file to locate it."));
            _empty.Visibility = Visibility.Visible;
            UpdateActions();
            return;
        }
        _fileAvailable = true;
        _error.IsOpen = false;
        _current = item;
        _title.Text = item.FileName;
        ToolTipService.SetToolTip(_title, item.FileName);
        if (item.IsVideo)
        {
            _editor.Clear();
            _editor.Visibility = Visibility.Collapsed;
            _empty.Visibility = Visibility.Collapsed;
            _video.Visibility = Visibility.Visible;
            _player?.Dispose();
            _player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(item.Path)), AutoPlay = false };
            _video.SetMediaPlayer(_player);
            _empty.Children.Clear();
            _empty.Children.Add(new SymbolIcon(Symbol.Video));
            _empty.Children.Add(new TextBlock { Text = item.FileName, FontSize = 20, TextWrapping = TextWrapping.Wrap });
            _empty.Children.Add(UiStyles.Description($"{item.Width} x {item.Height} - open or copy this recording using the toolbar."));
        }
        else
        {
            _video.Visibility = Visibility.Collapsed;
            _player?.Dispose();
            _player = null;
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
        item.DescriptionPending = CaptureAnalysisPolicy.CanAnalyzeAutomatically(_app.Settings.General, item.AppName);
        item.DescriptionNeedsReview = CaptureAnalysisPolicy.RequiresReview(_app.Settings.General, item.AppName);
        await Task.Run(() => _app.History.Update(item));
        if (item.DescriptionPending && _app.Descriptions is { } descriptions)
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
            CaptureSource.Now(title: Path.GetFileName(selected.Path)), copyToClipboard: false);
        if (item is not null) { await LoadAsync(item); RenderHistory(restoreCurrent: true); }
    }

    private async Task ShowDetailsAsync()
    {
        if (_current is not { } item) return;
        var tags = new TextBox { Header = "Tags (comma-separated)", Text = string.Join(", ", item.Tags ?? []) };
        var favorite = new CheckBox { Content = "Keep as a favorite", IsChecked = item.IsFavorite };
        var project = new TextBox { Header = "Project collection", Text = item.Project };
        AutomationProperties.SetAutomationId(project, "CaptureProjectName");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"{item.Width} x {item.Height}  |  {item.Kind.DisplayName()}  |  {item.FileSize / 1024d:N1} KB", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(favorite);
        content.Children.Add(tags);
        content.Children.Add(project);
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
            var isFavorite = favorite.IsChecked == true;
            var collection = project.Text;
            await Task.Run(() => _app.History.UpdateOrganization(item.Id, isFavorite, normalized, collection));
        }
    }

    private async Task ExtractTextAsync()
    {
        if (_current is not { IsVideo: false } item) return;
        var png = await _editor.ExportPngAsync();
        var text = await Task.Run(() => TextRecognition.RecognizeAsync(png));
        if (!_editor.IsDirty) await Task.Run(() => _app.History.UpdateRecognizedText(item.Id, text, null));
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 400, Text = text };
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
        var last = _app.Settings.Export.Last;
        var format = new ComboBox { Header = "File format", ItemsSource = new[] { "PNG", "JPEG" }, SelectedIndex = last.Format };
        var width = new NumberBox { Header = "Image width (0 = original)", Minimum = 0, Maximum = 16384, Value = last.Width };
        var padding = new NumberBox { Header = "Padding (pixels)", Minimum = 0, Maximum = 400, Value = last.Padding };
        var radius = new NumberBox { Header = "Rounded corners (pixels)", Minimum = 0, Maximum = 400, Value = last.CornerRadius };
        var background = new ComboBox { Header = "Background", ItemsSource = Enum.GetValues<ExportBackground>(), SelectedIndex = last.Background };
        var shadow = new CheckBox { Content = "Drop shadow", IsChecked = last.Shadow };
        var presets = new ComboBox { Header = "Saved style", ItemsSource = _app.Settings.Export.Presets, DisplayMemberPath = "Name" };
        var presetName = new TextBox { Header = "Save style as (optional)", MaxLength = 80 };
        AutomationProperties.SetAutomationId(presets, "ExportPresets");
        AutomationProperties.SetAutomationId(presetName, "ExportPresetName");
        AutomationProperties.SetAutomationId(width, "ExportWidth");
        AutomationProperties.SetAutomationId(padding, "ExportPadding");
        var removePreset = new Button { Content = "Delete style" };
        removePreset.Click += async (_, _) =>
        {
            if (presets.SelectedItem is not ExportPreset selected) return;
            _app.Settings.Export.Presets.Remove(selected);
            presets.ItemsSource = _app.Settings.Export.Presets.ToArray();
            presetName.Text = "";
            if (!await _app.SaveAsync()) ShowError("The preset deletion could not be saved.");
        };
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedItem is not ExportPreset selected) return;
            var style = selected.Style;
            format.SelectedIndex = style.Format; width.Value = style.Width; padding.Value = style.Padding;
            radius.Value = style.CornerRadius; background.SelectedIndex = style.Background; shadow.IsChecked = style.Shadow;
            presetName.Text = selected.Name;
        };
        var body = new StackPanel { Spacing = 10 };
        foreach (var control in new UIElement[] { format, width, padding, radius, background, shadow }) body.Children.Add(control);
        body.Children.Insert(0, presets);
        body.Children.Add(presetName);
        body.Children.Add(removePreset);
        body.Children.Add(UiStyles.Description("Saves a separate edited copy, without extracted text or descriptions. Your original is kept."));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "Export image", Content = new ScrollViewer { Content = body, MaxHeight = 500 },
            PrimaryButtonText = "Choose file", CloseButtonText = "Cancel", XamlRoot = ((FrameworkElement)Content).XamlRoot,
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
        var style = new ExportStyle((int)options.Format, options.Width, options.Padding, options.CornerRadius, options.Shadow, (int)options.Background);
        var named = presetName.Text.Trim();
        if (named.Length > 0)
        {
            if (_app.Settings.Export.Presets.Count >= 50 && !_app.Settings.Export.Presets.Any(preset => preset.Name.Equals(named, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Up to 50 export presets are supported.");
            _app.Settings.Export.Presets.RemoveAll(preset => preset.Name.Equals(named, StringComparison.OrdinalIgnoreCase));
            _app.Settings.Export.Presets.Add(new ExportPreset(named, style));
        }
        _app.Settings.Export.Last = style;
        if (!await _app.SaveAsync()) throw new IOException("Export preferences could not be saved.");
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
        var root = (FrameworkElement)Content;
        var focusBefore = root.XamlRoot is null ? null : FocusManager.GetFocusedElement(root.XamlRoot) as Control;
        var restoreKeyboardFocus = focusBefore?.FocusState is FocusState.Keyboard or FocusState.Programmatic;
        _commands.IsEnabled = false;
        _editor.SetHostBusy(true);
        _progress.Visibility = Visibility.Visible;
        _operationCancellation = new CancellationTokenSource();
        _cancelOperation.Visibility = Visibility.Visible;
        _history.IsEnabled = keepHistoryEnabled;
        _save.IsEnabled = _copy.IsEnabled = _convert.IsEnabled = _favorite.IsEnabled = _details.IsEnabled = _text.IsEnabled = _export.IsEnabled = false;
        try
        {
            _operation = action();
            await _operation;
        }
        catch (OperationCanceledException) when (_operationCancellation.IsCancellationRequested)
        {
            ShowError("Operation canceled. Completed outputs, if any, were preserved.");
        }
        catch (Exception ex)
        {
            Log.Error("Editor workspace action failed", ex);
            ShowError(ex.Message);
        }
        finally
        {
            var focused = root.XamlRoot is null ? null : FocusManager.GetFocusedElement(root.XamlRoot);
            var recoverFocus = restoreKeyboardFocus && (focused is null ||
                focused is DependencyObject element && IsWithin(element, _cancelOperation));
            _operation = null;
            _commands.IsEnabled = true;
            _editor.SetHostBusy(false);
            _progress.Visibility = Visibility.Collapsed;
            _cancelOperation.Visibility = Visibility.Collapsed;
            _operationCancellation.Dispose();
            _operationCancellation = null;
            if (!_closed)
            {
                _history.IsEnabled = true;
                UpdateActions();
                if (recoverFocus && focusBefore?.Focus(FocusState.Keyboard) != true)
                    _history.Focus(FocusState.Keyboard);
                if (_pendingHistorySelection is not null)
                    _ = RunAsync(LoadHistorySelectionAsync, keepHistoryEnabled: true);
            }
        }
    }

    private void UpdateActions()
    {
        var ready = _operation is null && !_editor.IsBusy;
        _save.IsEnabled = ready && _current is { IsVideo: false } && _editor.HasImage;
        _copy.IsEnabled = ready && _fileAvailable && _current is not null && (_current.IsVideo || _editor.HasImage);
        _open.IsEnabled = _fileAvailable && _current is not null;
        _folder.IsEnabled = _current is not null;
        _convert.IsEnabled = ready && _fileAvailable && _current is { } item && _actions.CanConvertToGif(item);
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
        var root = (FrameworkElement)Content;
        var focused = root.XamlRoot is null ? null : FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject;
        var recoverFocus = focused is not null && IsWithin(focused, _history);
        string? focusedId = null;
        for (var element = focused; recoverFocus && element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ListViewItem { Tag: HistoryItem focusedItem })
            {
                focusedId = focusedItem.Id;
                break;
            }
        }
        var selectedIds = restoreCurrent ? new HashSet<string>(_current is { } current ? [current.Id] : [])
            : SelectedCaptures().Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var selectedId = restoreCurrent ? _current?.Id
            : (_history.SelectedItem as ListViewItem)?.Tag is HistoryItem selected ? selected.Id : _current?.Id;
        if (selectedIds.Count == 0 && selectedId is not null) selectedIds.Add(selectedId);
        _rendering = true;
        var selectedProject = _projects.SelectedItem as string;
        _projects.ItemsSource = AllProjects.Concat(_app.History.Items.Select(item => item.Project).Where(project => project.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order()).ToArray();
        _projects.SelectedItem = selectedProject ?? "All projects";
        var chosenSearch = _savedSearches.SelectedItem as SavedCaptureSearch;
        _savedSearches.ItemsSource = _app.Settings.Library.SavedSearches.ToArray();
        _savedSearches.SelectedItem = _app.Settings.Library.SavedSearches.FirstOrDefault(search => search.Name == chosenSearch?.Name);
        _savedSearches.DisplayMemberPath = "Name";
        _history.Items.Clear();
        var filter = new CaptureLibraryFilter(_search.Text, _favoritesOnly.IsOn,
            (CaptureTypeFilter)Math.Max(0, _typeFilter.SelectedIndex), (CaptureDateRange)Math.Max(0, _dateFilter.SelectedIndex));
        var matches = _app.History.ReadPage(filter, selectedProject is null or "All projects" ? null : selectedProject, _visibleLimit, 0, out var total);
        foreach (var item in matches)
        {
            var body = new TextBlock { Text = item.FileName, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            var row = new ListViewItem { Content = body, Tag = item, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(row, $"Capture {item.FileName}");
            var details = item.DescriptionNeedsReview ? "Awaiting review before cloud analysis."
                : item.DescriptionError is { } error ? $"AI description failed: {error}"
                : item.DescriptionPending ? "AI description is pending."
                : item.Summary is { } summary ? $"{summary}\n{item.Description}" : null;
            ToolTipService.SetToolTip(row, details is null ? item.Path : $"{item.Path}\n{details}");
            AutomationProperties.SetHelpText(row, $"{item.Kind.DisplayName()}, {item.Width} x {item.Height} pixels. " +
                (item.IsFavorite ? "Favorite. " : string.Empty) + details);
            _history.Items.Add(row);
            if (selectedIds.Contains(item.Id)) _history.SelectedItems.Add(row);
        }
        AutomationProperties.SetHelpText(_history, $"{_history.Items.Count} captures");
        if (total > _visibleLimit)
        {
            var more = new Button { Content = $"Load more ({total} matching captures)" };
            more.Click += (_, _) => { _visibleLimit += 200; RenderHistory(); };
            _history.Items.Add(new ListViewItem { Content = more, IsTabStop = false });
        }
        _rendering = false;
        if (recoverFocus)
        {
            var target = _history.Items.OfType<ListViewItem>()
                .FirstOrDefault(row => row.Tag is HistoryItem item && item.Id == focusedId)
                ?? _history.Items.OfType<ListViewItem>().FirstOrDefault();
            if (target?.Focus(FocusState.Keyboard) != true) _search.Focus(FocusState.Keyboard);
        }
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private HistoryItem[] SelectedCaptures() => _history.SelectedItems.OfType<ListViewItem>().Select(row => row.Tag).OfType<HistoryItem>().ToArray();

    private async Task<ContentDialogResult> DialogAsync(string title, UIElement content, string primary = "Apply") =>
        await new ContentDialog { Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "Cancel",
            XamlRoot = ((FrameworkElement)Content).XamlRoot }.ShowAsync();

    private async Task RenameAsync()
    {
        if (_current is not { } item || !await ConfirmDiscardAsync()) return;
        var name = new TextBox { Header = "Filename (extension is kept)", Text = Path.GetFileNameWithoutExtension(item.Path) };
        if (await DialogAsync("Rename capture", name, "Rename") != ContentDialogResult.Primary) return;
        var value = name.Text.Trim();
        if (value.Length == 0 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value is "." or "..")
            throw new ArgumentException("Enter a valid filename without path separators.");
        _app.Descriptions?.Cancel(item.Id);
        var path = Path.Combine(Path.GetDirectoryName(item.Path)!, value + Path.GetExtension(item.Path));
        if (path == item.Path) return;
        async Task MoveAsync()
        {
            await Task.Run(() => File.Move(item.Path, path, overwrite: false));
            item.Path = path;
            await Task.Run(() => _app.History.Update(item));
        }
        if (_app.Descriptions is { } descriptions) await descriptions.RunFileOperationAsync(MoveAsync);
        else await MoveAsync();
        await LoadAsync(item);
    }

    private async Task OrganizeSelectedAsync()
    {
        var items = SelectedCaptures();
        if (items.Length == 0) return;
        var project = new TextBox { Header = "Project collection" };
        var tags = new TextBox { Header = "Tags to add (comma-separated)" };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(project); body.Children.Add(tags);
        if (await DialogAsync($"Organize {items.Length} captures", body) != ContentDialogResult.Primary) return;
        var collection = CaptureLibrary.NormalizeProject(project.Text);
        var added = CaptureLibrary.NormalizeTags(tags.Text.Split(','));
        await Task.Run(() => _app.History.Organize(items.Select(item => item.Id), collection, added));
    }

    private async Task DeleteSelectedAsync()
    {
        var items = SelectedCaptures();
        if (items.Length == 0 || !await ConfirmDiscardAsync()) return;
        if (await DialogAsync($"Delete {items.Length} captures?", UiStyles.Description(
            "Saved files go to the Recycle Bin. Temporary items are deleted. Originals are kept."), "Delete") != ContentDialogResult.Primary) return;
        var failed = false;
        foreach (var item in items)
        {
            _app.Descriptions?.Cancel(item.Id);
            var deleted = false;
            async Task DeleteAsync() => deleted = await Task.Run(() => _actions.Delete(item));
            if (_app.Descriptions is { } descriptions) await descriptions.RunFileOperationAsync(DeleteAsync);
            else await DeleteAsync();
            failed |= !deleted;
        }
        _current = null;
        _editor.Clear();
        _editor.Visibility = _video.Visibility = Visibility.Collapsed;
        _player?.Dispose(); _player = null;
        _empty.Visibility = Visibility.Visible;
        _empty.Children.Clear();
        _empty.Children.Add(new TextBlock { Text = "Choose a capture to edit", FontSize = 22 });
        RenderHistory(restoreCurrent: true);
        if (failed) throw new IOException("Some captures could not be deleted. Their library records were kept.");
    }

    private async Task ExportSelectedAsync()
    {
        var items = SelectedCaptures();
        if (items.Length == 0) return;
        if (_editor.IsDirty) throw new InvalidOperationException("Save your edits before exporting selected files.");
        var folder = await new FolderPicker(AppWindow.Id) { Title = "Choose an export folder" }.PickSingleFolderAsync();
        if (folder is null) return;
        await Task.Run(async () =>
        {
            foreach (var item in items)
            {
                OperationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(folder.Path, item.IsVideo ? item.FileName : Path.ChangeExtension(item.FileName, ".png"));
                if (File.Exists(destination)) throw new IOException("An export file already exists: " + item.FileName);
                if (item.IsVideo)
                {
                    try
                    {
                        await using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                        await input.CopyToAsync(output, OperationToken);
                    }
                    catch
                    {
                        if (File.Exists(destination)) File.Delete(destination);
                        throw;
                    }
                }
                else await ImageCodec.SavePngAsync(await ImageCodec.LoadAsync(item.Path), destination);
            }
        });
    }

    private async Task SaveSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(_search.Text)) throw new InvalidOperationException("Enter a search query first.");
        var name = new TextBox { Header = "Search name", Text = _search.Text };
        if (await DialogAsync("Save capture search", name, "Save") != ContentDialogResult.Primary) return;
        var label = CaptureLibrary.NormalizeProject(name.Text);
        if (label.Length == 0) throw new ArgumentException("A search name is required.");
        if (_app.Settings.Library.SavedSearches.Count >= 100 && !_app.Settings.Library.SavedSearches.Any(search => search.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The library supports up to 100 saved searches. Replace an existing named search or remove one first.");
        _app.Settings.Library.SavedSearches.RemoveAll(search => search.Name.Equals(label, StringComparison.OrdinalIgnoreCase));
        _app.Settings.Library.SavedSearches.Add(new SavedCaptureSearch(label, _search.Text));
        await _app.SaveAsync();
        RenderHistory();
    }

    private async Task AnalyzeCurrentAsync()
    {
        if (_current is not { IsVideo: false } item) throw new InvalidOperationException("Select a screenshot first.");
        if (!_app.Settings.General.DescribeScreenshots) throw new InvalidOperationException("Enable Copilot descriptions in Settings first.");
        if (CaptureAnalysisPolicy.IsExcluded(_app.Settings.General, item.AppName)) throw new InvalidOperationException("This app is excluded from Copilot.");
        if (_editor.IsDirty) throw new InvalidOperationException("Save your edits before sending to Copilot.");
        var sourceWarning = CaptureAnalysisPolicy.HasKnownSource(item.AppName) ? "" :
            "The source app is unknown, so exclusions can't be checked. ";
        if (await DialogAsync("Send this screenshot to Copilot?", UiStyles.Description(
            sourceWarning + "Sends the saved image to GitHub Copilot and uses your plan. Hide private details and save first."), "Send") != ContentDialogResult.Primary) return;
        item.DescriptionNeedsReview = false;
        await Task.Run(() => _app.History.Update(item));
        if (_app.Descriptions is { } descriptions) _ = descriptions.EnqueueAsync(item, approved: true);
    }

    private void AddCommand(string label, Symbol symbol, Func<Task> action)
    {
        var group = label switch
        {
            "Pin image" or "Compare selected screenshots" => "Image",
            "Describe screenshot" or "Cancel description" or "View description progress" => "Copilot",
            "Extract video frame" or "Trim recording" or "Recover interrupted recording" or "Dismiss recovery notice" => "Video",
            _ => "Library",
        };
        if (!_commandGroups.TryGetValue(group, out var flyout))
        {
            flyout = new MenuFlyout();
            _commandGroups.Add(group, flyout);
            _commands.SecondaryCommands.Add(new AppBarButton { Label = group, Icon = new SymbolIcon(symbol), Flyout = flyout });
        }
        var item = new MenuFlyoutItem { Text = label, Icon = new SymbolIcon(symbol) };
        flyout.Opening += (_, _) =>
        {
            item.IsEnabled = label switch
            {
                "Pin image" or "Describe screenshot" => _current is { IsVideo: false } && _editor.HasImage,
                "Cancel description" => _current is not null && _app.Descriptions?.QueuedCaptureIds.Contains(_current.Id) == true,
                "Compare selected screenshots" => SelectedCaptures().Length == 2 && SelectedCaptures().All(capture => !capture.IsVideo),
                "Extract video frame" => _current is { IsVideo: true } && _fileAvailable,
                "Trim recording" => _current is { Kind: CaptureKind.Recording } && _fileAvailable,
                "Rename capture" => _current is not null && _fileAvailable,
                "Organize selected" or "Delete selected" or "Export selected files" => SelectedCaptures().Length > 0,
                "Save this search" => !string.IsNullOrWhiteSpace(_search.Text),
                "Delete saved search" => _savedSearches.SelectedItem is SavedCaptureSearch,
                "Set project baseline" => _current is { IsVideo: false, Project.Length: > 0 } && _fileAvailable,
                "Compare with project baseline" => _current is { IsVideo: false, Project.Length: > 0 } && _fileAvailable,
                "Find missing file" => _current is not null && !_fileAvailable,
                "Package screenshots" => SelectedCaptures().Length is > 0 and <= 20 && SelectedCaptures().All(capture => !capture.IsVideo),
                _ => true,
            };
        };
        item.Click += (_, _) =>
        {
            _commands.IsOpen = false;
            _ = RunAsync(action);
        };
        flyout.Items.Add(item);
    }

    private async Task CompareSelectedAsync()
    {
        var items = SelectedCaptures().OrderBy(item => item.CreatedAt).ToArray();
        if (items.Length != 2 || items.Any(item => item.IsVideo))
            throw new InvalidOperationException("Ctrl-select two screenshots of the same size to compare.");
        if (_editor.IsDirty) throw new InvalidOperationException("Save edits before comparing saved captures.");
        var before = await Task.Run(() => ImageCodec.LoadAsync(items[0].Path));
        var after = await Task.Run(() => ImageCodec.LoadAsync(items[1].Path));
        var comparison = await Task.Run(() => ImageComparison.CompareCancellable(before, after, new ComparisonOptions(), 8, OperationToken), OperationToken);
        var window = await ComparisonWindow.CreateAsync(before, after, comparison);
        _app.AuxiliaryWindowCount++;
        window.Closed += (_, _) => _app.AuxiliaryWindowCount--;
        window.Activate();
    }

    private async Task ExtractFrameAsync()
    {
        if (_current is not { IsVideo: true } item || _player is null) throw new InvalidOperationException("Select a recording first.");
        var position = _player.PlaybackSession.Position;
        var image = await Hotshot.Recording.VideoEditing.FrameAsync(item.Path, position);
        var output = _app.Output ?? throw new InvalidOperationException("Capture output is not ready.");
        var saved = await output.SaveScreenshotAsync(image, CaptureKind.Region,
            CaptureSource.FromHistory(item), copyToClipboard: false);
        if (saved is not null) await LoadAsync(saved);
    }

    private async Task TrimVideoAsync()
    {
        if (_current is not { Kind: CaptureKind.Recording } item) throw new InvalidOperationException("Select an MP4 recording first.");
        var range = await VideoTrimDialog.ShowAsync(item.Path, ((FrameworkElement)Content).XamlRoot);
        if (range is null) return;
        var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = Path.GetFileNameWithoutExtension(item.Path) + "-trimmed" };
        picker.FileTypeChoices.Add("MP4 recording", [".mp4"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await Hotshot.Recording.VideoEditing.TrimAsync(item.Path, file.Path, range.Value.Start, range.Value.End, OperationToken);
        await (_app.Output ?? throw new InvalidOperationException("Output is unavailable.")).AddVideoAsync(
            file.Path, CaptureKind.Recording, item.Width, item.Height, range.Value.End - range.Value.Start, false,
            copyToClipboard: false, source: CaptureSource.FromHistory(item));
    }
}
