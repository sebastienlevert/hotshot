using System.Runtime.InteropServices.WindowsRuntime;
using Hotshot.Capture;
using Hotshot.CaptureFlow;
using Hotshot.Core;
using Hotshot.Core.Descriptions;
using Hotshot.Core.History;
using Hotshot.Core.Hotkeys;
using Hotshot.Core.Settings;
using Hotshot.Editor;
using Hotshot.Editor.Model;
using Hotshot.Interop;
using Hotshot.Shell;
using Hotshot.Views;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Hotshot.ProductHarness;

internal static class ProductAcceptance
{
    private static readonly string[] ComparisonModes = ["Before", "After", "Difference", "Swipe"];
    public static async Task RunAsync(string output, AcceptanceReport report)
    {
        var profile = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(profile);
        paths.EnsureCreated();
        var seed = new AppSettings();
        seed.General.SaveFolder = Path.Combine(profile, "captures");
        seed.General.CopyToClipboard = false;
        seed.General.DescribeScreenshots = false;
        seed.General.RecognizeScreenshotText = false;
        seed.General.StartWithWindows = false;
        seed.General.Theme = AppTheme.Light;
        seed.Agents.Enabled = seed.Agents.AllowHistoryAccess = false;
        foreach (var action in Enum.GetValues<HotkeyAction>()) seed.Hotkeys.Set(action, "");
        new SettingsStore(paths.SettingsFile).Save(seed);
        Directory.CreateDirectory(seed.General.SaveFolder);
        Log.FilePath = Path.Combine(output, "fixture.log");
        AppServices? app = null;
        SettingsWindow? settings = null;
        WorkspaceWindow? workspace = null;
        ComparisonWindow? comparison = null;
        var accountClients = new Queue<ICopilotSignInClient>();
        var browserVisits = new List<Uri>();
        var operationFailures = new List<string>();
        Exception? accessibilityFailure = null;
        try
        {
            try
            {
                await report.CheckAsync("native-accessibility-control-and-editor-acceptance", () => AccessibilityChecks.RunAsync(output));
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                accessibilityFailure = exception;
                Console.Error.WriteLine("Required accessibility checks failed; collecting independent native window evidence before failing the run.");
            }
            app = new AppServices(paths, DispatcherQueue.GetForCurrentThread());
            app.StopSettingsSync();
            app.Failed += operationFailures.Add;
            using var messages = new MessageWindow("Hotshot.Acceptance." + Guid.NewGuid().ToString("N"));
            using var hotkeys = new HotkeyManager(messages);
            // Empty bindings initialize status without registering any global shortcut.
            hotkeys.Apply(app.Settings.Hotkeys);
            settings = new SettingsWindow(app, hotkeys,
                _ => Task.FromResult(accountClients.Dequeue()),
                (uri, _) => { browserVisits.Add(uri); return Task.CompletedTask; });
            NativeProductWindow.AllowPublicationSize(WinRT.Interop.WindowNative.GetWindowHandle(settings));
            settings.SelectPage(SettingsPage.Naming);
            await report.CheckAsync("settings-native-render-and-automation-persistence", async () =>
            {
                await ReadyAsync(settings);
                var pattern = Find<TextBox>(settings.Content, "NamingRegionPattern");
                var namingPeer = Peer(pattern);
                Console.WriteLine($"Native naming control: loaded={pattern.IsLoaded}, enabled={pattern.IsEnabled}, readOnly={pattern.IsReadOnly}, size={pattern.ActualWidth:F1}x{pattern.ActualHeight:F1}, peer={namingPeer.GetType().Name}; native UIA ValuePattern client check follows.");
                await NativeUiAutomation.SetTextAsync(settings, "NamingRegionPattern", "fixture-{counter}");
                var preview = Find<TextBlock>(settings.Content, "NamingRegionPreview");
                await UntilAsync(() => preview.Text.Contains("fixture-", StringComparison.Ordinal), "The native naming preview did not process the UIA edit.");
                await settings.SavePendingAsync();
                if (app.Settings.Naming.ScreenshotPattern != "fixture-{counter}" ||
                    new SettingsStore(paths.SettingsFile).Load().Naming.ScreenshotPattern != "fixture-{counter}")
                    throw new InvalidDataException("Native settings input was not persisted to the isolated profile.");
                if (string.IsNullOrWhiteSpace(preview.Text))
                    throw new InvalidDataException("The native naming preview did not update.");
                await RenderAsync(settings, Path.Combine(output, "settings-native.png"));
            });
            await report.CheckAsync("settings-native-responsive-header", () => ProductMedia.CheckSettingsHeaderAsync(settings));
            await report.CheckAsync("copilot-native-sign-in-consent-cancellation-and-failure", () =>
                CheckCopilotSignInAsync(settings, app, accountClients, browserVisits));
            var scale = ((FrameworkElement)settings.Content).XamlRoot.RasterizationScale;
            settings.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(800 * scale)));

            var before = GeneratedImage(false);
            var after = GeneratedImage(true);
            await report.CheckAsync("derived-capture-source-and-ai-exclusion-policy", () => CheckDerivativePolicyAsync(app, before));
            var first = await AddImageAsync(app, "fixture-before", before);
            var second = await AddImageAsync(app, "fixture-after", after);
            await File.WriteAllBytesAsync(Path.Combine(paths.OriginalsDirectory, first.Id + ".png"), await ImageCodec.EncodePngAsync(before));
            var document = new AnnotationDocument(before.Width, before.Height);
            document.Add(new RectangleAnnotation { Rect = new RectF(20, 20, 60, 40), Color = RgbaColor.Red, StrokeWidth = 3 });
            document.MarkSaved();
            await File.WriteAllTextAsync(Path.Combine(paths.OriginalsDirectory, first.Id + ".json"), document.ToJson());
            workspace = new WorkspaceWindow(app, new CaptureActions(app), _ =>
                throw new InvalidOperationException("Native acceptance cannot execute capture, desktop or external app commands."));
            var canvas = Find<CanvasControl>(workspace.Content, "AnnotationCanvas");
            canvas.ForceSoftwareRenderer = Program.SoftwareRenderer;
            workspace.Show();
            await report.CheckAsync("workspace-native-history-editor-draw-and-generated-export", async () =>
            {
                await ReadyAsync(workspace);
                var editor = Descendants(workspace.Content).OfType<EditorView>().Single();
                var editorFailures = new List<string>();
                editor.Failed += editorFailures.Add;
                try
                {
                    await workspace.SelectAsync(first);
                    if (!editor.HasImage || workspace.IsBusy || workspace.HasUnsavedEdits)
                        throw new InvalidDataException("The native editor did not load the generated image and annotation sidecar.");
                    var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Drawn(CanvasControl sender, CanvasDrawEventArgs args) => drawn.TrySetResult();
                    canvas.Draw += Drawn;
                    try { canvas.Invalidate(); await drawn.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                    finally { canvas.Draw -= Drawn; }
                    report.SetEditorRenderer(canvas.Device.ForceSoftwareRenderer);
                    if (Program.SoftwareRenderer && !canvas.Device.ForceSoftwareRenderer)
                        throw new InvalidDataException("The requested software editor renderer was not used.");
                    var png = await editor.ExportPngAsync();
                    var decoded = await ImageCodec.DecodePngAsync(png);
                    if (decoded.Width != before.Width || decoded.Height != before.Height || decoded.Pixels.SequenceEqual(before.Pixels))
                        throw new InvalidDataException("The real native Win2D export did not include the generated annotation.");
                    await File.WriteAllBytesAsync(Path.Combine(output, "editor-generated-export.png"), png);
                    await workspace.SelectAsync(second);
                    var list = Find<ListView>(workspace.Content, "CaptureHistory");
                    if (list.Items.Count != 2 || list.SelectedItem is not ListViewItem { Tag: HistoryItem selected } || selected.Id != second.Id)
                        throw new InvalidDataException("Native history selection did not follow the requested generated item.");
                    await workspace.SelectAsync(first);
                    if (editorFailures.Count > 0) throw new InvalidOperationException(string.Join("; ", editorFailures));
                    await RenderAsync(workspace, Path.Combine(output, "workspace-native.png"));
                }
                finally { editor.Failed -= editorFailures.Add; }
            });

            comparison = await ComparisonWindow.CreateAsync(before, after, ImageComparison.Compare(before, after, 8));
            comparison.Activate();
            await report.CheckAsync("comparison-native-selection-range-invoke-validation-and-render", async () =>
            {
                await ReadyAsync(comparison);
                var mode = Find<ComboBox>(comparison.Content, "ComparisonMode");
                for (var index = 0; index < ComparisonModes.Length; index++)
                {
                    Console.WriteLine("Checking real comparison selection: " + ComparisonModes[index]);
                    await NativeUiAutomation.SelectAsync(comparison, "ComparisonMode", ComparisonModes[index]);
                    if (mode.SelectedIndex != index)
                        throw new InvalidDataException("The native comparison automation selection did not take effect.");
                }
                var swipe = Find<Slider>(comparison.Content, "ComparisonSwipe");
                await NativeUiAutomation.SetRangeAsync(comparison, "ComparisonSwipe", 25);
                if (swipe.Value != 25) throw new InvalidDataException("The native swipe value was not changed.");
                Invoke(Find<Button>(comparison.Content, "ComparisonActualPixels"));
                Invoke(Find<Button>(comparison.Content, "ComparisonFit"));
                var expander = Descendants(comparison.Content).OfType<Expander>().Single();
                expander.IsExpanded = true;
                await Task.Delay(150);
                var ignored = Find<TextBox>(comparison.Content, "ComparisonIgnoredRegions");
                _ = Peer(ignored);
                var error = Find<InfoBar>(comparison.Content, "ComparisonError");
                var export = Find<Button>(comparison.Content, "ComparisonExport");
                await NativeUiAutomation.SetTextAsync(comparison, "ComparisonIgnoredRegions", "not a rectangle");
                await UntilAsync(() => error.IsOpen && !export.IsEnabled, "Comparison invalid-input error was not surfaced.");
                await NativeUiAutomation.SetTextAsync(comparison, "ComparisonIgnoredRegions", "10,10,5,5");
                await UntilAsync(() => !error.IsOpen && export.IsEnabled, "Comparison did not recover from invalid input.");
                if (string.IsNullOrWhiteSpace(Find<TextBlock>(comparison.Content, "ComparisonSummary").Text))
                    throw new InvalidDataException("Native comparison has no result summary.");
                await RenderAsync(comparison, Path.Combine(output, "comparison-native.png"));
            });

            await report.CheckAsync("native-keyboard-focus-and-tab-traversal", async () =>
            {
                settings.SelectPage(SettingsPage.General);
                await ReadyAsync(settings);
                var search = Find<AutoSuggestBox>(settings.Content, "SettingsSearch");
                var input = Descendants(search).OfType<TextBox>().First();
                if (!input.Focus(FocusState.Keyboard) || !Equals(FocusManager.GetFocusedElement(input.XamlRoot), input))
                    throw new InvalidOperationException("Native settings search cannot receive keyboard focus.");
                if (!FocusManager.TryMoveFocus(FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = settings.Content }) ||
                    Equals(FocusManager.GetFocusedElement(input.XamlRoot), input))
                    throw new InvalidOperationException("Native tab traversal did not move focus from settings search.");
                workspace.Show();
                await ReadyAsync(workspace);
                var history = Find<ListView>(workspace.Content, "CaptureHistory");
                if (!history.Focus(FocusState.Keyboard))
                    throw new InvalidOperationException("The native history list cannot receive keyboard focus.");
                comparison.Activate();
                var viewport = Find<ComparisonViewport>(comparison.Content, "ComparisonViewport");
                if (!viewport.Focus(FocusState.Keyboard))
                    throw new InvalidOperationException("Native comparison viewport cannot receive keyboard focus.");
            });
            await report.CheckAsync("guided-setup-native-complete-and-cancel-with-no-permission-grants", async () =>
            {
                settings.SelectPage(SettingsPage.General);
                await ReadyAsync(settings);
                await ProductMedia.CheckSetupAsync(settings, app);
                if (app.Settings.General.CopyToClipboard || app.Settings.General.DescribeScreenshots ||
                    app.Settings.General.RecognizeScreenshotText || app.Settings.Agents.Enabled || app.Settings.Agents.AllowHistoryAccess ||
                    app.Settings.General.StartWithWindows || app.SharedSettingsFile is not null ||
                    hotkeys.Status.Values.Any(status => status.State == HotkeyState.Registered))
                    throw new InvalidDataException("Native setup changed isolated privacy, synchronization or global hotkey permissions.");
                await settings.SavePendingAsync();
            });
            await report.CheckAsync("isolated-native-operation-and-profile-cleanup", async () =>
            {
                if (operationFailures.Count > 0) throw new InvalidOperationException(string.Join("; ", operationFailures));
                comparison.Close(); comparison = null;
                workspace.CloseForShutdown(); workspace = null;
                settings.Close(); settings = null;
                await Task.Delay(250);
                await app.CheckpointHistoryAsync();
                if (!await app.SaveAsync()) throw new IOException("Could not flush the isolated acceptance profile.");
                app.StopSettingsSync();
                app.Dispose(); app = null;
                Directory.Delete(profile, recursive: true);
                if (Directory.Exists(profile)) throw new IOException("The synthetic profile was not removed.");
            });
            if (accessibilityFailure is not null)
                throw new InvalidOperationException("Required native accessibility acceptance failed; other native checks do not override that failure.", accessibilityFailure);
        }
        finally
        {
            comparison?.Close();
            workspace?.CloseForShutdown();
            settings?.Close();
            if (app is not null)
            {
                app.StopSettingsSync();
                await app.SaveAsync();
                app.Dispose();
            }
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task CheckCopilotSignInAsync(SettingsWindow settings, AppServices app,
        Queue<ICopilotSignInClient> clients, List<Uri> browserVisits)
    {
        var baseline = app.Settings.Clone();
        settings.SelectPage(SettingsPage.Capture);
        await ReadyAsync(settings);
        var signIn = Find<Button>(settings.Content, "CopilotSignIn");
        var check = Find<Button>(settings.Content, "CopilotCheckSignIn");
        var cancel = Find<Button>(settings.Content, "CopilotCancelSignIn");
        var status = Find<TextBlock>(settings.Content, "CopilotAccountStatus");
        var error = Find<InfoBar>(settings.Content, "SettingsError");
        var anonymous = new AccountClient();
        clients.Enqueue(anonymous);
        Invoke(check);
        await UntilAsync(() => anonymous.Disposed && !settings.IsCopilotAccountBusy, "The native account check did not complete.");
        if (!status.Text.Contains("Not signed in", StringComparison.Ordinal) || anonymous.BeginCount != 0 || browserVisits.Count != 0)
            throw new InvalidDataException("Checking sign-in triggered authorization or did not report signed-out status.");

        var signedIn = new AccountClient { Successful = true };
        var persistedSignIn = new AccountClient { Successful = true };
        clients.Enqueue(signedIn);
        clients.Enqueue(persistedSignIn);
        Invoke(signIn);
        await UntilAsync(() => signedIn.Disposed && persistedSignIn.Disposed && !settings.IsCopilotAccountBusy, "Native browser sign-in did not complete.");
        if (!status.Text.Contains("Signed in as generated-user", StringComparison.Ordinal) || browserVisits.Count != 1 ||
            signedIn.CancelCount != 0 || signedIn.BeginCount != 1 || persistedSignIn.BeginCount != 0)
            throw new InvalidDataException($"Native generated sign-in failed: status={status.Text}; browser callbacks={browserVisits.Count}; begins={signedIn.BeginCount}; cancels={signedIn.CancelCount}.");

        var pending = new AccountClient { WaitForCancellation = true };
        clients.Enqueue(pending);
        Invoke(signIn);
        await pending.Advancing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!settings.IsCopilotAccountBusy || !settings.HasPendingChanges || signIn.IsEnabled || check.IsEnabled || !cancel.IsEnabled ||
            Find<Button>(settings.Content, "InstallCopilotComponent").IsEnabled)
            throw new InvalidDataException("Pending sign-in did not block conflicting operations and update restart.");
        Invoke(cancel);
        await UntilAsync(() => pending.Disposed && !settings.IsCopilotAccountBusy, "Native sign-in cancellation did not drain.");
        if (pending.CancelCount != 1 || !pending.CleanupTokenWasUsable || !status.Text.Contains("cancelled", StringComparison.Ordinal))
            throw new InvalidDataException("Native cancellation did not release the exact flow with a fresh cleanup token.");

        var failed = new AccountClient { CheckFailure = new IOException("generated-sensitive-auth-detail") };
        clients.Enqueue(failed);
        Invoke(check);
        await UntilAsync(() => failed.Disposed && !settings.IsCopilotAccountBusy, "The native account failure was not surfaced.");
        if (!error.IsOpen || status.Text.Contains("generated-sensitive-auth-detail", StringComparison.Ordinal) ||
            error.Message.Contains("generated-sensitive-auth-detail", StringComparison.Ordinal))
            throw new InvalidDataException("Native authentication error handling leaked provider details or hid the failure.");
        error.IsOpen = false;
        if (app.Settings.General.DescribeScreenshots != baseline.General.DescribeScreenshots ||
            app.Settings.General.ReviewBeforeAiUpload != baseline.General.ReviewBeforeAiUpload ||
            !app.Settings.General.AiExcludedApps.SequenceEqual(baseline.General.AiExcludedApps) ||
            app.Settings.Agents.Enabled != baseline.Agents.Enabled ||
            app.Settings.Agents.AllowHistoryAccess != baseline.Agents.AllowHistoryAccess)
            throw new InvalidDataException("Signing in changed upload/review/exclusion or coding-agent permissions.");
        if (clients.Count != 0 || browserVisits.Any(uri => uri.Host != "github.com"))
            throw new InvalidDataException("Native sign-in did not remain scoped to its generated clients and GitHub browser callback.");
        settings.SelectPage(SettingsPage.Naming);
        await ReadyAsync(settings);
    }

    private sealed class AccountClient : ICopilotSignInClient
    {
        public bool Successful { get; init; }
        public bool WaitForCancellation { get; init; }
        public Exception? CheckFailure { get; init; }
        public bool Disposed { get; private set; }
        public int BeginCount { get; private set; }
        public int CancelCount { get; private set; }
        public bool CleanupTokenWasUsable { get; private set; }
        public TaskCompletionSource Advancing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CopilotAccountStatus> GetStatusAsync(CancellationToken cancellationToken) => CheckFailure is { } failure
            ? Task.FromException<CopilotAccountStatus>(failure)
            : Task.FromResult(new CopilotAccountStatus(Successful, Successful ? "generated-user" : null, "https://github.com"));
        public Task<CopilotSignInStart> BeginAsync(CancellationToken cancellationToken)
        {
            BeginCount++;
            return Task.FromResult(new CopilotSignInStart("native-generated-flow",
                new(CopilotSignInStepKind.OpenBrowser) { Url = "https://github.com/login/oauth/authorize?state=generated" }));
        }
        public async Task<CopilotSignInStep> AdvanceAsync(string flowId, string? selectionId, CancellationToken cancellationToken)
        {
            if (flowId != "native-generated-flow") throw new InvalidDataException("The wrong native sign-in flow was advanced.");
            Advancing.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(CopilotSignInStepKind.Completed) { Account = new(true, "generated-user", "https://github.com") };
        }
        public Task CancelAsync(string flowId, CancellationToken cancellationToken)
        {
            if (flowId != "native-generated-flow") throw new InvalidDataException("The wrong native sign-in flow was cancelled.");
            CancelCount++;
            CleanupTokenWasUsable = !cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            if (Successful) await Task.Delay(100);
            Disposed = true;
        }
    }

    private static async Task CheckDerivativePolicyAsync(AppServices app, CapturedImage image)
    {
        var general = app.Settings.General;
        var wasEnabled = general.DescribeScreenshots;
        var requiredReview = general.ReviewBeforeAiUpload;
        var excludedApps = general.AiExcludedApps;
        var previousDescriptions = app.Descriptions;
        var savedIds = new List<string>();
        await using var descriptions = new ScreenshotDescriptions(app.History,
            () => throw new InvalidOperationException("Derivative policy checks must not contact a provider."), true);
        try
        {
            general.DescribeScreenshots = true;
            general.ReviewBeforeAiUpload = false;
            general.AiExcludedApps = ["private.exe"];
            descriptions.ApplyPolicy(general);
            app.Descriptions = descriptions;
            var pipeline = new OutputPipeline(app);
            foreach (var appName in new string?[] { "private", null, "Video frame", "Imported image", "Scrolling capture" })
            {
                var recording = new HistoryItem
                {
                    Path = Path.Combine(app.Paths.DataDirectory, "generated-source.mp4"),
                    Kind = CaptureKind.Recording,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    AppName = appName,
                    WindowTitle = "Generated recording",
                    MonitorIndex = 3,
                };
                var source = CaptureSource.FromHistory(recording);
                if (source.AppName != recording.AppName || source.WindowTitle != recording.WindowTitle ||
                    source.MonitorIndex != recording.MonitorIndex || source.Timestamp == recording.CreatedAt.LocalDateTime)
                    throw new InvalidDataException("Derivative source creation lost original authorization/naming context or reused the original timestamp.");
                var saved = await pipeline.SaveScreenshotAsync(image, CaptureKind.Region, source, copyToClipboard: false)
                    ?? throw new IOException("The generated derivative could not be saved.");
                savedIds.Add(saved.Id);
                var persisted = new HistoryStore(app.History.CheckpointPath, 100);
                persisted.Load();
                var item = persisted.Items.Single(item => item.Id == saved.Id);
                if (item.AppName != recording.AppName || item.WindowTitle != recording.WindowTitle ||
                    item.MonitorIndex != recording.MonitorIndex || item.DescriptionPending ||
                    item.DescriptionNeedsReview != CaptureAnalysisPolicy.RequiresReview(general, recording.AppName) ||
                    descriptions.IsBusy || !File.Exists(item.Path))
                    throw new InvalidDataException("Saved derivative metadata or cloud-analysis policy was not preserved.");
                if (await descriptions.EnqueueAsync(item))
                    throw new InvalidOperationException("An excluded or unknown-source derivative was analyzed automatically.");
            }
        }
        finally
        {
            app.Descriptions = previousDescriptions;
            general.DescribeScreenshots = wasEnabled;
            general.ReviewBeforeAiUpload = requiredReview;
            general.AiExcludedApps = excludedApps;
            foreach (var id in savedIds) app.History.Remove(id);
        }
        if (!await app.SaveAsync()) throw new IOException("Could not restore the isolated policy fixture's settings.");
    }

    private static async Task<HistoryItem> AddImageAsync(AppServices app, string id, CapturedImage image)
    {
        var path = Path.Combine(app.Settings.General.SaveFolder, id + ".png");
        await ImageCodec.SavePngAsync(image, path);
        var item = new HistoryItem
        {
            Id = id, Path = path, Width = image.Width, Height = image.Height,
            Kind = CaptureKind.Window, Project = "Generated acceptance", CreatedAt = DateTimeOffset.UtcNow,
        };
        app.History.Add(item);
        return item;
    }

    private static CapturedImage GeneratedImage(bool after)
    {
        const int width = 320, height = 180;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)(40 + x / 2);
                pixels[offset + 1] = (byte)(60 + y);
                pixels[offset + 2] = (byte)(after && x > 160 ? 220 : 80);
                pixels[offset + 3] = 255;
            }
        return new CapturedImage(width, height, pixels);
    }

    private static async Task ReadyAsync(Window window)
    {
        window.SystemBackdrop = null;
        window.Activate();
        await UntilAsync(() => window.Content is FrameworkElement { IsLoaded: true, ActualWidth: > 100, ActualHeight: > 100, XamlRoot: not null },
            "The native WinUI window did not load/measure. An interactive desktop and native renderer are required.");
        if (window.Content is Panel { Background: null } panel)
            panel.Background = new SolidColorBrush(panel.ActualTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 249, 249, 249));
        await Task.Delay(150);
    }

    private static async Task RenderAsync(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync((FrameworkElement)window.Content, 960, 720);
        if (bitmap.PixelWidth < 100 || bitmap.PixelHeight < 100)
            throw new InvalidDataException("Native XAML rendering returned an empty surface.");
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        if (pixels.Length != checked(bitmap.PixelWidth * bitmap.PixelHeight * 4) ||
            !Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 3] != 0) ||
            !Enumerable.Range(1, pixels.Length / 4 - 1).Any(index => pixels.AsSpan(index * 4, 4).SequenceEqual(pixels.AsSpan(0, 4)) == false))
            throw new InvalidDataException("Native XAML rendering returned a blank/uniform surface.");
        await ImageCodec.SavePngAsync(new CapturedImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels), path);
    }

    private static void Invoke(Button button)
    {
        if (!button.IsEnabled || Peer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("A required native control is disabled or lacks Invoke automation.");
        invoke.Invoke();
    }

    private static AutomationPeer Peer(FrameworkElement element) =>
        FrameworkElementAutomationPeer.CreatePeerForElement(element)
        ?? throw new InvalidOperationException($"The native {element.GetType().Name} has no automation peer.");

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == id ||
            element is FrameworkElement framework && framework.Name == id)
        ?? throw new InvalidOperationException("Required native control is missing: " + id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index)))
                yield return child;
    }

    private static async Task UntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(failure);
            await Task.Delay(100);
        }
    }
}
