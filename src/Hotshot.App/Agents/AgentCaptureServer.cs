using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Hotshot.Capture;
using Hotshot.CaptureFlow;
using Hotshot.Core;
using Hotshot.Core.Agents;
using Hotshot.Core.History;

namespace Hotshot.Agents;

internal sealed class AgentCaptureServer(AppServices app, CaptureCoordinator capture, Func<bool> isUnavailable) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private int _requests;
    public bool IsBusy => Volatile.Read(ref _requests) > 0;
    public string Status { get; private set; } = "Agent access is off.";
    public event Action? StatusChanged;

    public void Start(string? pipeName = null)
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The Windows user identity is unavailable for agent IPC.");
        _listener = Task.Run(() => ListenAsync(pipeName ?? AgentProtocol.PipeName(identity, Process.GetCurrentProcess().SessionId)));
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        Status = app.Settings.Agents.Enabled
            ? "Ready for local coding agents. Capture tools use your current Hotshot settings."
            : "Agent access is off. Connected agents can only read the enabled/disabled status.";
        StatusChanged?.Invoke();
    }

    private async Task ListenAsync(string name)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                if (!NativeAgents.GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var sessionId) ||
                    sessionId != (uint)Process.GetCurrentProcess().SessionId)
                {
                    Log.Warn("Agent IPC connection rejected: client is not in this Windows session.");
                    var rejection = AgentResponse.Failure("wrong_session", "Connect from the same interactive Windows session as Hotshot.");
                    await AgentFrames.WriteAsync(pipe, AgentProtocol.Encode(rejection), AgentProtocol.MaxResponseBytes, deadline.Token);
                    continue;
                }
                var bytes = await AgentFrames.ReadAsync(pipe, AgentProtocol.MaxRequestBytes, deadline.Token);
                using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var disconnected = CancelOnDisconnectAsync(pipe, deadline, disconnect.Token);
                AgentResponse response;
                try
                {
                    var request = AgentProtocol.DecodeRequest(bytes);
                    request.Validate();
                    Interlocked.Increment(ref _requests);
                    try { response = await OnUiAsync(() => HandleAsync(request, deadline.Token), deadline.Token); }
                    finally { Interlocked.Decrement(ref _requests); }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or System.Text.Json.JsonException)
                {
                    response = AgentResponse.Failure("invalid_request", ex.Message);
                }
                finally
                {
                    disconnect.Cancel();
                    await disconnected;
                }
                await AgentFrames.WriteAsync(pipe, AgentProtocol.Encode(response), AgentProtocol.MaxResponseBytes, deadline.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                Log.Warn($"Agent IPC request failed: {ex.Message}");
                try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            }
        }
    }

    private static async Task CancelOnDisconnectAsync(Stream pipe, CancellationTokenSource request, CancellationToken token)
    {
        try
        {
            await pipe.ReadExactlyAsync(new byte[1], token);
            request.Cancel();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { request.Cancel(); }
    }

    private Task<AgentResponse> OnUiAsync(Func<Task<AgentResponse>> action, CancellationToken token)
    {
        var completion = new TaskCompletionSource<AgentResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!app.Dispatcher.TryEnqueue(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                completion.TrySetResult(await action());
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex)
            {
                Log.Error("Agent capture request failed", ex);
                completion.TrySetResult(AgentResponse.Failure("operation_failed", ex.Message));
            }
        })) completion.SetResult(AgentResponse.Failure("app_exiting", "Hotshot is shutting down."));
        return completion.Task;
    }

    private async Task<AgentResponse> HandleAsync(AgentRequest request, CancellationToken token)
    {
        var settings = app.Settings;
        var version = typeof(AgentCaptureServer).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        if (request.Operation == "status")
        {
            var status = settings.Agents.Enabled
                ? new AgentStatus(true, settings.Agents.AllowHistoryAccess, version, settings.General.SaveFolder,
                    settings.General.SaveToFile, settings.General.CopyToClipboard, settings.Capture.IncludeCursor,
                    settings.Capture.DelaySeconds, settings.General.RecognizeScreenshotText, settings.General.DescribeScreenshots)
                : new AgentStatus(false, false, version);
            return new AgentResponse { Success = true, Status = status };
        }
        if (!settings.Agents.Enabled)
            return AgentResponse.Failure("access_disabled", "Enable Hotshot Settings > Coding agents > Agent access before using capture tools.");
        if (isUnavailable() || capture.IsBusy) return AgentResponse.Failure("app_busy", "Hotshot is exiting, recording, or processing another capture. Retry when idle.");
        if (request.Operation == "windows")
            return new AgentResponse { Success = true, Windows = Windows().Take(request.Limit).Select(ToAgentWindow).ToArray() };
        if (request.Operation == "monitors")
            return new AgentResponse
            {
                Success = true, Monitors = Monitors.GetAll().Select(monitor =>
                    new AgentMonitor(monitor.Index, monitor.DeviceName, Rect(monitor.Bounds), monitor.Scale, monitor.IsPrimary)).Take(request.Limit).ToArray(),
            };
        if (request.Operation is "search_history" or "get_capture")
        {
            if (!settings.Agents.AllowHistoryAccess)
                return AgentResponse.Failure("history_disabled", "Enable Coding agents > Allow history access to expose previous capture files and descriptions.");
            if (request.Operation == "search_history")
                return new AgentResponse { Success = true, Captures = app.History.Items.Where(item => item.MatchesSearch(request.Query)).Take(request.Limit).Select(Metadata).ToArray() };
            var item = app.History.Items.FirstOrDefault(item => item.Id == request.CaptureId);
            if (item is null) return AgentResponse.Failure("not_found", "The capture ID is no longer in Hotshot history.");
            var result = await ResultAsync(item, request, token);
            token.ThrowIfCancellationRequested();
            if (!app.Settings.Agents.Enabled || !app.Settings.Agents.AllowHistoryAccess)
                return AgentResponse.Failure("access_disabled", "Agent or history access was disabled during the request.");
            return new AgentResponse { Success = true, Capture = result };
        }
        EnsureDesktopAccessible();
        PixelRect bounds;
        CaptureKind kind;
        CaptureSource source;
        if (request.Operation == "capture_window")
        {
            var window = Windows().FirstOrDefault(window => WindowId(window.Handle) == request.WindowId)
                ?? throw new ArgumentException("The window ID is not visible or no longer exists. List windows again.");
            bounds = window.Bounds.Intersect(Monitors.VirtualScreen);
            kind = CaptureKind.Window;
            source = CaptureSource.Now(WindowEnumerator.GetProcessName(window.ProcessId), window.Title);
        }
        else if (request.Operation == "capture_monitor")
        {
            var monitor = Monitors.GetAll().FirstOrDefault(monitor => monitor.Index == request.MonitorIndex)
                ?? throw new ArgumentException("Monitor index does not exist.");
            bounds = monitor.Bounds;
            kind = CaptureKind.Monitor;
            source = CaptureSource.Now(monitor: monitor.Index);
        }
        else
        {
            var region = request.Region!;
            bounds = new PixelRect(region.X, region.Y, region.Width, region.Height);
            if (bounds.Intersect(Monitors.VirtualScreen) != bounds) throw new ArgumentException("The region must lie completely inside the virtual screen.");
            kind = CaptureKind.Region;
            source = CaptureSource.Now();
        }
        Rect(bounds).Validate();
        Status = $"Agent requested {request.Operation}.";
        StatusChanged?.Invoke();
        Log.Info($"Agent operation: {request.Operation}");
        try
        {
            var (item, image, warnings) = await capture.CaptureForAgentAsync(bounds, kind, source, () =>
            {
                if (!app.Settings.Agents.Enabled || isUnavailable()) return false;
                EnsureDesktopAccessible();
                if (request.Operation == "capture_window")
                    return Windows().Any(window => WindowId(window.Handle) == request.WindowId &&
                        window.Bounds.Intersect(Monitors.VirtualScreen) == bounds);
                if (request.Operation == "capture_monitor")
                    return Monitors.GetAll().Any(monitor => monitor.Index == request.MonitorIndex && monitor.Bounds == bounds);
                return bounds.Intersect(Monitors.VirtualScreen) == bounds;
            }, token);
            var preview = await Task.Run(async () =>
            {
                var scaled = image.Downscale(request.MaxImageSize);
                var png = await ImageCodec.EncodePngAsync(scaled);
                return Metadata(item) with { ImageBase64 = Convert.ToBase64String(png), ImageWidth = scaled.Width, ImageHeight = scaled.Height };
            }, token);
            token.ThrowIfCancellationRequested();
            if (!app.Settings.Agents.Enabled) return AgentResponse.Failure("access_disabled", "Agent access was disabled during capture.");
            return new AgentResponse { Success = true, Capture = preview, Warnings = warnings.Length > 0 ? warnings : null };
        }
        finally { RefreshStatus(); }
    }

    private async Task<AgentCapture> ResultAsync(HistoryItem item, AgentRequest request, CancellationToken token)
    {
        var result = Metadata(item);
        if (!request.IncludeImage || item.IsVideo) return result;
        return await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (new FileInfo(item.Path).Length > 64 * 1024 * 1024) throw new IOException("Capture exceeds the agent image-read limit.");
            var image = await ImageCodec.LoadAsync(item.Path, request.MaxImageSize);
            var png = await ImageCodec.EncodePngAsync(image);
            return result with { ImageBase64 = Convert.ToBase64String(png), ImageWidth = image.Width, ImageHeight = image.Height };
        }, token);
    }

    private static AgentCapture Metadata(HistoryItem item) =>
        new(item.Id, item.Path, item.Kind.ToString(), item.Width, item.Height, item.CreatedAt, item.Summary, item.Description, item.RecognizedText);
    private static IReadOnlyList<WindowInfo> Windows() => WindowEnumerator.GetVisibleWindows((uint)Environment.ProcessId);
    private static string WindowId(nint handle) => ((long)handle).ToString("X", CultureInfo.InvariantCulture);
    private static AgentRect Rect(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
    private static AgentWindow ToAgentWindow(WindowInfo window) => new(WindowId(window.Handle), window.Title,
        WindowEnumerator.GetProcessName(window.ProcessId), Rect(window.Bounds));

    private static void EnsureDesktopAccessible()
    {
        var desktop = NativeAgents.OpenInputDesktop(0, false, 1);
        if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "The interactive desktop is unavailable. Reconnect and unlock Windows before capturing.");
        NativeAgents.CloseDesktop(desktop);
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_listener is not null) await _listener;
    }

    public void Dispose() => _stop.Cancel();
}
