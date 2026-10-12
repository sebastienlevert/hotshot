using System.Diagnostics;
using System.Text.Json;
using Hotshot.Core.Agents;
using Hotshot.Core.Hotkeys;
using Hotshot.Core.Setup;
using Hotshot.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Hotshot.Views;

internal sealed partial class SettingsWindow
{
    private Task? _quickStartTask;
    private bool _quickStartActive;

    public GuidedSetupSession? QuickStartState { get; private set; }
    public SetupOutcome QuickStartOutcome => QuickStartState?.Outcome ?? SetupOutcome.NotStarted;
    public bool IsQuickStartActive => _quickStartActive;
    public bool IsQuickStartBusy { get; private set; }
    public string? QuickStartLastError { get; private set; }
    public event Action? QuickStartStateChanged;

    private async Task TestAgentConnectionAsync(Button button)
    {
        button.IsEnabled = false;
        try
        {
            var bitmap = await LoadGeneratedConnectionImageAsync(CancellationToken.None);
            var body = new StackPanel { Spacing = 12 };
            var preview = new Image { Source = bitmap, Width = 256, Height = 128 };
            AutomationProperties.SetAutomationId(preview, "AgentSyntheticConnectionImage");
            body.Children.Add(preview);
            body.Children.Add(UiStyles.Description("The installed stdio companion connected to this native app and returned a generated image. No screen capture, historical files, settings change or model request occurred."));
            await new ContentDialog { Title = "Connection test passed", Content = body, CloseButtonText = "Close", XamlRoot = _navigation.XamlRoot }.ShowAsync();
        }
        catch (Exception ex) when (IsSetupOperationError(ex))
        {
            Log.Error("Agent connection test failed", ex);
            _error.Title = "Connection test failed"; _error.Message = ex.Message; _error.IsOpen = true;
        }
        finally { button.IsEnabled = true; }
    }

    public Task ShowQuickStartAsync() => _quickStartTask is { IsCompleted: false }
        ? _quickStartTask : _quickStartTask = RunQuickStartAsync();

    private async Task RunQuickStartAsync()
    {
        if (_closed) return;
        _quickStartActive = true;
        QuickStartLastError = null;
        try { await RunGuidedSetupAsync(); }
        catch (OperationCanceledException) when (_closed) { QuickStartState?.Cancel(); }
        catch (Exception ex) when (IsSetupOperationError(ex))
        {
            Log.Error("Setup could not open", ex);
            QuickStartLastError = ex.Message;
            QuickStartState?.Cancel();
            _error.Title = "Setup could not open";
            _error.Message = ex.Message;
            _error.IsOpen = true;
        }
        finally
        {
            _quickStartActive = false;
            IsQuickStartBusy = false;
            QuickStartStateChanged?.Invoke();
        }
    }

    private async Task RunGuidedSetupAsync()
    {
        if (_closed) return;
        QuickStartState = new GuidedSetupSession(_draft);
        QuickStartStateChanged?.Invoke();
        if (_navigation.XamlRoot is null)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? loaded = null;
            loaded = (_, _) => { _navigation.Loaded -= loaded; ready.TrySetResult(); };
            void ClosedBeforeLoad(object sender, WindowEventArgs args) => ready.TrySetResult();
            _navigation.Loaded += loaded;
            Closed += ClosedBeforeLoad;
            try { await ready.Task; }
            finally { _navigation.Loaded -= loaded; Closed -= ClosedBeforeLoad; }
        }
        if (_closed || _navigation.XamlRoot is null)
        {
            QuickStartState.Cancel();
            QuickStartStateChanged?.Invoke();
            return;
        }
        _saveTimer.Stop();
        await PersistAsync();
        if (_closed || _navigation.XamlRoot is null)
        {
            QuickStartState.Cancel();
            QuickStartStateChanged?.Invoke();
            return;
        }
        // The public pending flag may include this active setup; only pre-existing edits block entry.
        if (_editVersion != _savedVersion || _closing || _pickingFolder)
        {
            _error.Title = "Save your settings before setup";
            if (string.IsNullOrWhiteSpace(_error.Message))
                _error.Message = "Resolve unapplied settings changes, then reopen setup from About Hotshot.";
            _error.IsOpen = true;
            QuickStartLastError = _error.Message;
            QuickStartState.Cancel();
            QuickStartStateChanged?.Invoke();
            return;
        }

        var session = QuickStartState = new GuidedSetupSession(_app.Settings);
        using var stop = new CancellationTokenSource();
        var busy = false;
        SettingsPage? destination = null;
        var heading = new TextBlock { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        UiStyles.LiveStatus(heading, "QuickStartStep");
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        var progress = new ProgressBar { Minimum = 0, Maximum = GuidedSetupSession.StepCount, Height = 4 };
        AutomationProperties.SetAutomationId(progress, "QuickStartProgress");
        var description = QuickStartText("", "QuickStartDescription");
        var validation = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        AutomationProperties.SetAutomationId(validation, "QuickStartValidation");
        var stepBody = new StackPanel { Spacing = 12 };
        AutomationProperties.SetAutomationId(stepBody, "QuickStartStepContent");
        var scroll = new ScrollViewer
        {
            Content = stepBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Clamp(_navigation.XamlRoot.Size.Height - 330, 160, 420),
        };
        var back = QuickStartButton("Back", "QuickStartBack");
        var next = QuickStartButton("Next", "QuickStartNext");
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style accentStyle)
            next.Style = accentStyle;
        var skip = QuickStartButton("Skip", "QuickStartSkip");
        var cancel = QuickStartButton("Cancel", "QuickStartCancel");
        var footer = new Grid { ColumnSpacing = 8 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        footer.Children.Add(back);
        Grid.SetColumn(skip, 1); footer.Children.Add(skip);
        Grid.SetColumn(cancel, 3); footer.Children.Add(cancel);
        Grid.SetColumn(next, 4); footer.Children.Add(next);
        var body = new StackPanel { Spacing = 12, MaxWidth = 540 };
        body.Children.Add(progress);
        body.Children.Add(heading);
        body.Children.Add(description);
        body.Children.Add(validation);
        body.Children.Add(scroll);
        body.Children.Add(footer);
        var dialog = new ContentDialog
        {
            Title = "Set up Hotshot", Content = body, XamlRoot = _navigation.XamlRoot, RequestedTheme = _navigation.ActualTheme,
        };
        AutomationProperties.SetAutomationId(dialog, "QuickStartDialog");

        void ShowError(string message)
        {
            validation.Title = "This step needs attention";
            validation.Message = message;
            validation.IsOpen = true;
            QuickStartLastError = message;
            QuickStartStateChanged?.Invoke();
        }

        void SetBusy(bool value)
        {
            busy = value;
            IsQuickStartBusy = value;
            scroll.IsEnabled = !value;
            next.IsEnabled = !value;
            back.IsEnabled = !value && session.CanGoBack;
            skip.IsEnabled = !value && session.CanSkip;
            cancel.IsEnabled = !value;
        }

        void OpenPage(SettingsPage page)
        {
            if (busy) return;
            destination = page;
            session.OpenSettings();
            QuickStartStateChanged?.Invoke();
            dialog.Hide();
        }

        void ObserveShortcuts()
        {
            var observed = _hotkeys.Status.Values.ToArray();
            if (observed.Length == 0) return;
            session.RecordShortcuts(observed.Count(status => status.State == HotkeyState.Registered),
                observed.Count(status => status.State is HotkeyState.Conflict or HotkeyState.Duplicate or HotkeyState.Invalid or HotkeyState.Paused ||
                    status.Action == HotkeyAction.RegionScreenshot && status.State == HotkeyState.Unset), _hotkeys.IsPaused);
        }

        async Task CheckFolderAsync()
        {
            if (busy) return;
            var path = session.FolderPath;
            if (GuidedSetupSession.ValidateFolderPath(path) is { } invalid)
            {
                session.RecordFolderCheck(path, false, invalid);
                Render();
                ShowError(invalid);
                return;
            }
            SetBusy(true);
            description.Text = "Checking write access with a small, removable probe…";
            try
            {
                await CheckQuickStartFolderAsync(path, stop.Token);
                session.RecordFolderCheck(path, true, "Write access checked. The probe was removed.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) when (IsSetupOperationError(ex))
            {
                Log.Error("Setup folder check failed", ex);
                session.RecordFolderCheck(path, false, ex.Message);
            }
            finally { SetBusy(false); }
            if (session.Outcome != SetupOutcome.InProgress) return;
            Render();
            if (session.FolderCheck == SetupCheckResult.Failed) ShowError(session.FolderCheckMessage);
        }

        void Render()
        {
            if (session.Outcome != SetupOutcome.InProgress) return;
            validation.IsOpen = false;
            QuickStartLastError = null;
            heading.Text = $"{session.StepNumber} of {GuidedSetupSession.StepCount} · {QuickStartStepTitle(session.CurrentStep)}";
            progress.Value = session.StepNumber;
            AutomationProperties.SetName(progress, $"Setup step {session.StepNumber} of {GuidedSetupSession.StepCount}");
            next.Content = session.CurrentStep == SetupStep.Finish ? "Finish" : "Next";
            skip.Visibility = session.CanSkip ? Visibility.Visible : Visibility.Collapsed;
            stepBody.Children.Clear();
            description.Text = session.CurrentStep switch
            {
                SetupStep.Welcome => "Your capture utility, quietly ready in the Windows tray.",
                SetupStep.Folder => "Choose where saved captures belong on this device.",
                SetupStep.Shortcuts => "Check the shortcuts registered by this running app.",
                SetupStep.Output => "Keep your current preferences, or choose how new captures are delivered.",
                SetupStep.OneDrive => "See where portable preferences live. Accounts and device permissions stay yours.",
                SetupStep.Privacy => "Keep current choices, or turn optional access off. Setup never grants new permissions.",
                SetupStep.Connection => "An optional, local connection check using only a generated image.",
                _ => "Review the checklist. Finish saves only your explicit choices; Cancel discards them.",
            };
            switch (session.CurrentStep)
            {
                case SetupStep.Welcome:
                    stepBody.Children.Add(UiStyles.Description("Capture silently. Edit when you want."));
                    stepBody.Children.Add(UiStyles.Description("Screenshots copy and save without opening a preview or editor. Click Hotshot in the Windows tray to browse filenames, annotate or redact, and export a local flattened copy. The workspace does not automatically select the newest capture."));
                    stepBody.Children.Add(UiStyles.Description("Use your region shortcut to drag a region or click a window. Space captures the monitor; R + arrows + Enter selects a region with the keyboard. Escape cancels."));
                    stepBody.Children.Add(UiStyles.Description("In the editor, choose a tool and press Insert on the canvas to add an annotation or crop. Page Up/Down selects annotations; arrows move, Ctrl+arrows resize, and Shift uses 10 pixels. Alt+arrows pans, +/- zooms, Ctrl+1 shows 100%, Ctrl+0 fits, and Escape cancels or clears selection. Mouse wheel and Space-drag also work. Library actions offer reviewed cleanup, local backups, projects and relinking moved files."));
                    stepBody.Children.Add(UiStyles.Description("Nothing changes until Finish. Skip keeps a step's existing choices. Cancel or opening another settings page discards all pending setup choices. Reopen this flow from About Hotshot → Quick start guide."));
                    break;
                case SetupStep.Folder:
                    var path = new TextBox { Header = "Captures folder", Text = session.FolderPath, TextWrapping = TextWrapping.Wrap };
                    AutomationProperties.SetAutomationId(path, "QuickStartFolderPath");
                    var folderStatus = QuickStartText(session.FolderCheckMessage, "QuickStartFolderStatus");
                    path.TextChanged += (_, _) =>
                    {
                        session.FolderPath = path.Text;
                        folderStatus.Text = session.FolderCheckMessage;
                        validation.IsOpen = false;
                        QuickStartStateChanged?.Invoke();
                    };
                    stepBody.Children.Add(path);
                    var pick = QuickStartButton("Choose folder…", "QuickStartPickFolder");
                    pick.Click += async (_, _) =>
                    {
                        if (busy) return;
                        SetBusy(true);
                        _pickingFolder = true;
                        try
                        {
                            var selected = await new FolderPicker(AppWindow.Id) { Title = "Choose a captures folder for Hotshot" }.PickSingleFolderAsync();
                            if (selected is not null && session.Outcome == SetupOutcome.InProgress) session.FolderPath = selected.Path;
                        }
                        catch (Exception ex) when (IsSetupOperationError(ex)) { ShowError("The folder picker could not open: " + ex.Message); }
                        finally { _pickingFolder = false; SetBusy(false); }
                        if (session.Outcome == SetupOutcome.InProgress)
                        {
                            path.Text = session.FolderPath;
                            folderStatus.Text = session.FolderCheckMessage;
                        }
                    };
                    stepBody.Children.Add(pick);
                    var check = QuickStartButton("Check write access", "QuickStartCheckFolder");
                    check.Click += async (_, _) => await CheckFolderAsync();
                    stepBody.Children.Add(check);
                    stepBody.Children.Add(folderStatus);
                    stepBody.Children.Add(UiStyles.Description("The check writes and removes a tiny probe in the existing folder. It does not move captures or change your settings. If the default folder does not exist yet, choose an existing folder, or Skip to keep the current path without claiming it was verified."));
                    break;
                case SetupStep.Shortcuts:
                    ObserveShortcuts();
                    var shortcuts = new StackPanel { Spacing = 8 };
                    AutomationProperties.SetAutomationId(shortcuts, "QuickStartShortcutStatus");
                    foreach (var action in Enum.GetValues<HotkeyAction>())
                    {
                        var text = _hotkeys.Status.TryGetValue(action, out var status)
                            ? $"{action.DisplayName()}: {status.Hotkey.ToDisplayString()} — {status.Message}"
                            : $"{action.DisplayName()}: Registration has not been reported.";
                        shortcuts.Children.Add(UiStyles.Description(text));
                    }
                    stepBody.Children.Add(shortcuts);
                    if (session.HotkeysPaused) stepBody.Children.Add(UiStyles.Description("Hotkeys are paused in the tray. Setup will not unpause them."));
                    stepBody.Children.Add(UiStyles.Description("A conflict means Windows or another app already owns that shortcut. Keep it, choose an alternative in Keyboard shortcuts, or review Windows Settings → Accessibility → Keyboard for the Print Screen / Snipping Tool option. Setup never changes Windows shortcuts."));
                    var retry = QuickStartButton("Retry registration", "QuickStartRefreshShortcuts");
                    retry.Click += (_, _) => { _hotkeys.SetPaused(_hotkeys.IsPaused); Render(); };
                    stepBody.Children.Add(retry);
                    var openShortcuts = QuickStartButton("Open Keyboard shortcuts", "QuickStartOpenShortcuts");
                    openShortcuts.Click += (_, _) => OpenPage(SettingsPage.Hotkeys);
                    stepBody.Children.Add(openShortcuts);
                    AddHandoffNote();
                    break;
                case SetupStep.Output:
                    var outputNote = QuickStartText("", "QuickStartOutputNote");
                    void UpdateOutputNote() => outputNote.Text = !session.CopyToClipboard && !session.SaveToFile
                        ? "Both outputs are off. Captures remain available in temporary recent history; no new saved capture file or clipboard image is requested."
                        : "Captures remain silent. The tray opens the filename-based editor and history when you want them.";
                    stepBody.Children.Add(QuickStartToggle("Copy captures to clipboard", "QuickStartCopyToClipboard", session.CopyToClipboard,
                        value => { session.CopyToClipboard = value; UpdateOutputNote(); QuickStartStateChanged?.Invoke(); }));
                    stepBody.Children.Add(UiStyles.Description("Screenshot images go to the clipboard; recordings and GIFs use files."));
                    stepBody.Children.Add(QuickStartToggle("Save captures to files", "QuickStartSaveToFile", session.SaveToFile,
                        value => { session.SaveToFile = value; UpdateOutputNote(); QuickStartStateChanged?.Invoke(); }));
                    stepBody.Children.Add(UiStyles.Description("Keep capture files in the captures folder as well as recent history. Setup does not delete existing captures."));
                    UpdateOutputNote();
                    stepBody.Children.Add(outputNote);
                    break;
                case SetupStep.OneDrive:
                    stepBody.Children.Add(QuickStartText(_app.SettingsSyncStatus + "\nShared file: " + (_app.SharedSettingsFile ?? "None") +
                        "\nDevice-local cache: " + _app.Paths.SettingsFile, "QuickStartOneDriveStatus"));
                    stepBody.Children.Add(UiStyles.Description("Only the existing portable whitelist roams: theme, shortcuts, naming, capture/recording options, saved searches and export presets. Paths, layouts, microphone IDs, counters, AI upload/review choices and agent permissions remain on this device. Captures, originals, history and credentials do not roam."));
                    stepBody.Children.Add(UiStyles.Description("Next and Finish do not select an account, change the settings location, resolve a sync conflict or enable permissions. The native General page offers explicit, reviewed settings-location choices."));
                    var oneDrive = QuickStartButton("Open settings across devices", "QuickStartOpenOneDrive");
                    oneDrive.Click += (_, _) => OpenPage(SettingsPage.General);
                    stepBody.Children.Add(oneDrive);
                    AddHandoffNote();
                    break;
                case SetupStep.Privacy:
                    stepBody.Children.Add(QuickStartText(
                        $"Copilot descriptions: {OnOff(session.InitialAiDescriptions)}; review before upload: {OnOff(session.InitialAiReview)}.\n" +
                        $"Local text recognition: {OnOff(session.InitialTextRecognition)}.\n" +
                        $"Agent capture access: {OnOff(session.InitialAgentAccess)}; history permission: {OnOff(session.InitialAgentHistoryAccess)}.",
                        "QuickStartPrivacyStatus"));
                    stepBody.Children.Add(UiStyles.Description("Unchecked choices preserve the current device-local configuration. These controls can only turn features off. To opt in or review application restrictions, use the native Screenshots or Coding agents page separately."));
                    AddPrivacyChoice("Turn off automatic Copilot descriptions", "QuickStartTurnOffAiDescriptions", session.InitialAiDescriptions,
                        session.TurnOffAiDescriptions, value => session.TurnOffAiDescriptions = value);
                    AddPrivacyChoice("Turn off local screenshot text recognition", "QuickStartTurnOffTextRecognition", session.InitialTextRecognition,
                        session.TurnOffTextRecognition, value => session.TurnOffTextRecognition = value);
                    AddPrivacyChoice("Turn off agent capture access", "QuickStartTurnOffAgentAccess", session.InitialAgentAccess,
                        session.TurnOffAgentAccess, value => session.TurnOffAgentAccess = value);
                    AddPrivacyChoice("Turn off agent history access", "QuickStartTurnOffAgentHistoryAccess", session.InitialAgentHistoryAccess,
                        session.TurnOffAgentHistoryAccess, value => session.TurnOffAgentHistoryAccess = value);
                    stepBody.Children.Add(UiStyles.Description("Turning features off affects future requests only. It does not delete history, OCR text, descriptions or existing capture files. Review mode, exclusions, allowed applications, request approval and timed-session choices stay unchanged."));
                    var aiPrivacy = QuickStartButton("Review screenshot privacy", "QuickStartOpenAiPrivacy");
                    aiPrivacy.Click += (_, _) => OpenPage(SettingsPage.Capture);
                    stepBody.Children.Add(aiPrivacy);
                    var agentPrivacy = QuickStartButton("Review coding agent permissions", "QuickStartOpenAgentPrivacy");
                    agentPrivacy.Click += (_, _) => OpenPage(SettingsPage.Agents);
                    stepBody.Children.Add(agentPrivacy);
                    AddHandoffNote();
                    break;
                case SetupStep.Connection:
                    stepBody.Children.Add(UiStyles.Description("This test starts the installed local MCP companion, connects to the native app, and requests a generated 256 × 128 image. It does not capture the screen, read historical files, contact an AI provider, sign in or grant agent access. It is not an AI-service or account diagnostic."));
                    stepBody.Children.Add(QuickStartText(session.ConnectionCheckMessage, "QuickStartConnectionStatus"));
                    var test = QuickStartButton("Test with generated image", "QuickStartTestConnection");
                    test.IsEnabled = _app.AgentServer is not null && File.Exists(Path.Combine(AppContext.BaseDirectory, "Mcp", "Hotshot.Mcp.exe"));
                    if (!test.IsEnabled) stepBody.Children.Add(UiStyles.Description("The running native agent server and packaged MCP companion are required. You can Skip this optional test."));
                    test.Click += async (_, _) =>
                    {
                        if (busy) return;
                        SetBusy(true);
                        description.Text = "Connecting locally. No screen capture or model request…";
                        BitmapImage? bitmap = null;
                        try
                        {
                            bitmap = await LoadGeneratedConnectionImageAsync(stop.Token);
                            if (session.Outcome != SetupOutcome.InProgress) return;
                            session.RecordConnectionCheck(true, "Passed: the native app returned the generated image. No capture or model request occurred.");
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                        catch (Exception ex) when (IsSetupOperationError(ex))
                        {
                            Log.Error("Setup generated-image connection test failed", ex);
                            if (session.Outcome == SetupOutcome.InProgress) session.RecordConnectionCheck(false, ex.Message);
                        }
                        finally { SetBusy(false); }
                        if (session.Outcome != SetupOutcome.InProgress) return;
                        Render();
                        if (bitmap is not null)
                        {
                            var image = new Image { Source = bitmap, Width = 256, Height = 128 };
                            AutomationProperties.SetAutomationId(image, "QuickStartConnectionImage");
                            AutomationProperties.SetName(image, "Generated connection test image; not a screen capture");
                            stepBody.Children.Add(image);
                        }
                        else ShowError(session.ConnectionCheckMessage);
                    };
                    stepBody.Children.Add(test);
                    break;
                case SetupStep.Finish:
                    ObserveShortcuts();
                    var checklist = new StackPanel { Spacing = 10 };
                    AutomationProperties.SetAutomationId(checklist, "QuickStartChecklist");
                    checklist.Children.Add(UiStyles.Description("✓ Silent captures; tray opens the filename-based workspace on demand."));
                    checklist.Children.Add(UiStyles.Description($"{(session.FolderCheck == SetupCheckResult.Passed ? "✓" : "○")} Captures folder: {session.FolderPath}\n{session.FolderCheckMessage}"));
                    checklist.Children.Add(UiStyles.Description(session.ShortcutsChecked
                        ? $"{(session.ShortcutsNeedingAttention == 0 && session.RegisteredShortcuts > 0 ? "✓" : "○")} Shortcuts: {session.RegisteredShortcuts} active, {session.ShortcutsNeedingAttention} need attention. Unassigned optional shortcuts remain unassigned."
                        : "○ Shortcut registration has not been reported."));
                    checklist.Children.Add(UiStyles.Description($"✓ Clipboard: {OnOff(session.CopyToClipboard)}; saved files: {OnOff(session.SaveToFile)}."));
                    checklist.Children.Add(UiStyles.Description("✓ OneDrive settings location unchanged; device-local privacy and access choices do not roam."));
                    var privacyChanges = new[]
                    {
                        (session.TurnOffAiDescriptions, "Copilot descriptions"),
                        (session.TurnOffTextRecognition, "local text recognition"),
                        (session.TurnOffAgentAccess, "agent capture access"),
                        (session.TurnOffAgentHistoryAccess, "agent history access"),
                    }.Where(change => change.Item1).Select(change => change.Item2).ToArray();
                    checklist.Children.Add(UiStyles.Description(privacyChanges.Length > 0
                        ? "✓ Turn off: " + string.Join(", ", privacyChanges) + ". All other privacy choices preserved; no new permission granted."
                        : "✓ All existing AI, text-recognition, history-access and live agent permission choices preserved."));
                    checklist.Children.Add(UiStyles.Description($"{(session.ConnectionCheck == SetupCheckResult.Passed ? "✓" : "○")} Generated-image connection test: {session.ConnectionCheckMessage}"));
                    if (session.SkippedSteps.Count > 0)
                        checklist.Children.Add(UiStyles.Description("Skipped setup steps: " + string.Join(", ", session.SkippedSteps.Select(QuickStartStepTitle)) + ". Unchecked items remain unverified."));
                    stepBody.Children.Add(checklist);
                    stepBody.Children.Add(UiStyles.Description("Finish saves explicit folder/output changes and selected privacy reductions only. It does not capture anything, register new permissions or change your Windows or OneDrive account. Reopen setup any time from About Hotshot → Quick start guide."));
                    var review = QuickStartButton("Review setup from the beginning", "QuickStartReviewAgain");
                    review.Click += (_, _) => { while (session.Back()) { } Render(); };
                    stepBody.Children.Add(review);
                    break;
            }
            SetBusy(busy);
            scroll.ChangeView(null, 0, null);
            QuickStartStateChanged?.Invoke();
        }

        void AddHandoffNote() => stepBody.Children.Add(UiStyles.Description("Opening a settings page ends this setup without saving pending choices. Reopen setup from About Hotshot when you are ready."));

        void AddPrivacyChoice(string title, string id, bool currentlyEnabled, bool selected, Action<bool> changed)
        {
            var choice = new CheckBox { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
                IsChecked = selected, IsEnabled = currentlyEnabled };
            AutomationProperties.SetAutomationId(choice, id);
            AutomationProperties.SetName(choice, title);
            choice.Checked += (_, _) => { changed(true); QuickStartStateChanged?.Invoke(); };
            choice.Unchecked += (_, _) => { changed(false); QuickStartStateChanged?.Invoke(); };
            stepBody.Children.Add(choice);
        }

        back.Click += (_, _) => { if (!busy && session.Back()) Render(); };
        skip.Click += (_, _) => { if (!busy && session.Skip()) Render(); };
        cancel.Click += (_, _) =>
        {
            if (busy) return;
            session.Cancel();
            QuickStartStateChanged?.Invoke();
            dialog.Hide();
        };
        next.Click += async (_, _) =>
        {
            if (busy) return;
            if (session.CurrentStep != SetupStep.Finish)
            {
                if (session.Next(out var error)) Render();
                else if (error is not null) ShowError(error);
                return;
            }
            if (session.ValidateCompletion() is { } incomplete) { ShowError(incomplete); return; }
            SetBusy(true);
            try
            {
                var baseline = _app.Settings.Clone();
                var candidate = session.CreateCandidate(baseline);
                var errors = candidate.Validate();
                if (errors.Count > 0) { ShowError(string.Join(Environment.NewLine, errors)); return; }
                if (session.HasChanges && !await _app.ApplySettingsAsync(candidate, baseline))
                {
                    ShowError("Your choices could not be saved. The last saved configuration is still active. Retry Finish, Cancel to discard these choices, or review the diagnostic log and any OneDrive conflict.");
                    return;
                }
                _draft = _app.Settings.Clone();
                _draftBaseline = _app.Settings.Clone();
                _status.Text = session.HasChanges ? "Setup choices saved." : "Setup reviewed. Existing settings preserved.";
                _error.IsOpen = false;
                QuickStartLastError = null;
                if (session.Complete(out var completionError))
                {
                    QuickStartStateChanged?.Invoke();
                    dialog.Hide();
                }
                else if (completionError is not null) ShowError(completionError);
            }
            catch (Exception ex) when (IsSetupOperationError(ex))
            {
                Log.Error("Could not finish setup", ex);
                ShowError("Setup could not finish: " + ex.Message);
            }
            finally { SetBusy(false); }
        };
        // Finish hides the dialog before clearing busy; a terminal outcome must be allowed to close.
        dialog.Closing += (_, args) =>
        {
            if (busy && session.Outcome == SetupOutcome.InProgress && !_closed) args.Cancel = true;
        };
        dialog.Opened += (_, _) => next.Focus(FocusState.Programmatic);
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Escape) return;
            args.Handled = true;
            if (busy) return;
            session.Cancel();
            QuickStartStateChanged?.Invoke();
            dialog.Hide();
        };
        void OnWindowClosed(object sender, WindowEventArgs args) { stop.Cancel(); dialog.Hide(); }
        Closed += OnWindowClosed;
        try
        {
            Render();
            await dialog.ShowAsync();
        }
        finally
        {
            stop.Cancel();
            Closed -= OnWindowClosed;
            if (session.Outcome == SetupOutcome.InProgress) session.Cancel();
            QuickStartStateChanged?.Invoke();
        }
        if (_closed) return;
        if (destination is { } page)
        {
            if (page == SettingsPage.General)
                NavigateSearch(new SearchResult(SettingsPage.General, "Settings across devices", "onedrive"));
            else SelectPage(page);
        }
        else if (_navigation.SelectedItem is NavigationViewItem { Tag: SettingsPage selected }) RenderPage(selected);
    }

    private static TextBlock QuickStartText(string text, string id)
    {
        var result = UiStyles.Description(text);
        if (id.EndsWith("Status", StringComparison.Ordinal)) UiStyles.LiveStatus(result, id);
        else AutomationProperties.SetAutomationId(result, id);
        return result;
    }

    private static Button QuickStartButton(string text, string id)
    {
        var button = new Button { Content = text, MinWidth = 64 };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, text);
        return button;
    }

    private static ToggleSwitch QuickStartToggle(string title, string id, bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { Header = title, IsOn = value };
        AutomationProperties.SetAutomationId(toggle, id);
        AutomationProperties.SetName(toggle, title);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string QuickStartStepTitle(SetupStep step) => step switch
    {
        SetupStep.Welcome => "Welcome",
        SetupStep.Folder => "Captures folder",
        SetupStep.Shortcuts => "Keyboard shortcuts",
        SetupStep.Output => "Clipboard and saved files",
        SetupStep.OneDrive => "Settings across devices",
        SetupStep.Privacy => "Privacy on this device",
        SetupStep.Connection => "Optional connection test",
        _ => "Ready when you are",
    };

    private static async Task CheckQuickStartFolderAsync(string path, CancellationToken token)
    {
        await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Choose an existing folder, or Skip to keep the current unverified path. Setup will not create or move your captures folder.");
            var probe = Path.Combine(path, ".hotshot-setup-" + Guid.NewGuid().ToString("N") + ".probe");
            var created = false;
            try
            {
                await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose))
                {
                    created = true;
                    await stream.WriteAsync(new byte[] { 0x48 }, token);
                    await stream.FlushAsync(token);
                }
            }
            finally
            {
                if (created && File.Exists(probe)) File.Delete(probe);
            }
            if (File.Exists(probe)) throw new IOException("The write probe could not be removed: " + probe);
        }, token);
    }

    private static async Task<BitmapImage> LoadGeneratedConnectionImageAsync(CancellationToken token)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Mcp", "Hotshot.Mcp.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Use a packaged build with the MCP companion.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(exe, "--test-connection")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new IOException("The MCP companion could not start.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("The generated-image MCP connection test timed out.");
            }
            var json = await output;
            var errorText = await error;
            if (json.Length > 2_000_000) throw new InvalidDataException("The connection test returned an oversized response.");
            var response = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentResponse)
                ?? throw new InvalidDataException("MCP returned an empty connection result.");
            if (process.ExitCode != 0 || !response.Success) throw new IOException(response.Error ?? errorText);
            var image = response.Capture ?? throw new InvalidDataException("The generated image result is missing.");
            if (image.Id != "synthetic-connection-test" || image.ImageWidth != 256 || image.ImageHeight != 128 || image.ImageBase64 is null)
                throw new InvalidDataException("The connection test returned an unexpected image.");
            token.ThrowIfCancellationRequested();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(Convert.FromBase64String(image.ImageBase64));
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (bitmap.PixelWidth != 256 || bitmap.PixelHeight != 128)
                throw new InvalidDataException("The generated connection image has unexpected dimensions.");
            token.ThrowIfCancellationRequested();
            return bitmap;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The generated-image MCP connection test timed out.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static bool IsSetupOperationError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidOperationException or ArgumentException or NotSupportedException or FormatException or JsonException or TimeoutException or
        System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException;
}
