using System.Runtime.InteropServices.WindowsRuntime;
using Hotshot.Capture;
using Hotshot.CaptureFlow;
using Hotshot.Core;
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
            settings = new SettingsWindow(app, hotkeys);
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
            var scale = ((FrameworkElement)settings.Content).XamlRoot.RasterizationScale;
            settings.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(800 * scale)));

            var before = GeneratedImage(false);
            var after = GeneratedImage(true);
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
