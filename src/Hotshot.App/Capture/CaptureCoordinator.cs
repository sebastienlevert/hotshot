using System.Diagnostics;
using Hotshot.Capture;
using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Views;
using Hotshot.Recording;

namespace Hotshot.CaptureFlow;

/// <summary>Runs the screenshot flows (region / monitor / all monitors / repeat last region).</summary>
internal sealed class CaptureCoordinator(AppServices app, OutputPipeline pipeline, Func<nint>? capturableWindow = null)
{
    private bool _busy;
    private PixelRect? _lastRegion;
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource? _idle;
    private ScrollingCaptureWindow? _scrollingWindow;

    public bool IsBusy => _busy;

    /// <summary>Raised on the UI thread after a screenshot was saved.</summary>
    public event Action<HistoryItem>? Captured;
    public event Action<string>? Failed;

    public async Task<(HistoryItem Item, CapturedImage Image, string[] Warnings)> CaptureForAgentAsync(PixelRect bounds, CaptureKind kind, CaptureSource source,
        Func<bool> accessAllowed, CancellationToken token, nint window = 0)
    {
        if (_busy) throw new InvalidOperationException("Another capture is in progress.");
        SetBusy(true);
        var warnings = new List<string>();
        void Warning(string message) => warnings.Add(message);
        pipeline.Failed += Warning;
        try
        {
            if (app.Settings.Capture.DelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(app.Settings.Capture.DelaySeconds), token);
            token.ThrowIfCancellationRequested();
            if (!accessAllowed()) throw new InvalidOperationException("Agent access was disabled or the app became busy.");
            CapturedImage image;
            if (window != 0 && app.Settings.Capture.UnobscuredWindows)
            {
                var result = await WindowScreenshot.CaptureAsync(window, token, app.Settings.Capture.IncludeCursor);
                image = result.Image;
                warnings.AddRange(result.Warnings);
                if (!accessAllowed()) throw new InvalidOperationException("Target or agent access changed during window capture.");
            }
            else
            {
                using var snapshot = ScreenSnapshot.Take(bounds, app.Settings.Capture.IncludeCursor);
                image = snapshot.ToImage();
            }
            var item = await pipeline.SaveScreenshotAsync(image, kind, source)
                ?? throw new IOException("Hotshot could not save the agent screenshot. See the diagnostic log.");
            Captured?.Invoke(item);
            return (item, image, warnings.ToArray());
        }
        finally
        {
            pipeline.Failed -= Warning;
            SetBusy(false);
        }
    }

    public Task CaptureRegionAsync() => RunAsync(async () =>
    {
        var started = Stopwatch.GetTimestamp();
        var (appName, title) = WindowEnumerator.GetForegroundInfo();
        var capture = app.Settings.Capture;
        using var snapshot = ScreenSnapshot.Take();
        var overlayTask = CaptureOverlay.SelectAsync(snapshot, new OverlayOptions(OverlayMode.Screenshot,
            capture.SnapToWindows, capture.ShowMagnifier, capture.ShowCrosshair, capturableWindow?.Invoke() ?? 0), _shutdown.Token);
        Log.Info($"Region overlay shown in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");

        var selection = await overlayTask;
        if (selection is null)
        {
            return;
        }

        if (capture.IncludeCursor)
        {
            snapshot.DrawCursor();
        }

        CapturedImage image;
        if (selection is WindowSelection selected && capture.UnobscuredWindows)
        {
            var result = await WindowScreenshot.CaptureAsync(selected.Window.Handle, _shutdown.Token, capture.IncludeCursor);
            image = result.Image;
            foreach (var warning in result.Warnings) Log.Warn(warning);
        }
        else image = snapshot.Crop(selection.Rect);
        var (kind, source) = selection switch
        {
            WindowSelection w => (CaptureKind.Window,
                new CaptureSource(DateTime.Now, WindowEnumerator.GetProcessName(w.Window.ProcessId), w.Window.Title, MonitorIndexOf(w.Rect))),
            MonitorSelection m => (CaptureKind.Monitor, new CaptureSource(DateTime.Now, appName, title, m.Monitor.Index)),
            _ => (CaptureKind.Region, new CaptureSource(DateTime.Now, appName, title, MonitorIndexOf(selection.Rect))),
        };

        _lastRegion = selection.Rect;
        await SaveAsync(image, kind, source);
    });

    public Task CaptureMonitorAsync() => RunAsync(async () =>
    {
        var (appName, title) = WindowEnumerator.GetForegroundInfo();
        var monitor = Monitors.FromCursor();
        CapturedImage image;
        using (var snapshot = ScreenSnapshot.Take(monitor.Bounds, app.Settings.Capture.IncludeCursor))
        {
            image = snapshot.ToImage();
        }

        await SaveAsync(image, CaptureKind.Monitor, CaptureSource.Now(appName, title, monitor.Index));
    });

    public Task CaptureAllMonitorsAsync() => RunAsync(async () =>
    {
        var (appName, title) = WindowEnumerator.GetForegroundInfo();
        CapturedImage image;
        using (var snapshot = ScreenSnapshot.Take(null, app.Settings.Capture.IncludeCursor))
        {
            image = snapshot.ToImage();
        }

        await SaveAsync(image, CaptureKind.AllMonitors, CaptureSource.Now(appName, title));
    });

    public Task CaptureScrollingAsync() => RunAsync(async () =>
    {
        using var snapshot = ScreenSnapshot.Take();
        var selection = await CaptureOverlay.SelectAsync(snapshot, new OverlayOptions(OverlayMode.Screenshot, false, true, true), _shutdown.Token);
        if (selection is null) return;
        var includeCursor = app.Settings.Capture.IncludeCursor;
        if (includeCursor) snapshot.DrawCursor();
        var window = new ScrollingCaptureWindow(selection.Rect, snapshot.Crop(selection.Rect), includeCursor);
        _scrollingWindow = window;
        window.Activate();
        var image = await window.Completion;
        _scrollingWindow = null;
        if (image is not null) await SaveAsync(image, CaptureKind.Region, CaptureSource.Now("Scrolling capture"));
    });

    /// <summary>Re-captures the last region without showing the overlay (falls back to the region flow).</summary>
    public Task RepeatLastRegionAsync()
    {
        if (_lastRegion is not { } region || region.Intersect(Monitors.VirtualScreen).IsEmpty)
        {
            return CaptureRegionAsync();
        }

        return RunAsync(async () =>
        {
            var (appName, title) = WindowEnumerator.GetForegroundInfo();
            CapturedImage image;
            using (var snapshot = ScreenSnapshot.Take(region.Intersect(Monitors.VirtualScreen), app.Settings.Capture.IncludeCursor))
            {
                image = snapshot.ToImage();
            }

            await SaveAsync(image, CaptureKind.Region, CaptureSource.Now(appName, title, MonitorIndexOf(region)));
        });
    }

    /// <summary>Shows the overlay in recording mode and returns the chosen target (or null if cancelled).</summary>
    public async Task<OverlaySelection?> SelectRecordingTargetAsync()
    {
        if (_busy)
        {
            return null;
        }

        SetBusy(true);
        try
        {
            using var snapshot = ScreenSnapshot.Take();
            var capture = app.Settings.Capture;
            return await CaptureOverlay.SelectAsync(snapshot, new OverlayOptions(OverlayMode.Recording,
                capture.SnapToWindows, capture.ShowMagnifier, capture.ShowCrosshair), _shutdown.Token);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveAsync(CapturedImage image, CaptureKind kind, CaptureSource source)
    {
        var item = await pipeline.SaveScreenshotAsync(image, kind, source);
        if (item is not null)
        {
            Captured?.Invoke(item);
        }
    }

    private async Task RunAsync(Func<Task> flow)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (app.Settings.Capture.DelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(app.Settings.Capture.DelaySeconds), _shutdown.Token);
            _shutdown.Token.ThrowIfCancellationRequested();
            await flow();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error("Capture failed", ex);
            Failed?.Invoke($"Capture failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static int MonitorIndexOf(PixelRect rect) => Monitors.FromRect(rect).Index;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        else { _idle?.TrySetResult(); _idle = null; }
    }

    public async Task ShutdownAsync()
    {
        _shutdown.Cancel();
        _scrollingWindow?.RequestClose();
        if (_idle is { } idle) await idle.Task;
    }
}
