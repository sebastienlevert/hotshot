using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hotshot.Capture;
using Hotshot.CaptureFlow;
using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Core.Settings;
using Hotshot.Editor.Model;
using Hotshot.Interop;
using Hotshot.Recording;
using Hotshot.Shell;
using Hotshot.Views;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hotshot.Core.Setup;
using Windows.Graphics;
using Windows.UI;

namespace Hotshot.ProductHarness;

internal static class ProductMedia
{
    public static async Task RunAsync(string output)
    {
        var profile = Path.Combine(Path.GetTempPath(), "Hotshot.Product." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        AppServices? app = null;
        WorkspaceWindow? workspace = null;
        SettingsWindow? settings = null;
        ComparisonWindow? comparison = null;
        try
        {
            Log.FilePath = Path.Combine(profile, "fixture.log");
            app = new AppServices(new AppPaths(profile), DispatcherQueue.GetForCurrentThread());
            app.StopSettingsSync();
            app.Settings.General.SaveFolder = Path.Combine(profile, "captures");
            app.Settings.General.CopyToClipboard = false;
            app.Settings.General.DescribeScreenshots = false;
            app.Settings.General.RecognizeScreenshotText = false;
            app.Settings.General.StartWithWindows = false;
            app.Settings.General.Theme = AppTheme.Light;
            app.Settings.Agents.Enabled = app.Settings.Agents.AllowHistoryAccess = false;
            Directory.CreateDirectory(app.Settings.General.SaveFolder);
            using var messages = new MessageWindow("Hotshot.Product.Message." + Guid.NewGuid().ToString("N"));
            app.MessageWindowHandle = messages.Handle;
            using var hotkeys = new HotkeyManager(messages);
            foreach (var action in Enum.GetValues<Hotshot.Core.Hotkeys.HotkeyAction>())
                app.Settings.Hotkeys.Set(action, "");
            app.Settings.Hotkeys.RegionScreenshot = "Ctrl+Alt+F9";
            hotkeys.Apply(app.Settings.Hotkeys);

            var before = await GenerateAsync(Path.Combine(app.Settings.General.SaveFolder, "checkout-before.png"), false);
            var after = await GenerateAsync(Path.Combine(app.Settings.General.SaveFolder, "checkout-after.png"), true);
            var plain = await GenerateAsync(Path.Combine(app.Settings.General.SaveFolder, "checkout-review.png"), false);
            var first = new HistoryItem { Id = "demo-review", Path = plain.Path, Width = 1200, Height = 760,
                Kind = CaptureKind.Window, Project = "Website review", Tags = ["review", "checkout"], AppName = "Demo",
                CreatedAt = DateTimeOffset.UtcNow };
            var second = new HistoryItem { Id = "demo-before", Path = before.Path, Width = 1200, Height = 760,
                Kind = CaptureKind.Window, Project = "Website review", Tags = ["baseline"], AppName = "Demo",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2) };
            app.History.Add(second);
            app.History.Add(first);
            File.Copy(plain.Path, Path.Combine(app.Paths.OriginalsDirectory, first.Id + ".png"));
            var annotation = new AnnotationDocument(1200, 760);
            annotation.Add(new ArrowAnnotation { Start = new Vector2(880, 520), End = new Vector2(720, 370), Color = RgbaColor.Red, StrokeWidth = 5 });
            annotation.Add(new RectangleAnnotation { Rect = new RectF(625, 312, 410, 118), Color = RgbaColor.Red, StrokeWidth = 4 });
            annotation.Add(new TextAnnotation { Position = new Vector2(650, 452), Text = "Make this action clearer", FontSize = 26, Color = RgbaColor.Red });
            await File.WriteAllTextAsync(Path.Combine(app.Paths.OriginalsDirectory, first.Id + ".json"), annotation.ToJson());

            workspace = new WorkspaceWindow(app, new CaptureActions(app), _ => throw new InvalidOperationException("This media fixture cannot trigger desktop capture."));
            workspace.Show();
            PrepareWindow(workspace);
            await workspace.SelectAsync(first);
            await Task.Delay(1200);
            await ScreenshotAsync(workspace, Path.Combine(output, "editor.png"));
            await using (var recorder = await ScreenRecorder.StartAsync(new RecordingOptions
            {
                Target = new WindowTarget(WinRT.Interop.WindowNative.GetWindowHandle(workspace)),
                OutputPath = Path.Combine(output, "workflow.mp4"), FramesPerSecond = 24, VideoBitrate = 4_000_000,
                CaptureCursor = false, CaptureSystemAudio = false, CaptureMicrophone = false,
            }))
            {
                await Task.Delay(4500);
                await workspace.SelectAsync(second);
                await Task.Delay(4000);
                await workspace.SelectAsync(first);
                await Task.Delay(4500);
                var video = await recorder.StopAsync();
                if (video.HasAudio || video.Duration.TotalSeconds < 10 || video.FileSize == 0)
                    throw new InvalidDataException("Product video did not meet its silent, real-window recording requirements.");
                Console.WriteLine($"PASS real WinUI workspace recording: {video.Duration.TotalSeconds:F1}s, no audio.");
            }
            workspace.CloseForShutdown();
            workspace = null;
            await Task.Delay(300);
            settings = new SettingsWindow(app, hotkeys);
            settings.SelectPage(SettingsPage.Naming);
            PrepareWindow(settings);
            await Task.Delay(1200);
            await ScreenshotAsync(settings, Path.Combine(output, "settings.png"));
            await CheckSetupAsync(settings, app);
            settings.Close();
            settings = null;
            await Task.Delay(500);
            comparison = await ComparisonWindow.CreateAsync(before.Image, after.Image, ImageComparison.Compare(before.Image, after.Image, tolerance: 8));
            comparison.Activate();
            PrepareWindow(comparison);
            await Task.Delay(1000);
            await ScreenshotAsync(comparison, Path.Combine(output, "comparison.png"));
            comparison.Close();
            comparison = null;

            var evidence = new
            {
                schemaVersion = 1,
                collectedAt = DateTimeOffset.UtcNow,
                applicationVersion = typeof(AppServices).Assembly.GetName().Version?.ToString(),
                windowsBuild = Environment.OSVersion.Version.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                monitors = Monitors.GetAll().Select(monitor => new { width = monitor.Bounds.Width, height = monitor.Bounds.Height, scale = monitor.Scale, primary = monitor.IsPrimary }),
                passed = new[] { "real WinUI isolated-profile rendering", "WGC screenshots of own native windows", "silent native workspace recording", "filename selection", "annotation sidecar loading", "native comparison rendering" },
                notVerified = new[] { "physical ARM64 execution", "mixed-scale multi-monitor movement", "HDR display fidelity", "Narrator usability" },
                privacy = "Generated library only; captures target this harness's windows. No desktop, clipboard, microphone, real profile, OneDrive, or provider access.",
            };
            await File.WriteAllTextAsync(Path.Combine(output, "device-evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS real-device evidence recorded without identity, account, device names, paths, or serials.");
        }
        finally
        {
            comparison?.Close();
            workspace?.CloseForShutdown();
            settings?.Close();
            app?.StopSettingsSync();
            if (app is not null) await app.CheckpointHistoryAsync();
            if (app is not null) await app.SaveAsync();
            Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task CheckSetupAsync(SettingsWindow window, AppServices app)
    {
        var before = app.Settings.Clone();
        var setup = window.ShowQuickStartAsync();
        await Task.Delay(400);
        InvokeDialogButton(window, "QuickStartNext");
        await Task.Delay(200);
        for (var step = 0; step < 6; step++)
        {
            InvokeDialogButton(window, "QuickStartSkip");
            await Task.Delay(150);
        }
        InvokeDialogButton(window, "QuickStartNext");
        await setup.WaitAsync(TimeSpan.FromSeconds(20));
        if (window.QuickStartOutcome != SetupOutcome.Completed || app.Settings.General.DescribeScreenshots != before.General.DescribeScreenshots ||
            app.Settings.Agents.Enabled != before.Agents.Enabled || app.Settings.Agents.AllowHistoryAccess != before.Agents.AllowHistoryAccess)
            throw new InvalidDataException("Guided setup did not complete with unchanged privacy permissions.");
        var cancelled = window.ShowQuickStartAsync();
        await Task.Delay(250);
        InvokeDialogButton(window, "QuickStartCancel");
        await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        if (window.QuickStartOutcome != SetupOutcome.Cancelled)
            throw new InvalidDataException("Guided setup cancel did not report its actual outcome.");
        Console.WriteLine("PASS native guided setup completion/cancel and privacy preservation.");
    }

    private static void InvokeDialogButton(Window window, string id)
    {
        Button? button = null;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)window.Content).XamlRoot))
            button ??= Find<Button>(popup.Child, id);
        button ??= Find<Button>((DependencyObject)window.Content, id);
        if (button is null || !button.IsEnabled) throw new InvalidOperationException("Native setup control is not available: " + id);
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("Native setup control lacks invoke support: " + id);
        invoke.Invoke();
    }

    private static T? Find<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T match && AutomationProperties.GetAutomationId(root) == id) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(VisualTreeHelper.GetChild(root, index), id) is { } child) return child;
        return null;
    }

    private static void PrepareWindow(Window window)
    {
        window.SystemBackdrop = null;
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = ElementTheme.Light;
            if (root is Panel panel)
                panel.Background = new SolidColorBrush(Color.FromArgb(255, 249, 249, 249));
        }
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        NativeProductWindow.AllowPublicationSize(handle);
        if (!Win32.SetWindowDisplayAffinity(handle, Win32.WDA_NONE))
            throw new InvalidOperationException("Cannot make this fixture-owned window capturable.");
        window.AppWindow.MoveAndResize(new RectInt32(20, 80, 2200, 1500));
    }

    private static async Task ScreenshotAsync(Window window, string path)
    {
        var captured = await WindowScreenshot.CaptureAsync(WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (captured.Image.Width < 480 || captured.Image.Height < 360)
            throw new InvalidDataException($"Native product window is too small to document: {captured.Image.Width}x{captured.Image.Height}.");
        await ImageCodec.SavePngAsync(captured.Image, path);
        Console.WriteLine($"PASS real native window media: {Path.GetFileName(path)}, {captured.Image.Width}x{captured.Image.Height}.");
    }

    private static async Task<(string Path, CapturedImage Image)> GenerateAsync(string path, bool improved)
    {
        using var device = new CanvasDevice();
        using var canvas = new CanvasRenderTarget(device, 1200, 760, 96);
        using (var drawing = canvas.CreateDrawingSession())
        {
            var ink = Color.FromArgb(255, 38, 28, 22);
            var orange = Color.FromArgb(255, 255, 138, 25);
            var muted = Color.FromArgb(255, 100, 80, 67);
            drawing.Clear(Color.FromArgb(255, 255, 247, 237));
            drawing.DrawText("Northstar / Checkout", new Vector2(70, 60), ink, new CanvasTextFormat { FontSize = 24, FontFamily = "Segoe UI" });
            drawing.DrawText("A small change.\nA clearer next step.", new Vector2(70, 145), ink, new CanvasTextFormat { FontSize = 52, FontFamily = "Segoe UI", FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 } });
            drawing.DrawText("Generated example content. No personal data.", new Vector2(72, 300), muted, new CanvasTextFormat { FontSize = 20 });
            drawing.FillRoundedRectangle(70, 385, 410, 215, 20, 20, Color.FromArgb(255, 255, 255, 255));
            drawing.DrawText("Review order\nStudio notebook             $18\nDesk organizer              $24\n\nTotal                         $42", new Vector2(95, 410), ink, new CanvasTextFormat { FontSize = 24 });
            drawing.FillRoundedRectangle(620, 160, 435, 430, 20, 20, Color.FromArgb(255, 255, 255, 255));
            drawing.DrawText("Ready to continue?", new Vector2(650, 205), ink, new CanvasTextFormat { FontSize = 30 });
            drawing.DrawText("Your order is ready.\nThe next action should be obvious.", new Vector2(650, 265), muted, new CanvasTextFormat { FontSize = 21 });
            drawing.FillRoundedRectangle(650, 335, improved ? 335 : 170, 60, 10, 10, improved ? orange : Color.FromArgb(255, 237, 232, 227));
            drawing.DrawText(improved ? "Confirm order" : "Continue", new Vector2(675, 350), ink, new CanvasTextFormat { FontSize = 23 });
            drawing.DrawText("Example checkout / no real purchase", new Vector2(72, 676), muted, new CanvasTextFormat { FontSize = 18 });
        }
        using var stream = new MemoryStream();
        await canvas.SaveAsync(stream.AsRandomAccessStream(), CanvasBitmapFileFormat.Png);
        var png = stream.ToArray();
        await File.WriteAllBytesAsync(path, png);
        return (path, await ImageCodec.DecodePngAsync(png));
    }
}
