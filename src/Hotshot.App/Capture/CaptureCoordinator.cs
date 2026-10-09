using System.Diagnostics;
using Hotshot.Capture;
using Hotshot.Core;
using Hotshot.Core.History;

namespace Hotshot.CaptureFlow;

/// <summary>Runs the screenshot flows (region / monitor / all monitors / repeat last region).</summary>
internal sealed class CaptureCoordinator(AppServices app, OutputPipeline pipeline)
{
    private bool _busy;
    private PixelRect? _lastRegion;

    public bool IsBusy => _busy;

    /// <summary>Raised on the UI thread after a screenshot was saved.</summary>
    public event Action<HistoryItem>? Captured;
    public event Action<string>? Failed;

    public Task CaptureRegionAsync() => RunAsync(async () =>
    {
        var started = Stopwatch.GetTimestamp();
        var (appName, title) = WindowEnumerator.GetForegroundInfo();
        var capture = app.Settings.Capture;
        using var snapshot = ScreenSnapshot.Take();
        var overlayTask = CaptureOverlay.SelectAsync(snapshot, new OverlayOptions(OverlayMode.Screenshot,
            capture.SnapToWindows, capture.ShowMagnifier, capture.ShowCrosshair));
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

        var image = snapshot.Crop(selection.Rect);
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

        _busy = true;
        try
        {
            using var snapshot = ScreenSnapshot.Take();
            var capture = app.Settings.Capture;
            return await CaptureOverlay.SelectAsync(snapshot, new OverlayOptions(OverlayMode.Recording,
                capture.SnapToWindows, capture.ShowMagnifier, capture.ShowCrosshair));
        }
        finally
        {
            _busy = false;
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

        _busy = true;
        try
        {
            await flow();
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed", ex);
            Failed?.Invoke($"Capture failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private static int MonitorIndexOf(PixelRect rect) => Monitors.FromRect(rect).Index;
}
