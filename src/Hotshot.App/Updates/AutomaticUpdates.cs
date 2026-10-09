using System.Runtime.InteropServices;
using Hotshot.Core.Updates;
using Microsoft.UI.Dispatching;
using Velopack;
using Velopack.Sources;

namespace Hotshot.Updates;

internal sealed class AutomaticUpdates : IDisposable
{
    public const string RepositoryUrl = ReleaseDownloads.RepositoryUrl;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<bool> _canRestart;
    private readonly Func<VelopackAsset, Task<bool>> _apply;
    private readonly DispatcherQueueTimer _checkTimer;
    private readonly DispatcherQueueTimer _applyTimer;
    private readonly CancellationTokenSource _stop = new();
    private readonly UpdateManager _manager;
    private VelopackAsset? _pending;
    private bool _checking;
    private bool _applying;
    private bool _disposed;

    public AutomaticUpdates(DispatcherQueue dispatcher, Func<bool> canRestart, Func<VelopackAsset, Task<bool>> apply)
    {
        _dispatcher = dispatcher;
        _canRestart = canRestart;
        _apply = apply;
        Channel = UpdateChannel.ForArchitecture(RuntimeInformation.ProcessArchitecture);
        _manager = new UpdateManager(new GithubReleaseSource(),
            new UpdateOptions { ExplicitChannel = Channel, AllowVersionDowngrade = false });
        _checkTimer = dispatcher.CreateTimer();
        _checkTimer.Interval = TimeSpan.FromHours(1);
        _checkTimer.Tick += async (_, _) => await CheckAsync();
        _applyTimer = dispatcher.CreateTimer();
        _applyTimer.Interval = TimeSpan.FromSeconds(30);
        _applyTimer.Tick += async (_, _) => await TryApplyAsync();
        Status = _manager.IsInstalled
            ? "Automatic updates are enabled."
            : "This development/unmanaged build cannot update itself. Install Hotshot from GitHub Releases.";
    }

    public string Channel { get; }
    public string Status { get; private set; }
    public bool IsManaged => _manager.IsInstalled;
    public bool IsChecking => _checking;
    public event Action? StatusChanged;

    public void Start()
    {
        if (!IsManaged) { Log.Info(Status); return; }
        _pending = _manager.UpdatePendingRestart;
        _checkTimer.Start();
        if (_pending is not null) _applyTimer.Start();
        _ = InitialCheckAsync();
    }

    private async Task InitialCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token);
            await CheckAsync();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task CheckAsync()
    {
        if (_disposed || _checking || _applying || !IsManaged) return;
        if (_pending is not null) { await TryApplyAsync(); return; }
        _checking = true;
        SetStatus("Checking GitHub Releases...");
        try
        {
            var update = await _manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromMinutes(2), _stop.Token);
            if (update is null)
            {
                SetStatus($"Hotshot {_manager.CurrentVersion} is up to date.");
                return;
            }
            Log.Info($"Downloading update {update.TargetFullRelease.Version} ({Channel}).");
            SetStatus($"Downloading Hotshot {update.TargetFullRelease.Version}...");
            await _manager.DownloadUpdatesAsync(update,
                percent => SetStatus($"Downloading Hotshot {update.TargetFullRelease.Version}: {percent}%"), _stop.Token);
            _pending = update.TargetFullRelease;
            Log.Info($"Update {_pending.Version} downloaded and verified; waiting for idle.");
            SetStatus($"Hotshot {_pending.Version} is ready. It will restart automatically when idle.");
            _applyTimer.Start();
            await TryApplyAsync();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error("Automatic update check/download failed", ex);
            SetStatus("Updates could not be checked or downloaded. Hotshot will retry automatically; see the diagnostic log.");
        }
        finally
        {
            _checking = false;
            if (!_disposed) StatusChanged?.Invoke();
        }
    }

    private async Task TryApplyAsync()
    {
        if (_disposed || _applying || _pending is not { } pending || !_canRestart()) return;
        _applying = true;
        try
        {
            if (await _apply(pending))
            {
                _pending = null;
                _applyTimer.Stop();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Automatic update restart failed", ex);
            SetStatus("The update is downloaded but could not be applied. Hotshot will retry automatically.");
            _applyTimer.Interval = TimeSpan.FromMinutes(5);
        }
        finally { _applying = false; }
    }

    public void ScheduleRestart(VelopackAsset release) =>
        _manager.WaitExitThenApplyUpdates(release, silent: true, restart: true, restartArgs: ["--background"]);

    private void SetStatus(string status)
    {
        if (_disposed) return;
        if (_dispatcher.HasThreadAccess)
        {
            Status = status;
            StatusChanged?.Invoke();
        }
        else if (!_dispatcher.TryEnqueue(() => { if (!_disposed) { Status = status; StatusChanged?.Invoke(); } }))
        {
            Log.Warn("Update status could not reach the dispatcher.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _checkTimer.Stop();
        _applyTimer.Stop();
        _stop.Cancel();
    }
}
