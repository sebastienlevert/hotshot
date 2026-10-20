using Hotshot.Core;
using Hotshot.Core.Hotkeys;
using Hotshot.Core.Naming;
using Hotshot.Core.Settings;
using Hotshot.Core.Descriptions;
using Hotshot.Interop;
using Hotshot.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.Windows.Storage.Pickers;
using CommunityToolkit.WinUI.Controls;
using System.Runtime.InteropServices;
using Hotshot.Core.Agents;
using Hotshot.Capture;
using Hotshot.Recording;
using VirtualKey = Windows.System.VirtualKey;

namespace Hotshot.Views;

internal sealed partial class SettingsWindow : Window
{
    private static readonly int[] FrameRates = [15, 24, 30, 60];
    private readonly AppServices _app;
    private readonly HotkeyManager _hotkeys;
    private AppSettings _draft;
    private AppSettings _draftBaseline;
    private TextBlock _syncStatus = UiStyles.Description(string.Empty);
    private readonly NavigationView _navigation = new()
    {
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false,
        PaneDisplayMode = NavigationViewPaneDisplayMode.Auto, OpenPaneLength = 230,
        AlwaysShowHeader = false,
    };
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly InfoBar _error = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly TextBlock _status = UiStyles.Description("Saved automatically.");
    private TextBlock _updateStatus = UiStyles.Description(string.Empty);
    private TextBlock _agentStatus = UiStyles.Description(string.Empty);
    private TextBlock _aiStatus = UiStyles.Description(string.Empty);
    private Button? _checkUpdates;
    private readonly Dictionary<SettingsPage, NavigationViewItem> _pages = [];
    private readonly Dictionary<HotkeyAction, TextBlock> _hotkeyLabels = [];
    private readonly Dictionary<string, SettingsCard> _settingCards = [];
    private readonly AutoSuggestBox _search = new()
    {
        PlaceholderText = "Search settings", QueryIcon = new SymbolIcon(Symbol.Find),
        MaxWidth = 480, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TitleBar _titleBar = new()
    {
        Title = "Hotshot", Subtitle = "Settings", MinHeight = 48, IsPaneToggleButtonVisible = true,
    };
    private bool? _compactTitle;
    private XamlRoot? _titleBarRoot;
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

    public SettingsWindow(AppServices app, HotkeyManager hotkeys,
        Func<CancellationToken, Task<ICopilotSignInClient>>? copilotAccountFactory = null,
        Func<Uri, CancellationToken, Task>? copilotBrowser = null)
    {
        _app = app;
        _hotkeys = hotkeys;
        _copilotAccountFactory = copilotAccountFactory;
        _copilotBrowser = copilotBrowser;
        _draft = app.Settings.Clone();
        _draftBaseline = app.Settings.Clone();
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
        AddPage(SettingsPage.Agents, "AI assistants", Symbol.Remote);
        AddPage(SettingsPage.About, "About Hotshot", Symbol.Help, footer: true);
        _navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: SettingsPage page }) RenderPage(page);
        };

        AutomationProperties.SetName(_search, "Search settings");
        AutomationProperties.SetAutomationId(_search, "SettingsSearch");
        AutomationProperties.SetAutomationId(_scroll, "SettingsContentScroll");
        UiStyles.LiveStatus(_status, "SettingsSaveStatus");
        AutomationProperties.SetAutomationId(_error, "SettingsError");
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

        var content = new Grid
        {
            Padding = new Thickness(28, 24, 28, 16), RowSpacing = 12,
            MaxWidth = 976, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
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
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _titleBar.IconSource = new ImageIconSource { ImageSource = Logo() };
        _titleBar.PaneToggleRequested += (_, _) => _navigation.IsPaneOpen = !_navigation.IsPaneOpen;
        _titleBar.Content = _search;
        _titleBar.Resources["TitleBarContentHorizontalAlignment"] = HorizontalAlignment.Stretch;
        AutomationProperties.SetAutomationId(_titleBar, "SettingsTitleBar");
        _titleBar.SizeChanged += (_, _) => UpdateTitleBarInsets();
        _titleBar.RegisterPropertyChangedCallback(FrameworkElement.FlowDirectionProperty, (_, _) => UpdateTitleBarInsets());
        _titleBar.Loaded += (_, _) =>
        {
            if (_titleBarRoot is not null) _titleBarRoot.Changed -= TitleBarRootChanged;
            _titleBarRoot = _titleBar.XamlRoot;
            _titleBarRoot.Changed += TitleBarRootChanged;
            UpdateTitleBarInsets();
        };
        root.Children.Add(_titleBar);
        Grid.SetRow(_navigation, 1);
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
        if (_app.AgentServer is { } agents) agents.StatusChanged += RefreshAgentStatus;
        if (_app.Descriptions is { } descriptions) descriptions.Changed += RefreshAiStatus;
        _app.SettingsSyncStatusChanged += RefreshSyncStatus;
        _app.SharedSettingsReloaded += ReloadSharedPreferences;
        AppWindow.Closing += async (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            await Task.Yield();
            _saveTimer.Stop();
            await CancelCopilotSignInAsync();
            await PersistAsync();
            _allowClose = true;
            Close();
        };
        Closed += (_, _) =>
        {
            _copilotAccountCancellation?.Cancel();
            CloseAiComponent();
            _closed = true;
            _saveTimer.Stop();
            if (_titleBarRoot is not null) _titleBarRoot.Changed -= TitleBarRootChanged;
            _hotkeys.StatusChanged -= RefreshHotkeyStatus;
            if (_app.Updates is { } updates) updates.StatusChanged -= RefreshUpdateStatus;
            if (_app.AgentServer is { } agents) agents.StatusChanged -= RefreshAgentStatus;
            if (_app.Descriptions is { } descriptions) descriptions.Changed -= RefreshAiStatus;
            _app.SettingsSyncStatusChanged -= RefreshSyncStatus;
            _app.SharedSettingsReloaded -= ReloadSharedPreferences;
        };
        WindowTools.Configure(this, 1050, 790, excludeFromCapture: false, app: _app, layoutKey: "settings");
    }

    public void SelectPage(SettingsPage page)
    {
        _navigation.SelectedItem = _pages[page];
        Activate();
    }

    public Task SavePendingAsync() => PersistAsync();
    public bool HasPendingChanges => _editVersion != _savedVersion || _closing || _pickingFolder ||
        IsQuickStartActive || IsQuickStartBusy || IsAiComponentBusy || IsCopilotAccountBusy;

    private void TitleBarRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateTitleBarInsets();

    private void UpdateTitleBarInsets()
    {
        if (_titleBar.XamlRoot is not { } root || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(_titleBar) == 0) return;
        var layout = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(_titleBar, 0) as FrameworkElement;
        if (layout?.FindName("LeftPaddingColumn") is not ColumnDefinition left ||
            layout.FindName("RightPaddingColumn") is not ColumnDefinition right)
        {
            Log.Warn("The WinUI title bar template does not expose its caption-inset columns.");
            return;
        }

        // WinUI's TitleBar currently treats physical caption insets as DIPs (microsoft-ui-xaml#10344).
        var native = AppWindow.TitleBar;
        var rtl = _titleBar.FlowDirection == FlowDirection.RightToLeft;
        left.Width = new GridLength((rtl ? native.RightInset : native.LeftInset) / root.RasterizationScale);
        right.Width = new GridLength((rtl ? native.LeftInset : native.RightInset) / root.RasterizationScale);
    }

    private void UpdateSearchLayout(double width)
    {
        if (width <= 0) return;
        var compact = width < 760;
        if (_compactTitle == compact) return;
        _titleBar.Title = compact ? string.Empty : "Hotshot";
        _titleBar.Subtitle = compact ? string.Empty : "Settings";
        _compactTitle = compact;
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
        yield return new(SettingsPage.Capture, "Copilot descriptions", "ai describe screenshots summary search metadata image properties privacy");
        yield return new(SettingsPage.Capture, "Copilot account", "github sign in login authenticate browser account credentials");
        yield return new(SettingsPage.Capture, "Read text in screenshots", "recognize ocr copy search text offline locally privacy");
        yield return new(SettingsPage.Capture, "Screenshot delay", "countdown timed capture seconds");
        foreach (var action in Enum.GetValues<HotkeyAction>())
            yield return new(SettingsPage.Hotkeys, action.DisplayName(), $"shortcut hotkey keyboard {action.Description()}");
        yield return new(SettingsPage.Hotkeys, "Print Screen and Snipping Tool", "windows shortcut conflict");
        yield return new(SettingsPage.Naming, "Screenshots", "filename file naming pattern tokens timestamp counter folder");
        yield return new(SettingsPage.Naming, "Recordings and GIFs", "filename file naming pattern tokens timestamp counter folder");
        yield return new(SettingsPage.Recording, "Frame rate", "fps frames video");
        yield return new(SettingsPage.Recording, "Quality", "bitrate video file size");
        yield return new(SettingsPage.Recording, "Include cursor", "mouse pointer video");
        yield return new(SettingsPage.Recording, "Countdown", "start seconds delay video");
        yield return new(SettingsPage.Recording, "Timer size", "mini small recording time controls");
        yield return new(SettingsPage.Recording, "Timer corner", "placement position top right left bottom recording");
        yield return new(SettingsPage.Recording, "Record system audio", "sound playback speakers");
        yield return new(SettingsPage.Recording, "Record microphone", "audio voice input");
        yield return new(SettingsPage.Recording, "Also create a GIF", "convert animation mp4");
        yield return new(SettingsPage.Gif, "Frame rate", "fps frames animation");
        yield return new(SettingsPage.Gif, "Maximum width", "size scale resize animation");
        yield return new(SettingsPage.Gif, "Smoother colors", "dither palette gradient animation");
        yield return new(SettingsPage.Gif, "Loop animation", "repeat gif");
        yield return new(SettingsPage.About, "Automatic updates", "version releases github download install");
        yield return new(SettingsPage.About, "Troubleshooting", "diagnostics logs errors");
        yield return new(SettingsPage.Agents, "Allow assistant access", "mcp coding agents screenshots automation permissions");
        yield return new(SettingsPage.Agents, "Allow saved screenshots", "history mcp search ocr descriptions privacy");
        yield return new(SettingsPage.Agents, "Connect an assistant", "coding agent copilot claude cursor vscode codex mcp configuration skills");
        yield return new(SettingsPage.Agents, "Extra tools", "plugins skills marketplace install visual verification screenshot");
        yield return new(SettingsPage.General, "Keep saved captures searchable", "library retention archive history");
        yield return new(SettingsPage.General, "Settings across devices", "onedrive sync roaming preferences machines cloud settings");
        yield return new(SettingsPage.Capture, "Review before sending", "ai cloud analysis privacy redact approval");
        yield return new(SettingsPage.Capture, "Exclude apps", "applications ai privacy process cloud");
        yield return new(SettingsPage.Recording, "Microphone device", "audio input selection");
        yield return new(SettingsPage.Agents, "Allowed apps", "agent applications privacy restrictions allowlist");
        yield return new(SettingsPage.Agents, "Allow full-screen capture", "agent permissions privacy monitor");
        yield return new(SettingsPage.Agents, "Allow area capture", "agent permissions privacy region");
        yield return new(SettingsPage.Agents, "Temporary access only", "timed approval permissions session expire");
        yield return new(SettingsPage.Agents, "Ask each time", "agent request consent permissions approval");
        yield return new(SettingsPage.Capture, "Capture covered windows", "unobscured wgc windows graphics hdr dpi");
    }

    private void NavigateSearch(SearchResult result)
    {
        if (ReferenceEquals(_navigation.SelectedItem, _pages[result.Page])) RenderPage(result.Page);
        else _navigation.SelectedItem = _pages[result.Page];
        _app.Post(() =>
        {
            if (_closed || !_settingCards.TryGetValue(result.Title, out var card)) return;
            card.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.2 });
            if (Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(card) is Control target)
                target.Focus(FocusState.Keyboard);
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
            SettingsPage.General => "Make Hotshot yours.",
            SettingsPage.Capture => "Capture quietly. Edit when you want.",
            SettingsPage.Hotkeys => "Capture without opening Hotshot.",
            SettingsPage.Naming => "Choose how your files are named.",
            SettingsPage.Recording => "Choose video and sound options.",
            SettingsPage.Gif => "Turn a recording into a GIF.",
            SettingsPage.Agents => "Let trusted assistants use your screenshots.",
            _ => "Screenshots, recordings and quick edits.",
        }));
        switch (page)
        {
            case SettingsPage.General:
                Section(panel, "Appearance");
                Choice(panel, "App theme", "Match Windows, or choose light or dark.",
                    Enum.GetValues<AppTheme>(), _draft.General.Theme, v => _draft.General.Theme = v);
                Toggle(panel, "Start with Windows", "Run in the tray when you sign in.", _draft.General.StartWithWindows, v => _draft.General.StartWithWindows = v);
                Section(panel, "Output");
                CapturesFolder(panel);
                Toggle(panel, "Copy captures to clipboard", "Ready to paste after capturing.", _draft.General.CopyToClipboard, v => _draft.General.CopyToClipboard = v);
                Toggle(panel, "Save captures to files", "Save automatically to your folder.", _draft.General.SaveToFile, v => _draft.General.SaveToFile = v);
                Section(panel, "History and editor");
                Number(panel, "Recent captures", "Saved files are never deleted when history is trimmed.", _draft.General.HistorySize, 5, 500, v => _draft.General.HistorySize = v);
                Toggle(panel, "Keep saved captures searchable", "Keep saved files in history, without a time limit.",
                    _draft.Library.KeepSavedCaptures, value => _draft.Library.KeepSavedCaptures = value);
                Note(panel, "Click Hotshot in the taskbar tray to browse and edit.");
                SettingsSynchronization(panel);
                break;
            case SettingsPage.Capture:
                Section(panel, "Text");
                Toggle(panel, "Read text in screenshots",
                    "Works on this device. Nothing is sent.",
                    _draft.General.RecognizeScreenshotText, value => _draft.General.RecognizeScreenshotText = value);
                Number(panel, "Screenshot delay", "Wait this many seconds. 0 means no delay.",
                    _draft.Capture.DelaySeconds, 0, 30, value => _draft.Capture.DelaySeconds = value);
                Section(panel, "Copilot");
                Toggle(panel, "Copilot descriptions",
                    "Sends screenshots to GitHub Copilot. Uses your Copilot plan.",
                    _draft.General.DescribeScreenshots, value => _draft.General.DescribeScreenshots = value);
                Toggle(panel, "Review before sending", "Choose which screenshots to send after hiding anything private.",
                    _draft.General.ReviewBeforeAiUpload, value => _draft.General.ReviewBeforeAiUpload = value);
                TextInput(panel, "Exclude apps", string.Join(", ", _draft.General.AiExcludedApps),
                    value => _draft.General.AiExcludedApps = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                Note(panel, "Whole-screen captures may still include excluded apps. Review before sending.");
                _aiStatus = UiStyles.Description(string.Empty);
                RefreshAiStatus();
                panel.Children.Add(_aiStatus);
                RenderCopilotAccount(panel);
                RenderAiComponent(panel, _app.CopilotRuntime);
                var advanced = new StackPanel { Spacing = 8 };
                var runtime = new Button { Content = "Choose custom Copilot file" };
                runtime.Click += async (_, _) =>
                {
                    if (IsCopilotAccountBusy) { SetCopilotAccountMessage("Finish or cancel sign-in first."); return; }
                    var picker = new FileOpenPicker(AppWindow.Id) { Title = "Choose copilot-runtime.exe", FileTypeFilter = { ".exe" } };
                    var selected = await picker.PickSingleFileAsync();
                    if (selected is null) return;
                    _draft.General.CopilotRuntimePath = selected.Path;
                    Changed();
                };
                advanced.Children.Add(UiStyles.Description("For custom Copilot installations only."));
                advanced.Children.Add(runtime);
                var bundled = new Button { Content = "Use Hotshot's Copilot" };
                bundled.Click += (_, _) =>
                {
                    if (IsCopilotAccountBusy) { SetCopilotAccountMessage("Finish or cancel sign-in first."); return; }
                    _draft.General.CopilotRuntimePath = null; Changed();
                };
                advanced.Children.Add(bundled);
                panel.Children.Add(new Expander { Header = "Advanced", Content = advanced,
                    HorizontalAlignment = HorizontalAlignment.Stretch });
                Section(panel, "Selection");
                Toggle(panel, "Snap to windows", "Drag to select an area, or click a highlighted window.", _draft.Capture.SnapToWindows, v => _draft.Capture.SnapToWindows = v);
                Toggle(panel, "Show magnifier", "Inspect individual pixels while selecting a region.", _draft.Capture.ShowMagnifier, v => _draft.Capture.ShowMagnifier = v);
                Toggle(panel, "Show crosshair", "Align the selection precisely.", _draft.Capture.ShowCrosshair, v => _draft.Capture.ShowCrosshair = v);
                Toggle(panel, "Include cursor", "Include the mouse pointer in the saved screenshot.", _draft.Capture.IncludeCursor, v => _draft.Capture.IncludeCursor = v);
                Toggle(panel, "Capture covered windows", "Capture a window even when another window covers it.",
                    _draft.Capture.UnobscuredWindows, value => _draft.Capture.UnobscuredWindows = value);
                Note(panel, "Drag an area or click a window. Space captures the screen; Esc cancels.");
                break;
            case SettingsPage.Hotkeys:
                Section(panel, "Shortcuts");
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
                    UiStyles.Description("Already used by Windows? Change its setting or choose another shortcut."), windows);
                break;
            case SettingsPage.Naming:
                Section(panel, "File names");
                Pattern(panel, "Screenshots", _draft.Naming.ScreenshotPattern, CaptureKind.Region, v => _draft.Naming.ScreenshotPattern = v);
                Pattern(panel, "Recordings and GIFs", _draft.Naming.RecordingPattern, CaptureKind.Recording, v => _draft.Naming.RecordingPattern = v);
                Note(panel, @"Use \ for subfolders. Duplicate names get a number.");
                var tokens = new SettingsExpander
                {
                    Header = "Available fields", Description = "Add dates, app names and more.",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                var sample = NamingContext.Sample();
                foreach (var token in FileNameTemplate.Tokens)
                    tokens.Items.Add(new SettingsCard { Header = token.Token, Description = $"{token.Description}\nExample: {FileNameTemplate.Expand(token.Token, sample)}" });
                panel.Children.Add(tokens);
                break;
            case SettingsPage.Recording:
                Section(panel, "Video");
                Choice(panel, "Frame rate", "Frames recorded each second.", FrameRates, _draft.Recording.FramesPerSecond, v => _draft.Recording.FramesPerSecond = v);
                Choice(panel, "Quality", "Higher quality increases file size.", Enum.GetValues<RecordingQuality>(), _draft.Recording.Quality, v => _draft.Recording.Quality = v);
                Toggle(panel, "Include cursor", "Show the pointer in the recording.", _draft.Recording.IncludeCursor, v => _draft.Recording.IncludeCursor = v);
                Number(panel, "Countdown", "Seconds before recording begins.", _draft.Recording.CountdownSeconds, 0, 10, v => _draft.Recording.CountdownSeconds = v);
                Section(panel, "Recording timer");
                Choice(panel, "Timer size", "Only the time stays on screen.",
                    Enum.GetValues<RecordingTimerSize>(), _draft.Recording.TimerSize, value => _draft.Recording.TimerSize = value);
                Choice(panel, "Timer corner", "On the screen you're recording.",
                    Enum.GetValues<RecordingTimerPlacement>(), _draft.Recording.TimerPlacement,
                    value => _draft.Recording.TimerPlacement = value, RecordingTimerLayout.PlacementName);
                Section(panel, "Audio");
                Toggle(panel, "Record system audio", "Include audio played by Windows and apps.", _draft.Recording.CaptureSystemAudio, v => _draft.Recording.CaptureSystemAudio = v);
                Toggle(panel, "Record microphone", "Include your voice.", _draft.Recording.CaptureMicrophone, v => _draft.Recording.CaptureMicrophone = v);
                MicrophoneChoice(panel);
                Toggle(panel, "Also create a GIF", "Keep the video and make a GIF too.", _draft.Recording.AlsoCreateGif, v => _draft.Recording.AlsoCreateGif = v);
                Note(panel, "Choose an area on one screen. Turn off both audio options for silent video.");
                break;
            case SettingsPage.Gif:
                Section(panel, "Animation");
                Number(panel, "Frame rate", "Lower frame rates produce smaller files.", _draft.Gif.FramesPerSecond, 5, 30, v => _draft.Gif.FramesPerSecond = v);
                Number(panel, "Maximum width", "Width in pixels. 0 keeps the original size.", _draft.Gif.MaxWidth, 0, 3840, v => _draft.Gif.MaxWidth = v);
                Toggle(panel, "Smoother colors", "Reduce harsh color changes.", _draft.Gif.Dither, v => _draft.Gif.Dither = v);
                Toggle(panel, "Loop animation", "Play again automatically.", _draft.Gif.Loop, v => _draft.Gif.Loop = v);
                Note(panel, "Select a recording in history to make a GIF. GIFs have no sound.");
                break;
            case SettingsPage.Agents:
                Section(panel, "Permissions");
                Toggle(panel, "Allow assistant access",
                    "Let assistants take screenshots. Only enable for assistants you trust.",
                    _draft.Agents.Enabled, value => _draft.Agents.Enabled = value);
                Toggle(panel, "Allow saved screenshots",
                    "Let assistants read and search your saved screenshots.",
                    _draft.Agents.AllowHistoryAccess, value => _draft.Agents.AllowHistoryAccess = value);
                Toggle(panel, "Temporary access only", "Access expires when time runs out or Hotshot restarts.",
                    _draft.Agents.SessionAccessOnly, value => _draft.Agents.SessionAccessOnly = value);
                Number(panel, "Access duration", "Minutes before temporary access expires.", _draft.Agents.SessionMinutes, 5, 240,
                    value => _draft.Agents.SessionMinutes = value);
                Toggle(panel, "Ask each time", "Ask before taking or reading a screenshot.",
                    _draft.Agents.ApproveRequests, value => _draft.Agents.ApproveRequests = value);
                var grant = new Button { Content = "Allow temporary access" };
                grant.Click += async (_, _) =>
                {
                    await PersistAsync();
                    try { _app.AgentServer?.GrantSession(); }
                    catch (InvalidOperationException ex) { _error.Title = "Agent access"; _error.Message = ex.Message; _error.IsOpen = true; }
                };
                panel.Children.Add(grant);
                var revoke = new Button { Content = "End access" };
                revoke.Click += (_, _) => _app.AgentServer?.RevokeSession();
                panel.Children.Add(revoke);
                Toggle(panel, "Allow full-screen capture", "Unavailable when access is limited to specific apps.",
                    _draft.Agents.AllowMonitorCapture, value => _draft.Agents.AllowMonitorCapture = value);
                Toggle(panel, "Allow area capture", "Unavailable when access is limited to specific apps.",
                    _draft.Agents.AllowRegionCapture, value => _draft.Agents.AllowRegionCapture = value);
                TextInput(panel, "Allowed apps", string.Join(", ", _draft.Agents.AllowedApps),
                    value => _draft.Agents.AllowedApps = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                Note(panel, "Leave empty to allow all apps. Overlapping windows may still appear in screenshots.");
                Note(panel, "Assistants may send screenshots to their AI service.");
                _agentStatus = UiStyles.Description(string.Empty);
                RefreshAgentStatus();
                panel.Children.Add(_agentStatus);
                var test = new Button { Content = "Test connection" };
                test.Click += async (_, _) => await TestAgentConnectionAsync(test);
                panel.Children.Add(test);
                var activity = new Button { Content = "View activity" };
                activity.Click += async (_, _) =>
                {
                    var lines = _app.AgentServer?.Activity.Reverse().Select(entry =>
                        $"{entry.Timestamp:t}  {entry.Operation}  {(entry.Success ? "Completed" : entry.ErrorCode ?? "Failed")}") ?? [];
                    await new ContentDialog { Title = "Assistant activity", Content = new ScrollViewer
                        { Content = new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap }, MaxHeight = 400 },
                        CloseButtonText = "Close", XamlRoot = _navigation.XamlRoot }.ShowAsync();
                };
                panel.Children.Add(activity);
                AgentConnections(panel);
                break;
            case SettingsPage.About:
                panel.Children.Add(new Image { Source = Logo(), Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 24, 0, 12) });
                Section(panel, "Hotshot");
                Note(panel, $"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)}");
                Note(panel, "Your captures stay on this device unless you choose to share them.");
                Section(panel, "Updates");
                _checkUpdates = new Button { Content = "Check now" };
                _checkUpdates.Click += async (_, _) => { if (_app.Updates is { } updates) await updates.CheckAsync(); };
                _updateStatus = UiStyles.Description(string.Empty);
                Card(panel, "Automatic updates", _updateStatus, _checkUpdates);
                RefreshUpdateStatus();
                Note(panel, "Updates install when you're not capturing or editing.");
                var releases = new Button { Content = "What's new" };
                releases.Click += (_, _) => CaptureActions.OpenPath(Hotshot.Updates.AutomaticUpdates.RepositoryUrl + "/releases/latest");
                panel.Children.Add(releases);
                var guide = new Button { Content = "Quick start guide" };
                guide.Click += async (_, _) => await ShowQuickStartAsync();
                panel.Children.Add(guide);
                var logs = new Button { Content = "Open log" };
                logs.Click += (_, _) => CaptureActions.OpenPath(Log.FilePath);
                Card(panel, "Troubleshooting", UiStyles.Description("Details to help solve a problem."), logs);
                var diagnostic = new Button { Content = "Show details" };
                diagnostic.Click += async (_, _) =>
                {
                    diagnostic.IsEnabled = false;
                    try
                    {
                        var summary = await Task.Run(() => Hotshot.Diagnostics.AppDiagnostics.Read(_app));
                        var report = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = summary };
                        AutomationProperties.SetAutomationId(report, "DiagnosticsReport");
                        await new ContentDialog { Title = "Hotshot diagnostics", Content = new ScrollViewer
                            { Content = report, MaxHeight = 500 },
                            CloseButtonText = "Close", XamlRoot = _navigation.XamlRoot }.ShowAsync();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        Log.Error("Could not read diagnostics", ex);
                        _error.Title = "Diagnostics could not be read";
                        _error.Message = ex.Message;
                        _error.IsOpen = true;
                    }
                    finally { diagnostic.IsEnabled = true; }
                };
                panel.Children.Add(diagnostic);
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
            _error.Title = "Check these settings";
            _error.Message = string.Join(Environment.NewLine, errors);
            _error.IsOpen = true;
            _status.Text = "Not saved yet.";
            return;
        }
        var snapshot = _draft.Clone();
        if (await _app.ApplySettingsAsync(snapshot, _draftBaseline))
        {
            _savedVersion = version;
            if (version == _editVersion)
            {
                _draft = _app.Settings.Clone();
                _draftBaseline = _app.Settings.Clone();
            }
            if (_closed) return;
            _error.IsOpen = false;
            if (version == _editVersion) _status.Text = "All changes saved.";
        }
        else if (!_closed)
        {
            _error.Title = "Settings could not be saved";
            _error.Message = "Your previous settings are safe. Check the log for details.";
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
            SecondaryButtonText = "Clear", CloseButtonText = "Cancel", XamlRoot = _navigation.XamlRoot,
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
        _updateStatus.Text = _app.Updates?.Status ?? "Starting...";
        if (_checkUpdates is not null)
            _checkUpdates.IsEnabled = _app.Updates is { IsManaged: true, IsChecking: false };
    }

    private void RefreshAgentStatus()
    {
        if (!_closed)
        {
            var clients = _app.AgentServer?.Clients ?? [];
            _agentStatus.Text = (_app.AgentServer?.Status ?? "Starting...") + "\n" +
                string.Join("\n", clients.Select(client => $"{client.ProcessName}: {(client.Connected ? "connected" : "last seen")} {client.LastSeen:t}"));
        }
    }

    private void RefreshAiStatus() => _app.Post(() =>
    {
        if (!_closed)
        {
            _aiStatus.Text = _app.Descriptions?.Status ?? string.Empty;
            _aiStatus.Visibility = _draft.General.DescribeScreenshots || _app.Descriptions?.IsBusy == true
                ? Visibility.Visible : Visibility.Collapsed;
            RefreshCopilotAccountActions();
            RefreshAiComponentActions();
        }
    });

    private void MicrophoneChoice(StackPanel panel)
    {
        var devices = new[] { new AudioDeviceInfo("", "Windows default", true) }.Concat(AudioDevices.GetMicrophones(message =>
        {
            Log.Warn(message);
            _error.Title = "Audio devices unavailable";
            _error.Message = message;
            _error.IsOpen = true;
        })).ToArray();
        var input = new ComboBox { ItemsSource = devices, DisplayMemberPath = "Name", MinWidth = 180, MaxWidth = 300 };
        input.SelectedItem = devices.FirstOrDefault(device => device.Id == (_draft.Recording.MicrophoneDeviceId ?? "")) ?? devices[0];
        AutomationProperties.SetName(input, "Microphone device");
        input.SelectionChanged += (_, _) =>
        {
            if (input.SelectedItem is not AudioDeviceInfo device || _rendering) return;
            _draft.Recording.MicrophoneDeviceId = device.Id.Length == 0 ? null : device.Id;
            Changed();
        };
        Card(panel, "Microphone device", UiStyles.Description("Choose which microphone to use."), input);
    }

    private void TextInput(StackPanel panel, string title, string value, Action<string> update)
    {
        var input = new TextBox { Text = value, MinWidth = 180, MaxWidth = 320 };
        AutomationProperties.SetName(input, title);
        input.TextChanged += (_, _) => { if (!_rendering) { update(input.Text); Changed(); } };
        Card(panel, title, UiStyles.Description("App filenames, separated by commas. Example: app.exe"), input);
    }

    private void AgentConnections(StackPanel panel)
    {
        Section(panel, "Connections");
        var names = new[] { "GitHub Copilot CLI", "Claude Code", "VS Code", "Cursor", "Codex" };
        var choices = new ComboBox { ItemsSource = names, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choices, "Coding agent");
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120 };
        AutomationProperties.SetAutomationId(preview, "AgentMcpConfiguration");
        var location = UiStyles.Description(string.Empty);
        var executable = Path.Combine(AppContext.BaseDirectory, "Mcp", "Hotshot.Mcp.exe");
        void Update()
        {
            var agent = (CodingAgent)Math.Max(0, choices.SelectedIndex);
            preview.Text = AgentConfiguration.Create(agent, executable);
            location.Text = AgentConfiguration.Location(agent);
        }
        choices.SelectionChanged += (_, _) => Update();
        Update();
        var copy = new Button { Content = "Copy connection settings" };
        copy.Click += async (_, _) =>
        {
            if (!File.Exists(executable))
            {
                _error.Title = "Connection unavailable";
                _error.Message = "Reinstall Hotshot to restore assistant connections.";
                _error.IsOpen = true;
                return;
            }
            if (!await ClipboardService.SetTextAsync(_app.MessageWindowHandle, preview.Text))
            {
                _error.Title = "Could not copy settings";
                _error.Message = "Another application is using the clipboard.";
                _error.IsOpen = true;
            }
        };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(choices);
        body.Children.Add(copy);
        var manual = new StackPanel { Spacing = 8 };
        manual.Children.Add(location);
        manual.Children.Add(preview);
        body.Children.Add(new Expander { Header = "Manual setup", Content = manual,
            HorizontalAlignment = HorizontalAlignment.Stretch });
        Card(panel, "Connect an assistant", body);
        var plugins = new StackPanel { Spacing = 8 };
        plugins.Children.Add(UiStyles.Description("Add Hotshot tools to your assistant."));
        var commands = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Text = "Copilot CLI:\ncopilot plugin marketplace add sebastienlevert/hotshot\ncopilot plugin install hotshot@hotshot\n\nClaude Code:\n/plugin marketplace add sebastienlevert/hotshot\n/plugin install hotshot@hotshot",
        };
        plugins.Children.Add(new Expander { Header = "Install commands", Content = commands,
            HorizontalAlignment = HorizontalAlignment.Stretch });
        var docs = new Button { Content = "Connection guide" };
        docs.Click += (_, _) => CaptureActions.OpenPath("https://github.com/sebastienlevert/hotshot/blob/main/agents/README.md");
        plugins.Children.Add(docs);
        Card(panel, "Extra tools", plugins);
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
        body.Children.Add(UiStyles.Description("Where screenshots and recordings are saved."));
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
        UiStyles.LiveStatus(preview, $"Naming{kind}Preview");
        AutomationProperties.GetDescribedBy(input).Add(preview);
        void Update(string value)
        {
            changed(value);
            var invalid = FileNameTemplate.FindInvalidTokens(value);
            preview.Text = invalid.Count == 0 ? "Example: " + FileNameTemplate.BuildRelativePath(value, sample)
                : "Unknown fields: " + string.Join(", ", invalid);
        }
        input.TextChanged += (_, _) => { Update(input.Text); Changed(); };
        var tokens = new ComboBox
        {
            PlaceholderText = "Add a field",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(tokens, $"Add a field to {title}");
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
        body.Children.Add(UiStyles.Description("Type a name and add fields such as the date."));
        body.Children.Add(input);
        body.Children.Add(tokens);
        body.Children.Add(preview);
        Card(panel, title, body);
        Update(current);
    }

    private void Number(StackPanel panel, string title, string description, int current, int min, int max, Action<int> changed)
    {
        var input = new NumberBox { Value = current, Minimum = min, Maximum = max, MinWidth = 112, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        AutomationProperties.SetName(input, title);
        input.ValueChanged += (_, args) => { if (double.IsFinite(args.NewValue)) { changed((int)args.NewValue); Changed(); } };
        Card(panel, title, UiStyles.Description(description), input);
    }

    private void Choice<T>(StackPanel panel, string title, string description, T[] choices, T current, Action<T> changed,
        Func<T, string>? label = null)
    {
        var input = new ComboBox { MinWidth = 112 };
        foreach (var choice in choices)
            input.Items.Add(new ComboBoxItem { Content = label is null ? choice?.ToString() : label(choice), Tag = choice });
        input.SelectedIndex = Array.IndexOf(choices, current);
        AutomationProperties.SetName(input, title);
        input.SelectionChanged += (_, _) =>
        {
            if (input.SelectedItem is ComboBoxItem { Tag: T value }) { changed(value); Changed(); }
        };
        Card(panel, title, UiStyles.Description(description), input);
    }

    private void Card(StackPanel panel, string title, UIElement description, FrameworkElement? control = null)
    {
        var card = new SettingsCard
        {
            Header = title, Description = description, Content = control,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = control is null ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
        };
        if (control is not null && description is TextBlock)
            AutomationProperties.GetDescribedBy(control).Add(description);
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
