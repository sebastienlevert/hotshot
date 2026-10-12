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

internal sealed class AgentCaptureServer(AppServices app, CaptureCoordinator capture, Func<bool> isUnavailable, Func<nint>? settingsWindow = null) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private int _requests;
    private readonly Queue<AgentActivity> _activity = new();
    private readonly Dictionary<int, AgentClient> _clients = [];
    private DateTimeOffset? _sessionExpires;
    public DateTimeOffset? SessionExpires => _sessionExpires;
    public IReadOnlyList<AgentClient> Clients => _clients.Values.OrderByDescending(client => client.LastSeen).Take(10).ToArray();
    public Func<AgentRequest, AgentClient, CancellationToken, Task<bool>>? ApprovalHandler { get; set; }
    public IReadOnlyList<AgentActivity> Activity => _activity.ToArray();
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
        if (!app.Settings.Agents.Enabled) _sessionExpires = null;
        Status = HasAccess
            ? (app.Settings.Agents.SessionAccessOnly ? $"Native agent access expires at {_sessionExpires:t}." :
                "Ready for local coding agents. Capture tools use your current Hotshot settings.")
            : app.Settings.Agents.Enabled && app.Settings.Agents.SessionAccessOnly
                ? "Session access is not granted or expired. Grant a timed session to allow requests."
            : "Agent access is off. Connected agents can only read the enabled/disabled status.";
        StatusChanged?.Invoke();
    }

    public void GrantSession()
    {
        if (!app.Settings.Agents.Enabled) throw new InvalidOperationException("Enable agent access before granting a session.");
        _sessionExpires = DateTimeOffset.Now.AddMinutes(app.Settings.Agents.SessionMinutes);
        Status = $"Access granted until {_sessionExpires:t} for this Hotshot run.";
        StatusChanged?.Invoke();
    }
    public void RevokeSession() { _sessionExpires = null; RefreshStatus(); }
    private bool HasAccess => app.Settings.Agents.Enabled && (!app.Settings.Agents.SessionAccessOnly ||
        _sessionExpires is { } expires && DateTimeOffset.Now < expires);

    private async Task ListenAsync(string name)
    {
        while (!_stop.IsCancellationRequested)
        {
            int? clientId = null;
            var counted = false;
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                Interlocked.Increment(ref _requests);
                counted = true;
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
                if (!NativeAgents.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
                    throw new IOException("The native agent connection could not be identified.");
                clientId = checked((int)pid);
                var client = new AgentClient(clientId.Value, WindowEnumerator.GetProcessName(pid), DateTimeOffset.Now, true);
                var bytes = await AgentFrames.ReadAsync(pipe, AgentProtocol.MaxRequestBytes, deadline.Token);
                using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var disconnected = CancelOnDisconnectAsync(pipe, deadline, disconnect.Token);
                AgentResponse response;
                try
                {
                    var request = AgentProtocol.DecodeRequest(bytes);
                    request.Validate();
                    response = await OnUiAsync(async () =>
                        {
                            _clients[client.ProcessId] = client;
                            while (_clients.Count > 20) _clients.Remove(_clients.Values.OrderBy(entry => entry.LastSeen).First().ProcessId);
                            StatusChanged?.Invoke();
                            AgentResponse result;
                            try
                            {
                                if (request.Operation is not ("status" or "activity" or "test_connection") &&
                                    app.Settings.Agents.ApproveRequests && HasAccess)
                                {
                                    if (ApprovalHandler is null || !await ApprovalHandler(request, client, deadline.Token))
                                        result = AgentResponse.Failure("approval_denied", "The native approval request was denied or canceled.");
                                    else result = await HandleAsync(request, deadline.Token);
                                }
                                else result = await HandleAsync(request, deadline.Token);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                Log.Error("Agent operation failed", ex);
                                result = AgentResponse.Failure("operation_failed", ex.Message);
                            }
                            if (request.Operation is not ("status" or "activity"))
                            {
                                _activity.Enqueue(new AgentActivity(DateTimeOffset.Now, request.Operation, result.Success, result.ErrorCode, client.ProcessId));
                                while (_activity.Count > 100) _activity.Dequeue();
                                StatusChanged?.Invoke();
                            }
                            return result;
                        }, deadline.Token);
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
                app.Post(() =>
                {
                    Status = "The last agent connection failed: " + ex.Message;
                    StatusChanged?.Invoke();
                });
                try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            }
            finally
            {
                if (counted) Interlocked.Decrement(ref _requests);
                if (clientId is { } id) app.Post(() =>
                {
                    if (_clients.TryGetValue(id, out var client)) _clients[id] = client with { Connected = false };
                    StatusChanged?.Invoke();
                });
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
        if (request.Operation == "test_connection")
        {
            var pixels = new byte[256 * 128 * 4];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 120; pixels[i + 2] = 240; pixels[i + 3] = 255; }
            var png = await Task.Run(() => ImageCodec.EncodePngAsync(new CapturedImage(256, 128, pixels)), token);
            return new AgentResponse { Success = true, Capture = new AgentCapture("synthetic-connection-test", "", "Synthetic", 256, 128,
                DateTimeOffset.Now, "Generated connection test", "No desktop, files or model provider was accessed.", null, Convert.ToBase64String(png), 256, 128) };
        }
        if (request.Operation == "status")
        {
            var status = HasAccess
                ? new AgentStatus(HasAccess, settings.Agents.AllowHistoryAccess && HasAccess, version, settings.General.SaveFolder,
                    settings.General.SaveToFile, settings.General.CopyToClipboard, settings.Capture.IncludeCursor,
                    settings.Capture.DelaySeconds, settings.General.RecognizeScreenshotText, settings.General.DescribeScreenshots,
                    settings.Agents.AllowedApps, AgentAccessPolicy.AllowsRegion(settings.Agents), AgentAccessPolicy.AllowsMonitor(settings.Agents))
                : new AgentStatus(false, false, version);
            return new AgentResponse { Success = true, Status = status, Clients = settings.Agents.Enabled ? Clients.ToArray() : null };
        }
        if (!HasAccess)
            return AgentResponse.Failure("access_disabled", "Enable Hotshot Settings > Coding agents > Agent access before using capture tools.");
        if (request.Operation == "activity")
            return new AgentResponse { Success = true, Activity = _activity.Reverse().Skip(request.Offset).Take(request.Limit).ToArray() };
        if (isUnavailable() || capture.IsBusy) return AgentResponse.Failure("app_busy", "Hotshot is exiting, recording, or processing another capture. Retry when idle.");
        if (request.Operation == "windows")
            return new AgentResponse { Success = true, Windows = Windows().Where(window => AgentAccessPolicy.AllowsApp(settings.Agents,
                WindowEnumerator.GetProcessName(window.ProcessId))).Skip(request.Offset).Take(request.Limit).Select(ToAgentWindow).ToArray() };
        if (request.Operation == "monitors")
            return new AgentResponse
            {
                Success = true, Monitors = Monitors.GetAll().Select(monitor =>
                    new AgentMonitor(monitor.Index, monitor.DeviceName, Rect(monitor.Bounds), monitor.Scale, monitor.IsPrimary)).Take(request.Limit).ToArray(),
            };
        if (request.Operation is "search_history" or "get_capture" or "get_capture_region" or "compare_captures" or "read_capture_text")
        {
            if (!settings.Agents.AllowHistoryAccess)
                return AgentResponse.Failure("history_disabled", "Enable Coding agents > Allow history access to expose previous capture files and descriptions.");
            if (request.Operation == "search_history")
                return new AgentResponse { Success = true, Captures = app.History.Search(request.Query, int.MaxValue)
                    .Where(item => AgentAccessPolicy.AllowsApp(settings.Agents, item.AppName)).Skip(request.Offset).Take(request.Limit).Select(Metadata).ToArray() };
            var item = app.History.Items.FirstOrDefault(item => item.Id == request.CaptureId);
            if (item is null) return AgentResponse.Failure("not_found", "The capture ID is no longer in Hotshot history.");
            if (!AgentAccessPolicy.AllowsApp(settings.Agents, item.AppName))
                return AgentResponse.Failure("target_restricted", "The capture's application is outside the allowed-app list.");
            if (request.Operation is "compare_captures" or "read_capture_text")
            {
                if (item.IsVideo) throw new ArgumentException("This tool requires screenshots.");
                var after = await ReadImageAsync(item, token);
                AgentResponse analysisResult;
                string? beforeApp = null;
                if (request.Operation == "read_capture_text")
                    analysisResult = new AgentResponse { Success = true, Text = await Task.Run(() => TextRecognition.RecognizeWithBoundsAsync(after, token), token) };
                else
                {
                    var beforeItem = app.History.Items.FirstOrDefault(capture => capture.Id == request.BeforeId)
                        ?? throw new ArgumentException("The before capture ID is not in history.");
                    if (beforeItem.IsVideo || !AgentAccessPolicy.AllowsApp(settings.Agents, beforeItem.AppName))
                        throw new ArgumentException("The before capture must be an allowed screenshot.");
                    beforeApp = beforeItem.AppName;
                    var before = await ReadImageAsync(beforeItem, token);
                    var diff = await Task.Run(() => ImageComparison.CompareCancellable(before, after, new ComparisonOptions(), request.Tolerance, token), token);
                    var preview = diff.Difference.Downscale(request.MaxImageSize);
                    var png = await Task.Run(() => ImageCodec.EncodePngAsync(preview), token);
                    analysisResult = new AgentResponse { Success = true, Comparison = new AgentComparison(beforeItem.Id, item.Id,
                        diff.ChangedPixels, diff.ChangedPercent, diff.ChangedBounds is { } rect ? Rect(rect) : null, request.Tolerance),
                        Capture = new AgentCapture("difference", "", "Difference", after.Width, after.Height, DateTimeOffset.Now,
                            null, null, null, Convert.ToBase64String(png), preview.Width, preview.Height) };
                }
                token.ThrowIfCancellationRequested();
                if (!HasAccess || !app.Settings.Agents.AllowHistoryAccess ||
                    !AgentAccessPolicy.AllowsApp(app.Settings.Agents, item.AppName) ||
                    request.Operation == "compare_captures" && !AgentAccessPolicy.AllowsApp(app.Settings.Agents, beforeApp))
                    return AgentResponse.Failure("access_disabled", "Access changed during processing.");
                return analysisResult;
            }
            var result = await ResultAsync(item, request, token);
            token.ThrowIfCancellationRequested();
            if (!HasAccess || !app.Settings.Agents.AllowHistoryAccess || !AgentAccessPolicy.AllowsApp(app.Settings.Agents, item.AppName))
                return AgentResponse.Failure("access_disabled", "Agent or history access was disabled during the request.");
            return new AgentResponse { Success = true, Capture = result };
        }
        EnsureDesktopAccessible();
        PixelRect bounds;
        CaptureKind kind;
        CaptureSource source;
        nint targetWindow = 0;
        if (request.Operation == "capture_window")
        {
            var window = Windows().FirstOrDefault(window => WindowId(window.Handle) == request.WindowId)
                ?? throw new ArgumentException("The window ID is not visible or no longer exists. List windows again.");
            if (!AgentAccessPolicy.AllowsApp(settings.Agents, WindowEnumerator.GetProcessName(window.ProcessId)))
                return AgentResponse.Failure("target_restricted", "This application is not allowed.");
            bounds = window.Bounds.Intersect(Monitors.VirtualScreen);
            kind = CaptureKind.Window;
            source = CaptureSource.Now(WindowEnumerator.GetProcessName(window.ProcessId), window.Title);
            targetWindow = window.Handle;
        }
        else if (request.Operation == "capture_monitor")
        {
            if (!AgentAccessPolicy.AllowsMonitor(settings.Agents)) return AgentResponse.Failure("target_restricted", "Monitor capture is disabled or an app restriction is active.");
            var monitor = Monitors.GetAll().FirstOrDefault(monitor => monitor.Index == request.MonitorIndex)
                ?? throw new ArgumentException("Monitor index does not exist.");
            bounds = monitor.Bounds;
            kind = CaptureKind.Monitor;
            source = CaptureSource.Now(monitor: monitor.Index);
        }
        else
        {
            if (!AgentAccessPolicy.AllowsRegion(settings.Agents)) return AgentResponse.Failure("target_restricted", "Region capture is disabled or an app restriction is active.");
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
                if (!HasAccess || isUnavailable()) return false;
                EnsureDesktopAccessible();
                if (request.Operation == "capture_window")
                    return Windows().Any(window => WindowId(window.Handle) == request.WindowId &&
                        window.Bounds.Intersect(Monitors.VirtualScreen) == bounds &&
                        AgentAccessPolicy.AllowsApp(app.Settings.Agents, WindowEnumerator.GetProcessName(window.ProcessId)));
                if (request.Operation == "capture_monitor")
                    return AgentAccessPolicy.AllowsMonitor(app.Settings.Agents) && Monitors.GetAll().Any(monitor => monitor.Index == request.MonitorIndex && monitor.Bounds == bounds);
                return AgentAccessPolicy.AllowsRegion(app.Settings.Agents) && bounds.Intersect(Monitors.VirtualScreen) == bounds;
            }, token, targetWindow);
            var preview = await Task.Run(async () =>
            {
                var scaled = image.Downscale(request.MaxImageSize);
                var png = await ImageCodec.EncodePngAsync(scaled);
                return Metadata(item) with { ImageBase64 = Convert.ToBase64String(png), ImageWidth = scaled.Width, ImageHeight = scaled.Height };
            }, token);
            token.ThrowIfCancellationRequested();
            if (!HasAccess || request.Operation == "capture_window" && !AgentAccessPolicy.AllowsApp(app.Settings.Agents, source.AppName) ||
                request.Operation == "capture_monitor" && !AgentAccessPolicy.AllowsMonitor(app.Settings.Agents) ||
                request.Operation == "capture_region" && !AgentAccessPolicy.AllowsRegion(app.Settings.Agents))
                return AgentResponse.Failure("access_disabled", "Agent access changed during capture.");
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
            var image = request.Operation == "get_capture_region" ? await ReadImageAsync(item, token) : await ImageCodec.LoadAsync(item.Path, request.MaxImageSize);
            AgentRect? imageRegion = null;
            if (request.Operation == "get_capture_region")
            {
                imageRegion = request.Region!;
                image = image.Crop(new PixelRect(imageRegion.X, imageRegion.Y, imageRegion.Width, imageRegion.Height)).Downscale(request.MaxImageSize);
            }
            var png = await ImageCodec.EncodePngAsync(image);
            return result with { ImageBase64 = Convert.ToBase64String(png), ImageWidth = image.Width, ImageHeight = image.Height, ImageRegion = imageRegion };
        }, token);
    }

    private static AgentCapture Metadata(HistoryItem item) =>
        new(item.Id, item.Path, item.Kind.ToString(), item.Width, item.Height, item.CreatedAt, item.Summary, item.Description, item.RecognizedText);
    private IReadOnlyList<WindowInfo> Windows() => WindowEnumerator.GetVisibleWindows((uint)Environment.ProcessId, settingsWindow?.Invoke() ?? 0);
    private static async Task<CapturedImage> ReadImageAsync(HistoryItem item, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (new FileInfo(item.Path).Length > 64 * 1024 * 1024) throw new IOException("Image exceeds 64 MB.");
            var size = await ImageCodec.GetSizeAsync(item.Path);
            if ((long)size.Width * size.Height > 64_000_000) throw new IOException("Image exceeds 64 megapixels.");
            return await ImageCodec.LoadAsync(item.Path);
        }, token);
    }
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
