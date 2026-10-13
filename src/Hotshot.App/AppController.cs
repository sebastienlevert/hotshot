using Hotshot.CaptureFlow;
using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Core.Hotkeys;
using Hotshot.Core.Descriptions;
using Hotshot.Descriptions;
using Hotshot.Core.Setup;
using Hotshot.Agents;
using Hotshot.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hotshot;

/// <summary>Owns the resident app: tray icon, global hotkeys, capture flows and windows.</summary>
internal sealed partial class AppController : IDisposable
{
    private readonly AppServices _app;
    private readonly MessageWindow _window;
    private readonly TrayIcon _tray;
    private readonly HotkeyManager _hotkeys;
    private readonly OutputPipeline _pipeline;
    private readonly CaptureCoordinator _capture;
    private readonly CaptureActions _actions;
    private readonly ScreenshotDescriptions _descriptions;
    private readonly LocalTextIndex _textIndex;
    private readonly AgentCaptureServer _agentServer;
    private readonly CopilotRuntimeManager _copilotRuntime;
    private Action? _balloonAction;
    private bool _exiting;
    private bool _disposed;

    public AppController(DispatcherQueue dispatcher)
    {
        _app = new AppServices(AppPaths.Default, dispatcher);
        _window = new MessageWindow();
        _app.MessageWindowHandle = _window.Handle;

        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        _tray = new TrayIcon(_window, Path.Combine(assets, "Hotshot.ico"), Path.Combine(assets, "HotshotRecording.ico"));
        _hotkeys = new HotkeyManager(_window);
        _copilotRuntime = new CopilotRuntimeManager(_app.Paths.DataDirectory,
            () => _app.Settings.General.CopilotRuntimePath,
            () => !_exiting && _app.Descriptions?.IsBusy != true && _settingsWindow?.IsCopilotAccountBusy != true);
        _app.CopilotRuntime = _copilotRuntime;
        _descriptions = new ScreenshotDescriptions(_app.History,
            () => new CopilotDescriptionProvider(Path.Combine(_app.Paths.DataDirectory, "copilot-work"), _copilotRuntime.ResolveRuntime),
            _app.Settings.General.DescribeScreenshots);
        _app.Descriptions = _descriptions;
        _descriptions.ApplyPolicy(_app.Settings.General);
        _textIndex = new LocalTextIndex(_app);
        _app.TextIndex = _textIndex;
        _textIndex.Failed += message => Notify("Local text recognition", message, isError: true);
        _descriptions.Failed += message => _app.Post(() =>
        {
            Log.Warn(message);
            Notify("Screenshot description", message, isError: true);
        });
        _pipeline = new OutputPipeline(_app);
        _app.Output = _pipeline;
        _capture = new CaptureCoordinator(_app, _pipeline,
            () => _settingsWindow is { } settings ? WinRT.Interop.WindowNative.GetWindowHandle(settings) : 0);
        _actions = new CaptureActions(_app);
        _agentServer = new AgentCaptureServer(_app, _capture,
            () => _exiting || IsRecording || _startTask is not null || _stopTask is not null,
            () => _settingsWindow is { } settings ? WinRT.Interop.WindowNative.GetWindowHandle(settings) : 0);
        _app.AgentServer = _agentServer;
        _app.CanChangeSharedSettings = () => !_exiting && !_capture.IsBusy && !IsRecording && _startTask is null && _stopTask is null;
        _app.CanReloadSharedSettings = () => _app.CanChangeSharedSettings() &&
            !(_settingsWindow?.HasPendingChanges ?? false) && (_settingsWindow is null ||
                Hotshot.Interop.Win32.GetForegroundWindow() != WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow));
        _agentServer.ApprovalHandler = async (request, client, token) =>
        {
            var window = new Hotshot.Views.AgentApprovalWindow(request, client, token);
            window.Activate();
            return await window.Decision;
        };

        _hotkeys.Pressed += action => _app.Post(() => OnHotkey(action));
        _window.ArgumentsReceived += args => Execute(CommandLine.Parse(args).Command, secondInstance: true);
        _tray.LeftClick += () => _app.Post(OnTrayLeftClick);
        _tray.ContextMenuRequested += (x, y) => _app.Post(() => ShowTrayMenu(x, y));
        _tray.BalloonClicked += () =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };
        _pipeline.Failed += message => Notify("Hotshot", message, isError: true);
        _app.Failed += message => Notify("Hotshot", message, isError: true);
        _capture.Failed += message => Notify("Hotshot", message, isError: true);
        _actions.Failed += message => _app.Post(() => Notify("Hotshot", message, isError: true));
        _app.SettingsChanged += OnSettingsChanged;

        InitializeViews();
        InitializeRecording();
        InitializeUpdates();
    }

    public static AppController? Current { get; private set; }

    internal AppServices Services => _app;

    internal HotkeyManager Hotkeys => _hotkeys;

    internal CaptureActions Actions => _actions;

    public void Start(CommandLine commandLine)
    {
        Current = this;
        _tray.Add();
        _hotkeys.Apply(_app.Settings.Hotkeys);
        SyncStartupRegistration();

        var firstRun = !_app.Settings.General.FirstRunCompleted;
        Log.Info($"Started (first run: {firstRun}, args: {commandLine})");

        if (commandLine.Command is not AppCommand.None)
        {
            Execute(commandLine.Command, secondInstance: false);
        }
        else if (firstRun || (!commandLine.Background && PrintScreenNeedsAttention()))
        {
            ShowSettings(firstRun ? SettingsPage.General : SettingsPage.Hotkeys);
        }
        else if (!commandLine.Background)
        {
            Notify("Hotshot is running", $"Press {DisplayHotkey(HotkeyAction.RegionScreenshot)} to capture. Hotshot lives in the tray.");
        }

        ReportHotkeyConflicts();
        if (firstRun && _settingsWindow is { } firstSettings) Launch(firstSettings.ShowQuickStartAsync, "Guided setup failed");
        _updates.Start();
        _agentServer.Start();
        Program.MarkReady();
        Log.Info($"Resident ready in {Program.StartupMilliseconds} ms.");
        if (File.Exists(RecordingJournal))
            Notify("Recording recovery", "An interrupted recording needs review. Use Recording tools in the workspace to recover a playable file or dismiss its notice.", isError: true);
        foreach (var item in _app.History.Items.Where(item => item.DescriptionPending && !item.IsVideo))
        {
            if (_app.Settings.General.DescribeScreenshots) Launch(() => _descriptions.EnqueueAsync(item), "Screenshot description failed");
            else Launch(() => Task.Run(() => _app.History.SetDescriptionPending(item.Id, false)), "Could not update pending description");
        }
    }

    private async Task MarkFirstRunCompletedAsync()
    {
        if (_app.Settings.General.FirstRunCompleted || _exiting) return;
        _app.Settings.General.FirstRunCompleted = true;
        if (!await _app.SaveAsync()) _app.Settings.General.FirstRunCompleted = false;
    }

    public void Execute(AppCommand command, bool secondInstance)
    {
        if (_exiting) return;
        Log.Info($"Command: {command}{(secondInstance ? " (forwarded)" : string.Empty)}");
        switch (command)
        {
            case AppCommand.Region: Launch(_capture.CaptureRegionAsync, "Region capture failed"); break;
            case AppCommand.Monitor: Launch(_capture.CaptureMonitorAsync, "Monitor capture failed"); break;
            case AppCommand.AllMonitors: Launch(_capture.CaptureAllMonitorsAsync, "Desktop capture failed"); break;
            case AppCommand.Scrolling: Launch(_capture.CaptureScrollingAsync, "Scrolling capture failed"); break;
            case AppCommand.Record: Launch(() => ToggleRecordingAsync(asGif: false), "Recording failed"); break;
            case AppCommand.RecordGif: Launch(() => ToggleRecordingAsync(asGif: true), "GIF recording failed"); break;
            case AppCommand.Settings: ShowSettings(); break;
            case AppCommand.History: ShowHistory(); break;
            case AppCommand.Exit: Exit(); break;
            case AppCommand.Activate or AppCommand.None when secondInstance: ShowSettings(); break;
        }
    }

    public void Exit() => Launch(ExitAsync, "Hotshot could not shut down");

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        if (_historyWindow is not null && !await _historyWindow.PrepareToCloseAsync()) return;
        if (_settingsWindow is not null) await _settingsWindow.SavePendingAsync();
        _exiting = true;
        if (_settingsWindow is not null) await _settingsWindow.CancelCopilotSignInAsync();
        _settingsWindow?.CloseAiComponent();
        _app.StopSettingsSync();
        Log.Info("Exiting");
        try
        {
            await _agentServer.DisposeAsync();
            await _capture.ShutdownAsync();
            await ShutdownRecordingAsync();
            await _descriptions.DisposeAsync();
            await _textIndex.DisposeAsync();
            await _app.CheckpointHistoryAsync();
            CloseViews();
            await _app.SaveAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown cleanup failed", ex);
        }

        Dispose();
        Application.Current.Exit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        _copilotRuntime.Dispose();
        _updates.Dispose();
        _agentServer.Dispose();
        _hotkeys.Dispose();
        _tray.Dispose();
        _window.Dispose();
        _capture.Dispose();
        _app.Dispose();
    }

    public void Notify(string title, string text, bool isError = false, Action? onClick = null)
    {
        _balloonAction = onClick;
        _tray.ShowBalloon(title, text, isError);
    }

    // ------------------------------------------------------------------ input

    private void OnHotkey(HotkeyAction action)
    {
        if (_exiting) return;
        Log.Info($"Hotkey: {action}");
        switch (action)
        {
            case HotkeyAction.RegionScreenshot: Launch(_capture.CaptureRegionAsync, "Region capture failed"); break;
            case HotkeyAction.MonitorScreenshot: Launch(_capture.CaptureMonitorAsync, "Monitor capture failed"); break;
            case HotkeyAction.AllMonitorsScreenshot: Launch(_capture.CaptureAllMonitorsAsync, "Desktop capture failed"); break;
            case HotkeyAction.RepeatLastRegion: Launch(_capture.RepeatLastRegionAsync, "Repeat capture failed"); break;
            case HotkeyAction.ToggleRecording: Launch(() => ToggleRecordingAsync(asGif: false), "Recording failed"); break;
            case HotkeyAction.RecordGif: Launch(() => ToggleRecordingAsync(asGif: true), "GIF recording failed"); break;
            case HotkeyAction.OpenHistory: ShowHistory(); break;
            case HotkeyAction.OpenLastInEditor:
                if (_app.History.Latest(i => !i.IsVideo) is { } latest)
                {
                    Launch(() => _actions.EditAsync(latest), "Could not open the editor");
                }

                break;
        }
    }

    private void OnTrayLeftClick() => ShowHistory();

    private void ShowTrayMenu(int x, int y)
    {
        var recent = _app.History.Items.Take(8)
            .Select(item => new TrayMenuItem(DescribeForMenu(item), () =>
            {
                if (item.IsVideo) _actions.Open(item); else Launch(() => _actions.EditAsync(item), "Could not open the editor");
            }))
            .ToList();

        var recording = IsRecording;
        var items = new List<TrayMenuItem>
        {
            new($"Capture region\t{DisplayHotkey(HotkeyAction.RegionScreenshot)}", () => Delayed(_capture.CaptureRegionAsync)),
            new($"Capture monitor\t{DisplayHotkey(HotkeyAction.MonitorScreenshot)}", () => Delayed(_capture.CaptureMonitorAsync)),
            new($"Capture all monitors\t{DisplayHotkey(HotkeyAction.AllMonitorsScreenshot)}", () => Delayed(_capture.CaptureAllMonitorsAsync)),
            new("Scrolling capture", () => Delayed(_capture.CaptureScrollingAsync)),
            new($"Repeat last region\t{DisplayHotkey(HotkeyAction.RepeatLastRegion)}", () => Delayed(_capture.RepeatLastRegionAsync)),
            TrayMenuItem.Separator,
            recording
                ? new("Stop recording", () => Launch(StopRecordingAsync, "Could not stop recording"))
                : new($"Record screen\t{DisplayHotkey(HotkeyAction.ToggleRecording)}", () => Delayed(() => ToggleRecordingAsync(false)), RecordingAvailable),
            new($"Record GIF\t{DisplayHotkey(HotkeyAction.RecordGif)}", () => Delayed(() => ToggleRecordingAsync(true)), RecordingAvailable && !recording),
            TrayMenuItem.Separator,
            new("Recent", Enabled: recent.Count > 0, Children: recent.Count > 0 ? recent : null),
            new("Editor and history", ShowHistory),
            new("Open captures folder", _actions.OpenSaveFolder),
            TrayMenuItem.Separator,
            new("Pause hotkeys", () => _hotkeys.SetPaused(!_hotkeys.IsPaused), Checked: _hotkeys.IsPaused),
            new("Settings…", () => ShowSettings()),
            new("Exit Hotshot", Exit),
        };

        _tray.ShowMenu(items, x, y);
    }

    /// <summary>Lets the tray menu fade out before grabbing the screen.</summary>
    private void Launch(Func<Task> action, string description) => _ = _app.RunObservedAsync(action, description);

    private void Delayed(Func<Task> action) => Launch(() => DelayedAsync(action), "Hotshot action failed");

    private static async Task DelayedAsync(Func<Task> action)
    {
        await Task.Delay(250);
        await action();
    }

    private string DisplayHotkey(HotkeyAction action) =>
        Hotkey.TryParse(_app.Settings.Hotkeys.Get(action), out var hotkey) && !hotkey.IsEmpty
            ? hotkey.ToDisplayString().Replace(" + ", "+", StringComparison.Ordinal)
            : string.Empty;

    private static string DescribeForMenu(HistoryItem item)
    {
        var size = item.Width > 0 ? $" {item.Width}×{item.Height}" : string.Empty;
        var duration = item.DurationSeconds is { } seconds ? $" {TimeSpan.FromSeconds(seconds):m\\:ss}" : string.Empty;
        return $"{item.Kind.DisplayName()}{size}{duration}  ·  {item.CreatedAt.LocalDateTime:t}";
    }

    // --------------------------------------------------------------- settings

    private void OnSettingsChanged()
    {
        _hotkeys.Apply(_app.Settings.Hotkeys);
        SyncStartupRegistration();
        ApplyViewTheme();
        _descriptions.SetEnabled(_app.Settings.General.DescribeScreenshots);
        _descriptions.ApplyPolicy(_app.Settings.General);
        _agentServer.RefreshStatus();
    }

    private void SyncStartupRegistration()
    {
        var wanted = _app.Settings.General.StartWithWindows;
        if (StartupManager.IsEnabled() != wanted)
        {
            StartupManager.Apply(wanted);
        }
    }

    internal bool PrintScreenNeedsAttention() =>
        UsesPrintScreen() && SnippingToolKey.IsPrintScreenTakenByWindows();

    private bool UsesPrintScreen() =>
        _app.Settings.Hotkeys.GetBindings().Any(b => b.Hotkey.Key == 0x2C);

    private void ReportHotkeyConflicts()
    {
        var conflicts = _hotkeys.Status.Values.Where(s => s.State == HotkeyState.Conflict).ToList();
        if (conflicts.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", conflicts.Select(c => c.Hotkey.ToDisplayString()));
        Notify("Some shortcuts are taken", $"{names} is used by another app (ShareX, Snagit…). Click to change it.",
            isError: true, onClick: () => ShowSettings(SettingsPage.Hotkeys));
    }
}
