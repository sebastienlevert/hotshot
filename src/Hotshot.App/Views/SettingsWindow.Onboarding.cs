using System.Diagnostics;
using System.Text.Json;
using Hotshot.Core.Agents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Hotshot.Views;

internal sealed partial class SettingsWindow
{
    private async Task TestAgentConnectionAsync(Button button)
    {
        button.IsEnabled = false;
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "Mcp", "Hotshot.Mcp.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Use a packaged build with the MCP companion.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var start = new ProcessStartInfo(exe, "--test-connection") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start) ?? throw new IOException("The MCP companion could not start.");
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw new TimeoutException("The synthetic MCP connection test timed out.");
            }
            var response = JsonSerializer.Deserialize(await output, AgentJsonContext.Default.AgentResponse)
                ?? throw new InvalidDataException("MCP returned an empty connection result.");
            if (process.ExitCode != 0 || !response.Success) throw new IOException(response.Error ?? await error);
            var image = response.Capture ?? throw new InvalidDataException("The generated image result is missing.");
            if (image.Id != "synthetic-connection-test" || image.ImageWidth != 256 || image.ImageHeight != 128 || image.ImageBase64 is null)
                throw new InvalidDataException("The connection test returned an unexpected image.");
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) { writer.WriteBytes(Convert.FromBase64String(image.ImageBase64)); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            var body = new StackPanel { Spacing = 12 };
            var preview = new Image { Source = bitmap, Width = 256, Height = 128 };
            AutomationProperties.SetAutomationId(preview, "AgentSyntheticConnectionImage");
            body.Children.Add(preview);
            body.Children.Add(UiStyles.Description("The installed stdio companion connected to this native app and returned a generated image. No screen capture, historical files, settings change or model request occurred."));
            await new ContentDialog { Title = "Connection test passed", Content = body, CloseButtonText = "Close", XamlRoot = _navigation.XamlRoot }.ShowAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Agent connection test failed", ex);
            _error.Title = "Connection test failed"; _error.Message = ex.Message; _error.IsOpen = true;
        }
        finally { button.IsEnabled = true; }
    }

    public async Task ShowQuickStartAsync()
    {
        if (_navigation.XamlRoot is null)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? loaded = null;
            loaded = (_, _) => { _navigation.Loaded -= loaded; ready.TrySetResult(); };
            _navigation.Loaded += loaded;
            await ready.Task;
        }
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "Capture silently. Edit when you want.", FontSize = 22, TextWrapping = TextWrapping.Wrap });
        foreach (var text in new[]
        {
            "Print Screen selects a region or window. Space captures the monitor; R + arrows + Enter selects a region with the keyboard. Escape cancels.",
            "Captures save and copy without opening a window. Click the tray to browse filenames, annotate or redact, and export a local flattened copy.",
            "Wheel zooms; Space-drag pans; Ctrl+1 is 100%; Ctrl+0 fits. Export remembers styles and named presets.",
            "Library actions offer projects, baselines, storage cleanup, local backup/restore and relinking moved files.",
            "Copilot analysis and agent access are optional. Use review mode or application exclusions for sensitive captures. Test an agent connection with a generated image first.",
        }) body.Children.Add(UiStyles.Description(text));
        await new ContentDialog { Title = "Hotshot quick start", Content = new ScrollViewer { Content = body, MaxHeight = 480 },
            CloseButtonText = "Got it", XamlRoot = _navigation.XamlRoot }.ShowAsync();
    }
}
