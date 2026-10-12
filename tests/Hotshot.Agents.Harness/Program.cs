using System.Diagnostics;
using System.IO.Pipes;
using Hotshot;
using Hotshot.Agents;
using Hotshot.Capture;
using Hotshot.CaptureFlow;
using Hotshot.Core;
using Hotshot.Core.Agents;
using Hotshot.Core.History;
using Hotshot.Core.Settings;
using Microsoft.UI.Dispatching;
using Hotshot.Recording.Harness;

namespace Hotshot.AgentHarness;

internal static class Program
{
    internal const string FixtureTitle = "Hotshot synthetic agent capture fixture";
    internal static int ExitCode = 1;

    [STAThread]
    public static int Main(string[] args)
    {
        HarnessNative.SetProcessDpiAwarenessContext(-4);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Contains("--fixture"))
        {
            using var window = new TestWindow(FixtureTitle, 600, 400);
            Console.WriteLine("READY");
            Console.ReadLine();
            return 0;
        }
        try
        {
            var controller = DispatcherQueueController.CreateOnDedicatedThread();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.DispatcherQueue.TryEnqueue(async () =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                try { await Harness.RunAsync(); }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { completion.SetResult(); }
            });
            completion.Task.GetAwaiter().GetResult();
            controller.ShutdownQueueAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        return ExitCode;
    }
}

internal static class Harness
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Hotshot.Agents.Harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Log.FilePath = Path.Combine(directory, "harness.log");
        var name = "Hotshot.Agent.Test." + Guid.NewGuid().ToString("N");
        AgentCaptureServer? server = null;
        AppServices? services = null;
        Process? target = null;
        var artifacts = new List<string>();
        try
        {
            var cloud = Path.Combine(directory, "OneDrive");
            Directory.CreateDirectory(cloud);
            var app = new AppServices(new AppPaths(directory), DispatcherQueue.GetForCurrentThread(), cloud);
            services = app;
            app.Settings.General.SaveFolder = Path.Combine(directory, "captures");
            app.Settings.General.CopyToClipboard = false;
            app.Settings.General.DescribeScreenshots = false;
            app.Settings.General.RecognizeScreenshotText = false;
            app.Settings.Capture.IncludeCursor = false;
            var pipeline = new OutputPipeline(app);
            var capture = new CaptureCoordinator(app, pipeline);
            var unavailable = false;
            server = new AgentCaptureServer(app, capture, () => unavailable);
            server.Start(name);
            var status = await Send(new AgentRequest { Operation = "status" });
            Check(status.Success && status.Status?.Enabled == false && status.Status.SaveFolder is null, "Disabled status disclosed private settings.");
            Check((await Send(new AgentRequest { Operation = "windows" })).ErrorCode == "access_disabled", "Disabled window access was allowed.");
            Check(await app.SaveAsync(), "Initial shared preferences could not be saved.");
            var other = new RoamingSettingsStore(Path.Combine(directory, "other-machine", "settings.json"), () => cloud);
            var remote = other.Load();
            remote.General.Theme = AppTheme.Dark;
            remote.Agents.Enabled = true;
            other.Save(remote);
            app.CanReloadSharedSettings = () => false;
            await app.ReloadSharedSettingsAsync();
            Check(app.Settings.General.Theme != AppTheme.Dark, "Shared settings reloaded while native edits were protected.");
            app.CanReloadSharedSettings = () => true;
            await app.ReloadSharedSettingsAsync();
            Check(app.Settings.General.Theme == AppTheme.Dark && !app.Settings.Agents.Enabled &&
                app.Settings.General.SaveFolder == Path.Combine(directory, "captures"), "Shared preferences changed device paths or permissions.");
            remote.General.Theme = AppTheme.Light;
            other.Save(remote);
            var checks = 0;
            app.CanReloadSharedSettings = () => ++checks == 1;
            await app.ReloadSharedSettingsAsync();
            Check(app.Settings.General.Theme == AppTheme.Dark, "A late edit-start check did not defer shared changes.");
            app.Settings.Capture.ShowMagnifier = false;
            Check(await app.SaveAsync(), "A local change could not merge deferred shared settings.");
            Check(app.Settings.General.Theme == AppTheme.Light && !app.Settings.Capture.ShowMagnifier,
                "A local save reverted deferred portable changes or lost local edits.");
            app.CanReloadSharedSettings = () => true;
            remote.General.Theme = AppTheme.Dark;
            other.Save(remote);
            app.CanChangeSharedSettings = () => false;
            await app.ReloadSharedSettingsAsync(manual: true);
            Check(app.Settings.General.Theme == AppTheme.Light, "Manual reload bypassed the active capture/recording guard.");
            app.CanChangeSharedSettings = () => true;
            app.CanReloadSharedSettings = () => false;
            await app.ReloadSharedSettingsAsync(manual: true);
            Check(app.Settings.General.Theme == AppTheme.Dark, "Explicit reload did not work with Settings in the foreground.");
            app.CanReloadSharedSettings = () => true;
            Console.WriteLine("PASS native shared-settings reload defers editing and preserves local capture path/agent permissions.");
            var synthetic = await Send(new AgentRequest { Operation = "test_connection" });
            Check(synthetic.Success && synthetic.Capture?.Id == "synthetic-connection-test", "Generated connection test failed while access was off.");
            app.Settings.Agents.Enabled = true;
            app.Settings.Agents.SessionAccessOnly = true;
            Check((await Send(new AgentRequest { Operation = "windows" })).ErrorCode == "access_disabled", "Timed session access implicitly started.");
            server.GrantSession();
            Check((await Send(new AgentRequest { Operation = "windows" })).Success, "Granted session did not allow target listing.");
            server.RevokeSession();
            Check((await Send(new AgentRequest { Operation = "windows" })).ErrorCode == "access_disabled", "Revoked session allowed requests.");
            app.Settings.Agents.SessionAccessOnly = false;
            app.Settings.Agents.ApproveRequests = true;
            server.ApprovalHandler = (_, _, _) => Task.FromResult(false);
            Check((await Send(new AgentRequest { Operation = "windows" })).ErrorCode == "approval_denied", "Denied request bypassed native approval.");
            server.ApprovalHandler = (_, _, _) => Task.FromResult(true);
            Check((await Send(new AgentRequest { Operation = "windows" })).Success, "Approved target listing failed.");
            app.Settings.Agents.ApproveRequests = false;
            Check((await Send(new AgentRequest { Operation = "search_history" })).ErrorCode == "history_disabled", "History access was implicit.");

            var generated = new CapturedImage(600, 300, Enumerable.Range(0, 600 * 300).SelectMany(_ => new byte[] { 0, 69, 255, 255 }).ToArray());
            var path = Path.Combine(directory, "generated.png");
            await ImageCodec.SavePngAsync(generated, path);
            app.History.Add(new HistoryItem { Id = "generated", Path = path, Width = 600, Height = 300, Kind = CaptureKind.Region, RecognizedText = "Synthetic agent evidence" });
            app.Settings.Agents.AllowHistoryAccess = true;
            var history = await Send(new AgentRequest { Operation = "get_capture", CaptureId = "generated", MaxImageSize = 256 });
            Check(history.Success && history.Capture?.ImageWidth == 256 && history.Capture.ImageHeight == 128, "History preview geometry is incorrect.");
            var crop = await Send(new AgentRequest { Operation = "get_capture_region", CaptureId = "generated", Region = new AgentRect(20, 30, 50, 40) });
            Check(crop.Success && crop.Capture?.ImageWidth == 50 && crop.Capture.ImageHeight == 40 && crop.Capture.ImageRegion?.X == 20,
                "Known-image region geometry was incorrect.");
            var same = await Send(new AgentRequest { Operation = "compare_captures", BeforeId = "generated", CaptureId = "generated" });
            Check(same.Success && same.Comparison?.ChangedPixels == 0 && same.Capture?.ImageBase64 is not null, "Native visual comparison failed.");
            app.Settings.Agents.AllowedApps = ["restricted"];
            Check((await Send(new AgentRequest { Operation = "get_capture", CaptureId = "generated" })).ErrorCode == "target_restricted", "Historical app restrictions were bypassed.");
            app.Settings.Agents.AllowedApps = [];
            var decoded = await ImageCodec.DecodePngAsync(Convert.FromBase64String(history.Capture!.ImageBase64!));
            Check(decoded.Pixels[2] == 255 && decoded.Pixels[1] == 69, "History image pixels changed.");
            Check((await Send(new AgentRequest { Operation = "search_history", Query = "agent evidence" })).Captures?.Length == 1, "OCR search failed.");
            Check(!(await Send(new AgentRequest { Operation = "get_capture", CaptureId = path })).Success, "Arbitrary paths were accepted.");
            unavailable = true;
            Check((await Send(new AgentRequest { Operation = "monitors" })).ErrorCode == "app_busy", "Busy app accepted a request.");
            unavailable = false;
            Console.WriteLine("PASS native opt-in/history policy, known-ID reads, image resizing, OCR search and busy errors.");

            target = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fixture")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            }) ?? throw new InvalidOperationException("Could not start the synthetic window.");
            Check(await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "READY", "Synthetic window failed to launch.");
            await Task.Delay(1000);
            var windows = await Send(new AgentRequest { Operation = "windows", Limit = 50 });
            var window = windows.Windows?.SingleOrDefault(window => window.Title == Program.FixtureTitle)
                ?? throw new InvalidOperationException("Synthetic window was not enumerated.");
            var clipboard = HarnessNative.GetClipboardSequenceNumber();
            app.Settings.Capture.DelaySeconds = 2;
            app.Settings.Naming.ScreenshotPattern = "{app}-{width}x{height}-{counter:4}";
            var started = Stopwatch.StartNew();
            var result = await Send(new AgentRequest { Operation = "capture_window", WindowId = window.Id, MaxImageSize = 256 });
            Check(result.Success, "Native synthetic window capture failed: " + result.Error);
            var item = result.Capture!;
            Check(started.Elapsed >= TimeSpan.FromSeconds(2), "Capture delay was not honored.");
            Check(item.Width == window.Bounds.Width && item.Height == window.Bounds.Height && item.ImageWidth == 256, "Window/result geometry differs.");
            Check(item.Path.StartsWith(app.Settings.General.SaveFolder, StringComparison.OrdinalIgnoreCase) && item.Path.Contains("-0001.png"), "Capture folder/naming/counter differs.");
            Check(HarnessNative.GetClipboardSequenceNumber() == clipboard, "Disabled clipboard setting was ignored.");
            decoded = await ImageCodec.DecodePngAsync(Convert.FromBase64String(item.ImageBase64!));
            var sample = (decoded.Height / 2 * decoded.Width + decoded.Width / 2) * 4;
            Check(decoded.Pixels[sample] == 224 && decoded.Pixels[sample + 1] == 160 && decoded.Pixels[sample + 2] == 40,
                "Synthetic capture pixels differ; the interactive desktop may be unavailable or occluded.");
            Check(app.History.Items.Single(historyItem => historyItem.Id == item.Id).Path == item.Path, "Capture response did not identify its actual history entry.");
            Console.WriteLine("PASS real synthetic-window pixels, exact history ID, settings-backed save/naming/delay and disabled clipboard.");

            var region = new AgentRect(window.Bounds.X + window.Bounds.Width / 2, window.Bounds.Y + window.Bounds.Height / 2, 32, 32);
            var count = app.History.Items.Count;
            app.Settings.Capture.DelaySeconds = 5;
            var pending = Send(new AgentRequest { Operation = "capture_region", Region = region });
            await Task.Delay(500);
            app.Settings.Agents.Enabled = false;
            Check(!(await pending).Success && app.History.Items.Count == count, "Disable during delay still captured.");
            app.Settings.Agents.Enabled = true;
            using (var disconnected = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await disconnected.ConnectAsync(3000);
                await AgentFrames.WriteAsync(disconnected, AgentProtocol.Encode(new AgentRequest { Operation = "capture_region", Region = region }), AgentProtocol.MaxRequestBytes, default);
                await Task.Delay(300);
            }
            await Task.Delay(600);
            Check((await Send(new AgentRequest { Operation = "status" })).Success && app.History.Items.Count == count, "Disconnected request was not canceled.");
            app.Settings.Capture.DelaySeconds = 0;
            app.Settings.General.SaveToFile = false;
            var temporary = await Send(new AgentRequest { Operation = "capture_region", Region = region });
            Check(temporary.Success, "Temporary capture failed: " + temporary.Error);
            artifacts.Add(temporary.Capture!.Path);
            Check(app.History.Items.Single(historyItem => historyItem.Id == temporary.Capture.Id).IsTemporary, "Save-off policy was ignored.");
            Console.WriteLine("PASS permission revocation, disconnect cancellation, region capture and temporary-file policy.");
            Program.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
        finally
        {
            services?.StopSettingsSync();
            if (server is not null) { await server.StopAsync(); server.Dispose(); }
            if (target is not null)
            {
                await target.StandardInput.WriteLineAsync("exit");
                await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                target.Dispose();
            }
            foreach (var path in artifacts) File.Delete(path);
            Directory.Delete(directory, recursive: true);
        }

        async Task<AgentResponse> Send(AgentRequest request)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000, timeout.Token);
            await AgentFrames.WriteAsync(pipe, AgentProtocol.Encode(request), AgentProtocol.MaxRequestBytes, timeout.Token);
            return AgentProtocol.DecodeResponse(await AgentFrames.ReadAsync(pipe, AgentProtocol.MaxResponseBytes, timeout.Token));
        }
    }

    private static void Check(bool condition, string error)
    {
        if (!condition) throw new InvalidDataException(error);
    }
}
