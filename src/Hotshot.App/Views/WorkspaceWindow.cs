using Hotshot.Capture;
using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Editor;
using Hotshot.Shell;
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
    private readonly TextBox _search = new() { PlaceholderText = "Search captures" };
    private readonly EditorView _editor = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _empty = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { Text = "Editor and history", FontSize = 18, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly InfoBar _error = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly AppBarButton _save = new() { Label = "Save", Icon = new SymbolIcon(Symbol.Save), IsEnabled = false };
    private readonly AppBarButton _copy = new() { Label = "Copy", Icon = new SymbolIcon(Symbol.Copy), IsEnabled = false };
    private readonly AppBarButton _open = new() { Label = "Open file", Icon = new SymbolIcon(Symbol.OpenFile), IsEnabled = false };
    private readonly AppBarButton _folder = new() { Label = "Show in folder", Icon = new SymbolIcon(Symbol.Folder), IsEnabled = false };
    private readonly AppBarButton _convert = new() { Label = "Convert to GIF", Icon = new SymbolIcon(Symbol.Video), IsEnabled = false };
    private HistoryItem? _current;
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
        commands.SecondaryCommands.Add(_open);
        commands.SecondaryCommands.Add(_folder);
        commands.SecondaryCommands.Add(_convert);
        var settings = new AppBarButton { Label = "Settings", Icon = new SymbolIcon(Symbol.Setting) };
        settings.Click += (_, _) => _execute(AppCommand.Settings);
        commands.PrimaryCommands.Add(settings);
        root.Children.Add(commands);
        Grid.SetRow(_error, 1);
        root.Children.Add(_error);

        var main = new Grid { ColumnSpacing = 16 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var historyPane = new Grid { RowSpacing = 12 };
        historyPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        historyPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        historyPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        historyPane.Children.Add(new TextBlock { Text = "Recent captures", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        AutomationProperties.SetName(_search, "Search captures");
        Grid.SetRow(_search, 1);
        historyPane.Children.Add(_search);
        AutomationProperties.SetName(_history, "Capture history");
        Grid.SetRow(_history, 2);
        historyPane.Children.Add(_history);
        main.Children.Add(historyPane);
        var surface = new Grid();
        _empty.Children.Add(new SymbolIcon(Symbol.Edit) { Width = 40, Height = 40 });
        _empty.Children.Add(new TextBlock { Text = "Choose a capture to edit", FontSize = 22, TextWrapping = TextWrapping.Wrap });
        _empty.Children.Add(UiStyles.Description("Select a screenshot from history to annotate, crop, save or copy it."));
        surface.Children.Add(_empty);
        surface.Children.Add(_editor);
        Grid.SetColumn(surface, 1);
        main.Children.Add(surface);
        Grid.SetRow(main, 2);
        root.Children.Add(main);
        Content = root;
        UiStyles.ApplyTheme(this, app.Settings.General.Theme);
        _search.TextChanged += (_, _) => RenderHistory();
        _history.SelectionChanged += (_, _) =>
        {
            if (_rendering || _history.SelectedItem is not ListViewItem { Tag: HistoryItem item }) return;
            _ = RunAsync(async () =>
            {
                if (!await ConfirmDiscardAsync()) { RenderHistory(); return; }
                await LoadAsync(item);
            });
        };
        _save.Click += (_, _) => _ = RunAsync(async () => await SaveCurrentAsync());
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
            RenderHistory();
        });
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
        await Task.Run(() => _app.History.Update(item));
        return true;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_operation is not null) return;
        _history.IsEnabled = false;
        _save.IsEnabled = _copy.IsEnabled = _convert.IsEnabled = false;
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
            if (!_closed) { _history.IsEnabled = true; UpdateActions(); }
        }
    }

    private void UpdateActions()
    {
        var ready = _operation is null && !_editor.IsBusy;
        _save.IsEnabled = ready && _current is { IsVideo: false } && _editor.HasImage;
        _copy.IsEnabled = ready && _current is not null && (_current.IsVideo || _editor.HasImage);
        _open.IsEnabled = _folder.IsEnabled = _current is not null;
        _convert.IsEnabled = ready && _current is { } item && _actions.CanConvertToGif(item);
    }

    private void ShowError(string message)
    {
        if (_closed) return;
        _error.Title = "Hotshot needs your attention";
        _error.Message = message;
        _error.IsOpen = true;
    }

    private void HistoryChanged(object? sender, EventArgs args) => _app.Post(() => { if (!_closed) RenderHistory(); });

    private void RenderHistory()
    {
        _rendering = true;
        _history.Items.Clear();
        foreach (var item in _app.History.Items.Where(i => string.IsNullOrWhiteSpace(_search.Text) ||
                     i.FileName.Contains(_search.Text, StringComparison.OrdinalIgnoreCase) || i.Kind.DisplayName().Contains(_search.Text, StringComparison.OrdinalIgnoreCase)))
        {
            var body = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 8) };
            if (item.ThumbnailPath is { } thumbnail && File.Exists(thumbnail))
                body.Children.Add(new Image { Source = new BitmapImage(new Uri(thumbnail)) { DecodePixelWidth = 180, CreateOptions = BitmapCreateOptions.IgnoreImageCache }, Height = 96 });
            body.Children.Add(new TextBlock { Text = item.FileName, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(UiStyles.Description($"{item.Kind.DisplayName()} - {item.CreatedAt.LocalDateTime:t}"));
            var row = new ListViewItem { Content = body, Tag = item, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(row, $"Capture {item.FileName}");
            ToolTipService.SetToolTip(row, item.Path);
            _history.Items.Add(row);
            if (_current?.Id == item.Id) _history.SelectedItem = row;
        }
        _rendering = false;
    }
}
