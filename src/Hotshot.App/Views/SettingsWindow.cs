using Hotshot.Core;
using Hotshot.Core.Hotkeys;
using Hotshot.Core.Naming;
using Hotshot.Core.Settings;
using Hotshot.Interop;
using Hotshot.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.Windows.Storage.Pickers;
using System.Runtime.InteropServices;
using VirtualKey = Windows.System.VirtualKey;

namespace Hotshot.Views;

internal sealed class SettingsWindow : Window
{
    private readonly AppServices _app;
    private readonly HotkeyManager _hotkeys;
    private readonly AppSettings _draft;
    private readonly NavigationView _navigation = new()
    {
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        PaneDisplayMode = NavigationViewPaneDisplayMode.Auto, OpenPaneLength = 230,
        AlwaysShowHeader = false,
    };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly InfoBar _error = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly TextBlock _status = UiStyles.Description("Changes are saved automatically.");
    private readonly TextBlock _updateStatus = UiStyles.Description(string.Empty);
    private Button? _checkUpdates;
    private readonly Dictionary<SettingsPage, NavigationViewItem> _pages = [];
    private readonly Dictionary<HotkeyAction, TextBlock> _hotkeyLabels = [];
    private readonly Dictionary<string, Border> _settingCards = [];
    private readonly AutoSuggestBox _search = new()
    {
        PlaceholderText = "Search settings", QueryIcon = new SymbolIcon(Symbol.Find),
        MaxWidth = 480, MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TitleBar _titleBar = new() { Title = "Hotshot", Subtitle = "Settings" };
    private readonly Border _compactSearchHost = new()
    {
        Margin = new Thickness(16, 4, 16, 8), Visibility = Visibility.Collapsed,
    };
    private bool? _compactSearch;
    private SearchResult[] _searchResults = [];
    private string _searchQuery = string.Empty;
    private readonly DispatcherQueueTimer _saveTimer;
    private long _editVersion;
    private long _savedVersion;
    private bool _rendering;
    private bool _closed;
    private bool _allowClose;
    private bool _closing;
    private bool _pickingFolder;

    public SettingsWindow(AppServices app, HotkeyManager hotkeys)
    {
        _app = app;
        _hotkeys = hotkeys;
        _draft = app.Settings.Clone();
        Title = "Hotshot settings";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        _saveTimer = app.Dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(450);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await PersistAsync();

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(16, 12, 0, 16) };
        brand.Children.Add(new Image { Source = Logo(), Width = 32, Height = 32 });
        brand.Children.Add(new TextBlock { Text = "Hotshot", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        _navigation.PaneHeader = brand;
        AddPage(SettingsPage.General, "General", Symbol.Setting);
        AddPage(SettingsPage.Capture, "Screenshots", Symbol.Camera);
        AddPage(SettingsPage.Recording, "Screen recording", Symbol.Video);
        AddPage(SettingsPage.Gif, "GIF conversion", Symbol.RepeatAll);
        AddPage(SettingsPage.Hotkeys, "Keyboard shortcuts", Symbol.Keyboard);
        AddPage(SettingsPage.Naming, "File naming", Symbol.Folder);
        AddPage(SettingsPage.About, "About Hotshot", Symbol.Help, footer: true);
        _navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: SettingsPage page }) RenderPage(page);
        };

        AutomationProperties.SetName(_search, "Search settings");
        AutomationProperties.SetAutomationId(_search, "SettingsSearch");
        _search.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                _searchQuery = _search.Text;
                _searchResults = SearchCatalog()
                    .Select(result => (Result: result, Score: SettingsSearch.Score(_search.Text, result.Title, PageTitle(result.Page), result.Keywords)))
                    .Where(match => match.Score > 0).OrderByDescending(match => match.Score)
                    .ThenBy(match => match.Result.Title, StringComparer.OrdinalIgnoreCase).Select(match => match.Result).ToArray();
                _search.ItemsSource = _searchResults.Take(8).Select(DisplayResult)
                    .Concat(_searchResults.Length > 0 ? ["Show all results"] : Array.Empty<string>()).ToArray();
            }
        };
        _search.QuerySubmitted += (_, args) =>
        {
            var result = _searchResults.FirstOrDefault(result => DisplayResult(result) == args.ChosenSuggestion?.ToString());
            if (result is not null) NavigateSearch(result);
            else ShowSearchResults(args.ChosenSuggestion?.ToString() == "Show all results" ? _searchQuery : args.QueryText);
            _search.IsSuggestionListOpen = false;
        };
        _search.SuggestionChosen += (_, args) =>
        {
            if (_searchResults.FirstOrDefault(result => DisplayResult(result) == args.SelectedItem?.ToString()) is { } result)
                NavigateSearch(result);
            else if (args.SelectedItem?.ToString() == "Show all results")
                ShowSearchResults(_searchQuery);
        };

        var content = new Grid { Padding = new Thickness(28, 24, 28, 16), RowSpacing = 12 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(_error);
        Grid.SetRow(_scroll, 1);
        content.Children.Add(_scroll);
        Grid.SetRow(_status, 2);
        content.Children.Add(_status);
        _navigation.Content = content;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _titleBar.IconSource = new ImageIconSource { ImageSource = Logo() };
        _titleBar.Content = _search;
        root.Children.Add(_titleBar);
        Grid.SetRow(_compactSearchHost, 1);
        root.Children.Add(_compactSearchHost);
        Grid.SetRow(_navigation, 2);
        root.Children.Add(_navigation);
        root.SizeChanged += (_, args) => UpdateSearchLayout(args.NewSize.Width);
        root.Loaded += (_, _) => UpdateSearchLayout(root.ActualWidth);
        Content = root;
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        find.Invoked += (_, args) => { _search.Focus(FocusState.Keyboard); args.Handled = true; };
        root.KeyboardAccelerators.Add(find);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_titleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        UiStyles.ApplyTheme(this, _draft.General.Theme);
        _hotkeys.StatusChanged += RefreshHotkeyStatus;
        if (_app.Updates is { } updates) updates.StatusChanged += RefreshUpdateStatus;
        AppWindow.Closing += async (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            await Task.Yield();
            _saveTimer.Stop();
            await PersistAsync();
            _allowClose = true;
            Close();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _saveTimer.Stop();
            _hotkeys.StatusChanged -= RefreshHotkeyStatus;
            if (_app.Updates is { } updates) updates.StatusChanged -= RefreshUpdateStatus;
        };
        WindowTools.Configure(this, 1050, 790);
    }

    public void SelectPage(SettingsPage page)
    {
        _navigation.SelectedItem = _pages[page];
        Activate();
    }

    public Task SavePendingAsync() => PersistAsync();
    public bool HasPendingChanges => _editVersion != _savedVersion || _closing || _pickingFolder;

    private void UpdateSearchLayout(double width)
    {
        if (width <= 0) return;
        var compact = width < 760;
        if (_compactSearch == compact) return;
        var focused = false;
        for (var element = _search.XamlRoot is { } root
            ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as DependencyObject : null;
            element is not null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element != _search) continue;
            focused = true;
            break;
        }
        _search.IsSuggestionListOpen = false;
        if (compact)
        {
            _titleBar.Content = null;
            _search.MinWidth = 0;
            _search.MaxWidth = double.PositiveInfinity;
            _compactSearchHost.Child = _search;
            _compactSearchHost.Visibility = Visibility.Visible;
        }
        else
        {
            _compactSearchHost.Child = null;
            _compactSearchHost.Visibility = Visibility.Collapsed;
            _search.MinWidth = 150;
            _search.MaxWidth = 480;
            _titleBar.Content = _search;
        }
        _compactSearch = compact;
        if (focused) _app.Post(() => { if (!_closed) _search.Focus(FocusState.Programmatic); });
    }

    private void AddPage(SettingsPage page, string title, Symbol icon, bool footer = false)
    {
        var item = new NavigationViewItem { Content = title, Icon = new SymbolIcon(icon), Tag = page };
        AutomationProperties.SetAutomationId(item, $"Settings{page}");
        _pages[page] = item;
        if (footer) _navigation.FooterMenuItems.Add(item); else _navigation.MenuItems.Add(item);
    }

    private string PageTitle(SettingsPage page) => _pages[page].Content.ToString() ?? page.ToString();
    private string DisplayResult(SearchResult result) => $"{result.Title} - {PageTitle(result.Page)}";

    private static IEnumerable<SearchResult> SearchCatalog()
    {
        yield return new(SettingsPage.General, "App theme", "dark light system appearance color title bar");
        yield return new(SettingsPage.General, "Start with Windows", "startup sign in launch login");
        yield return new(SettingsPage.General, "Captures folder", "save output location directory path");
        yield return new(SettingsPage.General, "Copy captures to clipboard", "png paste image");
        yield return new(SettingsPage.General, "Save captures to files", "disk output png");
        yield return new(SettingsPage.General, "Recent captures", "history editor limit size");
        yield return new(SettingsPage.Capture, "Snap to windows", "region selection screenshot");
        yield return new(SettingsPage.Capture, "Show magnifier", "zoom pixels selection screenshot");
        yield return new(SettingsPage.Capture, "Show crosshair", "alignment selection screenshot");
        yield return new(SettingsPage.Capture, "Include cursor", "mouse pointer screenshot");
        foreach (var action in Enum.GetValues<HotkeyAction>())
            yield return new(SettingsPage.Hotkeys, action.DisplayName(), $"shortcut hotkey keyboard {action.Description()}");
        yield return new(SettingsPage.Hotkeys, "Print Screen and Snipping Tool", "windows shortcut conflict");
        yield return new(SettingsPage.Naming, "Screenshots", "filename file naming pattern tokens timestamp counter folder");
        yield return new(SettingsPage.Naming, "Recordings and GIFs", "filename file naming pattern tokens timestamp counter folder");
        yield return new(SettingsPage.Recording, "Frame rate", "fps frames video");
        yield return new(SettingsPage.Recording, "Quality", "bitrate video file size");
        yield return new(SettingsPage.Recording, "Include cursor", "mouse pointer video");
        yield return new(SettingsPage.Recording, "Countdown", "start seconds delay video");
        yield return new(SettingsPage.Recording, "Record system audio", "sound playback speakers");
        yield return new(SettingsPage.Recording, "Record microphone", "audio voice input");
        yield return new(SettingsPage.Recording, "Also create a GIF", "convert animation mp4");
        yield return new(SettingsPage.Gif, "Frame rate", "fps frames animation");
        yield return new(SettingsPage.Gif, "Maximum width", "size scale resize animation");
        yield return new(SettingsPage.Gif, "Dither colors", "palette gradient animation");
        yield return new(SettingsPage.Gif, "Loop animation", "repeat gif");
        yield return new(SettingsPage.About, "Automatic updates", "version releases github download install");
        yield return new(SettingsPage.About, "Diagnostics", "logs errors troubleshooting");
    }

    private void NavigateSearch(SearchResult result)
    {
        if (ReferenceEquals(_navigation.SelectedItem, _pages[result.Page])) RenderPage(result.Page);
        else _navigation.SelectedItem = _pages[result.Page];
        _app.Post(() =>
        {
            if (_closed || !_settingCards.TryGetValue(result.Title, out var card)) return;
            card.BorderThickness = new Thickness(2);
            card.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 106, 61));
            card.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.2 });
        });
    }

    private void ShowSearchResults(string query)
    {
        var results = SearchCatalog()
            .Select(result => (Result: result, Score: SettingsSearch.Score(query, result.Title, PageTitle(result.Page), result.Keywords)))
            .Where(match => match.Score > 0).OrderByDescending(match => match.Score).Select(match => match.Result).ToArray();
        var panel = new StackPanel { Spacing = 10, MaxWidth = 920, Padding = new Thickness(0, 0, 4, 20) };
        panel.Children.Add(new TextBlock { Text = "Search results", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(UiStyles.Description(results.Length == 0 ? $"No settings match \"{query}\"." : $"{results.Length} settings match \"{query}\"."));
        foreach (var result in results)
        {
            var button = new Button { Content = UiStyles.Card(new StackPanel
            {
                Spacing = 4, Children = { new TextBlock { Text = result.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, UiStyles.Description(PageTitle(result.Page)) },
            }), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0) };
            AutomationProperties.SetName(button, DisplayResult(result));
            button.Click += (_, _) => NavigateSearch(result);
            panel.Children.Add(button);
        }
        _scroll.Content = panel;
        _scroll.ChangeView(null, 0, null);
    }

    private void RenderPage(SettingsPage page)
    {
        _rendering = true;
        _hotkeyLabels.Clear();
        _settingCards.Clear();
        var panel = new StackPanel { Spacing = 8, MaxWidth = 920, Padding = new Thickness(0, 0, 4, 20) };
        panel.Children.Add(new TextBlock { Text = _pages[page].Content.ToString(), FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(UiStyles.Description(page switch
        {
            SettingsPage.General => "Make Hotshot work the way you do. Changes apply immediately.",
            SettingsPage.Capture => "Fast, silent screenshots. Saved as PNG and copied without opening a window.",
            SettingsPage.Hotkeys => "Global shortcuts that work while Hotshot is in the tray.",
            SettingsPage.Naming => "Organize captures with folders and filename tokens.",
            SettingsPage.Recording => "Record a window or region with optional audio.",
            SettingsPage.Gif => "Turn recordings into shareable animations. Original videos are kept.",
            _ => "A lightweight native capture utility, always one shortcut away.",
        }));
        switch (page)
        {
            case SettingsPage.General:
                Section(panel, "Appearance and behavior");
                Choice(panel, "App theme", "Follow Windows or choose a light or dark appearance.",
                    Enum.GetValues<AppTheme>(), _draft.General.Theme, v => _draft.General.Theme = v);
                Toggle(panel, "Start with Windows", "Run in the tray when you sign in.", _draft.General.StartWithWindows, v => _draft.General.StartWithWindows = v);
                Section(panel, "Output");
                CapturesFolder(panel);
                Toggle(panel, "Copy captures to clipboard", "PNG images for screenshots; files for recordings and GIFs.", _draft.General.CopyToClipboard, v => _draft.General.CopyToClipboard = v);
                Toggle(panel, "Save captures to files", "Keep captures on disk as well as in recent history.", _draft.General.SaveToFile, v => _draft.General.SaveToFile = v);
                Section(panel, "History and editor");
                Number(panel, "Recent captures", "Saved files are never deleted when history is trimmed.", _draft.General.HistorySize, 5, 500, v => _draft.General.HistorySize = v);
                Note(panel, "Click the tray icon to open the editor and history. Nothing opens automatically after a screenshot.");
                break;
            case SettingsPage.Capture:
                Section(panel, "Selection");
                Toggle(panel, "Snap to windows", "Click a window instead of dragging a region.", _draft.Capture.SnapToWindows, v => _draft.Capture.SnapToWindows = v);
                Toggle(panel, "Show magnifier", "Inspect individual pixels while selecting a region.", _draft.Capture.ShowMagnifier, v => _draft.Capture.ShowMagnifier = v);
                Toggle(panel, "Show crosshair", "Align the selection precisely.", _draft.Capture.ShowCrosshair, v => _draft.Capture.ShowCrosshair = v);
                Toggle(panel, "Include cursor", "Include the mouse pointer in the saved screenshot.", _draft.Capture.IncludeCursor, v => _draft.Capture.IncludeCursor = v);
                Note(panel, "Drag a region, click a window, or press Space for the monitor under the cursor. Escape cancels.");
                break;
            case SettingsPage.Hotkeys:
                Section(panel, "Activation shortcuts");
                foreach (var action in Enum.GetValues<HotkeyAction>())
                {
                    var label = UiStyles.Description(string.Empty);
                    _hotkeyLabels[action] = label;
                    var button = new Button { Content = DisplayShortcut(action), MinWidth = 140 };
                    AutomationProperties.SetName(button, $"Edit {action.DisplayName()} shortcut");
                    button.Click += async (_, _) => await EditShortcutAsync(action, button);
                    var body = new StackPanel { Spacing = 6 };
                    body.Children.Add(UiStyles.Description(action.Description()));
                    body.Children.Add(label);
                    Card(panel, action.DisplayName(), body, button);
                }
                RefreshHotkeyStatus();
                Section(panel, "Windows shortcuts");
                var windows = new Button { Content = "Open Windows keyboard settings" };
                windows.Click += (_, _) => CaptureActions.OpenPath("ms-settings:easeofaccess-keyboard");
                Card(panel, "Print Screen and Snipping Tool",
                    UiStyles.Description("If Windows reserves Print Screen, turn off its Snipping Tool option or choose another shortcut."), windows);
                break;
            case SettingsPage.Naming:
                Section(panel, "Naming patterns");
                Pattern(panel, "Screenshots", _draft.Naming.ScreenshotPattern, CaptureKind.Region, v => _draft.Naming.ScreenshotPattern = v);
                Pattern(panel, "Recordings and GIFs", _draft.Naming.RecordingPattern, CaptureKind.Recording, v => _draft.Naming.RecordingPattern = v);
                Note(panel, @"Use \ for subfolders. Extensions are automatic and collisions receive a numeric suffix.");
                var tokens = new StackPanel { Spacing = 8 };
                var sample = NamingContext.Sample();
                foreach (var token in FileNameTemplate.Tokens)
                    tokens.Children.Add(UiStyles.Description($"{token.Token}  -  {token.Description}\nExample: {FileNameTemplate.Expand(token.Token, sample)}"));
                panel.Children.Add(new Expander { Header = "Available tokens", Content = tokens, HorizontalAlignment = HorizontalAlignment.Stretch });
                break;
            case SettingsPage.Recording:
                Section(panel, "Video");
                Choice(panel, "Frame rate", "Frames recorded each second.", new[] { 15, 24, 30, 60 }, _draft.Recording.FramesPerSecond, v => _draft.Recording.FramesPerSecond = v);
                Choice(panel, "Quality", "Higher quality increases file size.", Enum.GetValues<RecordingQuality>(), _draft.Recording.Quality, v => _draft.Recording.Quality = v);
                Toggle(panel, "Include cursor", "Show the pointer in the recording.", _draft.Recording.IncludeCursor, v => _draft.Recording.IncludeCursor = v);
                Number(panel, "Countdown", "Seconds before recording begins.", _draft.Recording.CountdownSeconds, 0, 10, v => _draft.Recording.CountdownSeconds = v);
                Section(panel, "Audio");
                Toggle(panel, "Record system audio", "Include audio played by Windows and apps.", _draft.Recording.CaptureSystemAudio, v => _draft.Recording.CaptureSystemAudio = v);
                Toggle(panel, "Record microphone", "Use the default Windows microphone.", _draft.Recording.CaptureMicrophone, v => _draft.Recording.CaptureMicrophone = v);
                Toggle(panel, "Also create a GIF", "Convert after recording and keep the MP4.", _draft.Recording.AlsoCreateGif, v => _draft.Recording.AlsoCreateGif = v);
                Note(panel, "Disable both audio sources for silent video. Recording regions must fit within a single monitor.");
                break;
            case SettingsPage.Gif:
                Section(panel, "Animation");
                Number(panel, "Frame rate", "Lower frame rates produce smaller files.", _draft.Gif.FramesPerSecond, 5, 30, v => _draft.Gif.FramesPerSecond = v);
                Number(panel, "Maximum width", "0 keeps the source width; aspect ratio is preserved.", _draft.Gif.MaxWidth, 0, 3840, v => _draft.Gif.MaxWidth = v);
                Toggle(panel, "Dither colors", "Smooth gradients in the GIF's limited color palette.", _draft.Gif.Dither, v => _draft.Gif.Dither = v);
                Toggle(panel, "Loop animation", "Repeat the animation continuously.", _draft.Gif.Loop, v => _draft.Gif.Loop = v);
                Note(panel, "GIFs have no audio. Select a recording in history to convert it.");
                break;
            case SettingsPage.About:
                panel.Children.Add(new Image { Source = Logo(), Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 24, 0, 12) });
                Section(panel, "Hotshot");
                Note(panel, $"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)} - native Windows screenshots, recordings and annotation.");
                Note(panel, "A PowerToys-inspired experience built with WinUI 3. No accounts, uploads or background services.");
                Section(panel, "Updates");
                _checkUpdates = new Button { Content = "Check now" };
                _checkUpdates.Click += async (_, _) => { if (_app.Updates is { } updates) await updates.CheckAsync(); };
                Card(panel, "Automatic updates", _updateStatus, _checkUpdates);
                RefreshUpdateStatus();
                Note(panel, "New versions download from GitHub Releases. Updates restart Hotshot silently only when capture, recording and editing are idle.");
                var releases = new Button { Content = "Open GitHub Releases" };
                releases.Click += (_, _) => CaptureActions.OpenPath(Hotshot.Updates.AutomaticUpdates.RepositoryUrl + "/releases/latest");
                panel.Children.Add(releases);
                var logs = new Button { Content = "Open diagnostic log" };
                logs.Click += (_, _) => CaptureActions.OpenPath(Log.FilePath);
                Card(panel, "Diagnostics", UiStyles.Description("Local logs help diagnose capture and shortcut issues."), logs);
                break;
        }
        _scroll.Content = panel;
        _scroll.ChangeView(null, 0, null);
        _rendering = false;
    }

    private void Changed()
    {
        if (_rendering || _closed) return;
        _editVersion++;
        _status.Text = "Saving changes...";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task PersistAsync()
    {
        if (_editVersion == _savedVersion) return;
        var version = _editVersion;
        var errors = _draft.Validate();
        if (errors.Count > 0)
        {
            _error.Title = "These changes cannot be applied";
            _error.Message = string.Join(Environment.NewLine, errors);
            _error.IsOpen = true;
            _status.Text = "Invalid changes have not been saved.";
            return;
        }
        var snapshot = _draft.Clone();
        if (await _app.ApplySettingsAsync(snapshot))
        {
            _savedVersion = version;
            if (_closed) return;
            _error.IsOpen = false;
            if (version == _editVersion) _status.Text = "All changes saved.";
        }
        else if (!_closed)
        {
            _error.Title = "Settings could not be saved";
            _error.Message = "Your last saved configuration is still available. See the diagnostic log.";
            _error.IsOpen = true;
        }
    }

    private async Task EditShortcutAsync(HotkeyAction action, Button button)
    {
        using var lease = _hotkeys.Suspend();
        var value = _draft.Hotkeys.Get(action);
        var input = new TextBox { IsReadOnly = true, Text = DisplayShortcut(action), Header = "Press the shortcut you want to use" };
        input.KeyDown += (_, args) =>
        {
            if (args.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.Escape) return;
            var modifiers = HotkeyModifiers.None;
            if (Win32.IsKeyDown(Win32.VK_CONTROL)) modifiers |= HotkeyModifiers.Ctrl;
            if (Win32.IsKeyDown(Win32.VK_MENU)) modifiers |= HotkeyModifiers.Alt;
            if (Win32.IsKeyDown(Win32.VK_SHIFT)) modifiers |= HotkeyModifiers.Shift;
            if (Win32.IsKeyDown(Win32.VK_LWIN) || Win32.IsKeyDown(Win32.VK_RWIN)) modifiers |= HotkeyModifiers.Win;
            var key = new Hotkey(modifiers, (int)args.Key);
            value = key.ToString();
            input.Text = key.ToDisplayString();
            args.Handled = true;
        };
        var dialog = new ContentDialog
        {
            Title = action.DisplayName(), Content = input, PrimaryButtonText = "Apply",
            SecondaryButtonText = "Clear shortcut", CloseButtonText = "Cancel", XamlRoot = _navigation.XamlRoot,
        };
        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        _draft.Hotkeys.Set(action, result == ContentDialogResult.Secondary ? string.Empty : value);
        button.Content = DisplayShortcut(action);
        Changed();
    }

    private string DisplayShortcut(HotkeyAction action) => Hotkey.TryParse(_draft.Hotkeys.Get(action), out var key)
        ? key.ToDisplayString() : _draft.Hotkeys.Get(action);

    private void RefreshHotkeyStatus()
    {
        foreach (var (action, label) in _hotkeyLabels)
            label.Text = _hotkeys.Status.TryGetValue(action, out var status) ? status.Message : "Not set";
    }

    private void RefreshUpdateStatus()
    {
        if (_closed) return;
        _updateStatus.Text = _app.Updates?.Status ?? "Update service is starting.";
        if (_checkUpdates is not null)
            _checkUpdates.IsEnabled = _app.Updates is { IsManaged: true, IsChecking: false };
    }

    private void Toggle(StackPanel panel, string title, string description, bool current, Action<bool> changed)
    {
        var input = new ToggleSwitch { IsOn = current, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(input, title);
        input.Toggled += (_, _) => { changed(input.IsOn); Changed(); };
        Card(panel, title, UiStyles.Description(description), input);
    }

    private void CapturesFolder(StackPanel panel)
    {
        var path = UiStyles.Description(_draft.General.SaveFolder);
        path.IsTextSelectionEnabled = true;
        AutomationProperties.SetAutomationId(path, "CapturesFolderPath");
        var browse = new Button { Content = "Choose folder...", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(browse, "Choose captures folder");
        AutomationProperties.SetAutomationId(browse, "CapturesFolderPicker");
        browse.Click += async (_, _) =>
        {
            if (_pickingFolder) return;
            _pickingFolder = true;
            browse.IsEnabled = false;
            try
            {
                var picker = new FolderPicker(AppWindow.Id)
                {
                    Title = "Choose captures folder",
                    CommitButtonText = "Use this folder",
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                    SuggestedFolder = _draft.General.SaveFolder,
                };
                var folder = await picker.PickSingleFolderAsync();
                if (_closed || folder is null) return;
                _draft.General.SaveFolder = folder.Path;
                path.Text = folder.Path;
                Changed();
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                Log.Error("Could not select the captures folder", ex);
                if (_closed) return;
                _error.Title = "Could not select the captures folder";
                _error.Message = ex.Message;
                _error.IsOpen = true;
            }
            finally
            {
                _pickingFolder = false;
                browse.IsEnabled = true;
            }
        };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(UiStyles.Description("Choose the root folder for all saved screenshots and recordings."));
        body.Children.Add(path);
        body.Children.Add(browse);
        Card(panel, "Captures folder", body);
    }

    private void Pattern(StackPanel panel, string title, string current, CaptureKind kind, Action<string> changed)
    {
        var sample = NamingContext.Sample(kind);
        var input = new TextBox { Text = current };
        AutomationProperties.SetName(input, title);
        AutomationProperties.SetAutomationId(input, $"Naming{kind}Pattern");
        var preview = UiStyles.Description(string.Empty);
        AutomationProperties.SetAutomationId(preview, $"Naming{kind}Preview");
        void Update(string value)
        {
            changed(value);
            var invalid = FileNameTemplate.FindInvalidTokens(value);
            preview.Text = invalid.Count == 0 ? "Example: " + FileNameTemplate.BuildRelativePath(value, sample)
                : "Unknown tokens: " + string.Join(", ", invalid);
        }
        input.TextChanged += (_, _) => { Update(input.Text); Changed(); };
        var tokens = new ComboBox
        {
            PlaceholderText = "Insert a token",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(tokens, $"Insert token into {title}");
        AutomationProperties.SetAutomationId(tokens, $"Naming{kind}Tokens");
        foreach (var token in FileNameTemplate.Tokens)
        {
            var example = FileNameTemplate.Expand(token.Token, sample);
            var label = new StackPanel { Spacing = 4, MaxWidth = 360 };
            label.Children.Add(new TextBlock { Text = $"{token.Token} - {token.Description}", TextWrapping = TextWrapping.Wrap });
            label.Children.Add(UiStyles.Description($"Example: {example}"));
            var item = new ComboBoxItem { Content = label, Tag = token.Token };
            AutomationProperties.SetName(item, $"{token.Token} - {token.Description}. Example: {example}");
            tokens.Items.Add(item);
        }
        tokens.SelectionChanged += (_, _) =>
        {
            if (tokens.SelectedItem is not ComboBoxItem { Tag: string token }) return;
            var start = input.SelectionStart;
            input.SelectedText = token;
            tokens.SelectedIndex = -1;
            input.Focus(FocusState.Programmatic);
            input.Select(start + token.Length, 0);
        };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(UiStyles.Description("Type a pattern or insert a token at the cursor. Examples show how each token expands."));
        body.Children.Add(input);
        body.Children.Add(tokens);
        body.Children.Add(preview);
        Card(panel, title, body);
        Update(current);
    }

    private void Number(StackPanel panel, string title, string description, int current, int min, int max, Action<int> changed)
    {
        var input = new NumberBox { Value = current, Minimum = min, Maximum = max, Width = 112, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        AutomationProperties.SetName(input, title);
        input.ValueChanged += (_, args) => { if (double.IsFinite(args.NewValue)) { changed((int)args.NewValue); Changed(); } };
        Card(panel, title, UiStyles.Description(description), input);
    }

    private void Choice<T>(StackPanel panel, string title, string description, T[] choices, T current, Action<T> changed)
    {
        var input = new ComboBox { ItemsSource = choices, SelectedItem = current, MinWidth = 112 };
        AutomationProperties.SetName(input, title);
        input.SelectionChanged += (_, _) => { if (input.SelectedItem is T value) { changed(value); Changed(); } };
        Card(panel, title, UiStyles.Description(description), input);
    }

    private void Card(StackPanel panel, string title, UIElement description, FrameworkElement? control = null)
    {
        var grid = new Grid { ColumnSpacing = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(description);
        grid.Children.Add(body);
        if (control is not null) { Grid.SetColumn(control, 1); grid.Children.Add(control); }
        var card = UiStyles.Card(grid);
        _settingCards[title] = card;
        panel.Children.Add(card);
    }

    private static void Section(StackPanel panel, string title) => panel.Children.Add(new TextBlock
    {
        Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 4),
    });
    private static void Note(StackPanel panel, string text) => panel.Children.Add(UiStyles.Description(text));
    private static BitmapImage Logo() => new(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Logo.png")));
    private sealed record SearchResult(SettingsPage Page, string Title, string Keywords);
}
