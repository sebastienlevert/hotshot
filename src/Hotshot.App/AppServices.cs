using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Core.Settings;
using Microsoft.UI.Dispatching;
using Hotshot.Updates;
using Hotshot.Core.Descriptions;
using Hotshot.CaptureFlow;
using Hotshot.Agents;
using Hotshot.Settings;
using Hotshot.Descriptions;

namespace Hotshot;

/// <summary>Process-wide state shared by the controller, capture flows and windows. UI-thread affine.</summary>
internal sealed class AppServices : IDisposable
{
    private readonly RoamingSettingsStore _settingsStore;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly DispatcherQueueTimer _syncTimer;
    private bool _draftSyncConflict;
    private AppSettings? _deferredSharedSettings;
    private AppSettings? _deferredSharedBaseline;
    private bool _disposed;

    public AppServices(AppPaths paths, DispatcherQueue dispatcher, string? sharedSettingsRoot = null)
    {
        Paths = paths;
        Dispatcher = dispatcher;
        paths.EnsureCreated();
        var testLocal = Environment.GetEnvironmentVariable("HOTSHOT_SETTINGS_SYNC_MODE") == "local";
        Func<string?> discovery = sharedSettingsRoot is not null ? () => sharedSettingsRoot :
            paths.DataDirectory.Equals(AppPaths.Default.DataDirectory, StringComparison.OrdinalIgnoreCase) ? OneDriveFolders.GetDefault : () => null;
        _settingsStore = new RoamingSettingsStore(paths.SettingsFile, discovery, forceLocal: testLocal);
        _settingsStore.StatusChanged += () => Post(() => SettingsSyncStatusChanged?.Invoke());
        _settingsStore.Failed += message => Post(() => { Log.Warn(message); Failed?.Invoke(message); });
        Settings = _settingsStore.Load();
        History = new HistoryStore(paths.HistoryFile, Settings.General.HistorySize, Settings.Library.KeepSavedCaptures);
        History.Failed += message => Post(() => { Log.Warn(message); Failed?.Invoke(message); });
        History.Load();

        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await SaveAsync();
        _syncTimer = dispatcher.CreateTimer();
        _syncTimer.Interval = TimeSpan.FromSeconds(5);
        _syncTimer.Tick += async (_, _) => await ReloadSharedSettingsAsync();
        _syncTimer.Start();
    }

    public AppPaths Paths { get; }

    public DispatcherQueue Dispatcher { get; }

    public AppSettings Settings { get; private set; }

    public HistoryStore History { get; }
    public AutomaticUpdates? Updates { get; set; }
    public ScreenshotDescriptions? Descriptions { get; set; }
    public CopilotRuntimeManager? CopilotRuntime { get; set; }
    public LocalTextIndex? TextIndex { get; set; }
    public OutputPipeline? Output { get; set; }
    public AgentCaptureServer? AgentServer { get; set; }
    public int AuxiliaryWindowCount { get; set; }
    public bool IsSaving => _saveGate.CurrentCount == 0 || _saveTimer.IsRunning;
    public string SettingsSyncStatus => _settingsStore.Status;
    public string? SharedSettingsFile => _settingsStore.SharedFilePath;
    public bool SettingsSyncConflict => _settingsStore.HasConflict || _draftSyncConflict;
    public SettingsSyncMode SettingsSyncMode => _settingsStore.Mode;
    public Func<bool> CanChangeSharedSettings { get; set; } = () => true;
    public Func<bool> CanReloadSharedSettings { get; set; } = () => true;

    /// <summary>HWND that owns clipboard data and receives hotkeys/tray messages.</summary>
    public nint MessageWindowHandle { get; set; }

    /// <summary>Raised (debounced) after settings were changed and persisted.</summary>
    public event Action? SettingsChanged;
    public event Action<string>? Failed;
    public event Action? SettingsSyncStatusChanged;
    public event Action? SharedSettingsReloaded;

    /// <summary>Schedules a debounced save; settings pages call this on every edit.</summary>
    public void SaveSettings()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public async Task<bool> SaveAsync()
    {
        _saveTimer.Stop();
        await _saveGate.WaitAsync();
        try
        {
            Settings.Normalize();
            var snapshot = Settings.Clone();
            snapshot = MergeDeferredSharedSettings(snapshot);
            var saved = await Task.Run(() => _settingsStore.Save(snapshot));
            saved.General.Counter = Math.Max(saved.General.Counter, Settings.General.Counter);
            saved.Layout = Settings.Layout;
            Settings = saved;
            ClearDeferredSharedSettings();
            _draftSyncConflict = false;
            History.MaxItems = Settings.General.HistorySize;
            History.KeepSavedCaptures = Settings.Library.KeepSavedCaptures;
            SettingsChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            if (ex is SettingsSyncConflictException) _draftSyncConflict = true;
            Log.Error("Could not save settings", ex);
            Failed?.Invoke($"Could not save settings: {ex.Message}");
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task<bool> ApplySettingsAsync(AppSettings draft, AppSettings? baseline = null)
    {
        await _saveGate.WaitAsync();
        try
        {
            var candidate = draft.Clone();
            if (baseline is not null)
                candidate = PortableSettingsMerge.Merge(PortableSettings.From(baseline), PortableSettings.From(candidate),
                    PortableSettings.From(Settings)).ApplyTo(candidate);
            candidate.General.Counter = Settings.General.Counter;
            candidate.General.FirstRunCompleted = Settings.General.FirstRunCompleted;
            candidate.Library.SavedSearches = Settings.Library.SavedSearches.ToList();
            candidate.Library.Baselines = Settings.Library.Baselines.ToList();
            candidate.Export = Settings.Export;
            candidate.Layout = Settings.Layout;
            candidate = MergeDeferredSharedSettings(candidate);
            candidate = await Task.Run(() => _settingsStore.Save(candidate));
            var counter = Math.Max(candidate.General.Counter, Settings.General.Counter);
            Settings = candidate;
            ClearDeferredSharedSettings();
            _draftSyncConflict = false;
            if (counter != candidate.General.Counter)
            {
                candidate.General.Counter = counter;
                SaveSettings();
            }

            History.MaxItems = Settings.General.HistorySize;
            History.KeepSavedCaptures = Settings.Library.KeepSavedCaptures;
            await Task.Run(History.Prune);
            SettingsChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            if (ex is SettingsSyncConflictException) _draftSyncConflict = true;
            Log.Error("Could not apply settings", ex);
            Failed?.Invoke($"Could not save settings: {ex.Message}");
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task CheckpointHistoryAsync()
    {
        try { await Task.Run(History.Checkpoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Library checkpoint failed", ex);
            Failed?.Invoke("Library checkpoint failed; the journal was retained: " + ex.Message);
        }

    }

    public async Task ReloadSharedSettingsAsync(bool manual = false)
    {
        bool CanReload() => CanChangeSharedSettings() && (manual || CanReloadSharedSettings());
        if (!CanReload())
        {
            if (manual) Failed?.Invoke("Shared preferences cannot be reloaded while a capture or recording is active.");
            return;
        }
        if (IsSaving || !await _saveGate.WaitAsync(0)) return;
        try
        {
            var snapshot = Settings.Clone();
            var updated = _deferredSharedSettings is not null
                ? MergeDeferredSharedSettings(snapshot)
                : await Task.Run(() => _settingsStore.Refresh(snapshot));
            if (updated is null) return;
            if (!CanReload())
            {
                _deferredSharedBaseline = snapshot;
                _deferredSharedSettings = updated;
                return;
            }
            updated.General.Counter = Math.Max(updated.General.Counter, Settings.General.Counter);
            updated.Layout = Settings.Layout;
            Settings = updated;
            ClearDeferredSharedSettings();
            _draftSyncConflict = false;
            History.MaxItems = Settings.General.HistorySize;
            History.KeepSavedCaptures = Settings.Library.KeepSavedCaptures;
            SettingsChanged?.Invoke();
            SharedSettingsReloaded?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { Log.Error("Shared settings reload failed", ex); Failed?.Invoke("Shared settings reload failed: " + ex.Message); }
        finally { _saveGate.Release(); }
    }

    public async Task<bool> ConfigureSettingsSyncAsync(SettingsSyncMode mode, string? root, bool? useLocalConflict = null, AppSettings? proposed = null)
    {
        await _saveGate.WaitAsync();
        try
        {
            if (!CanChangeSharedSettings()) throw new InvalidOperationException("Finish the active capture or recording before changing shared preferences.");
            var snapshot = (proposed ?? Settings).Clone();
            if (useLocalConflict is null) snapshot = MergeDeferredSharedSettings(snapshot);
            var updated = await Task.Run(() => useLocalConflict is { } local ? _settingsStore.ResolveConflict(snapshot, local) :
                _settingsStore.Configure(snapshot, mode, root));
            updated.General.Counter = Math.Max(updated.General.Counter, Settings.General.Counter);
            updated.Layout = Settings.Layout;
            Settings = updated;
            ClearDeferredSharedSettings();
            _draftSyncConflict = false;
            History.MaxItems = Settings.General.HistorySize;
            History.KeepSavedCaptures = Settings.Library.KeepSavedCaptures;
            SettingsChanged?.Invoke();
            SharedSettingsReloaded?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException)
        { Log.Error("Settings synchronization could not be configured", ex); Failed?.Invoke(ex.Message); return false; }
        finally { _saveGate.Release(); }
    }

    public void StopSettingsSync() => _syncTimer.Stop();

    public async Task RunObservedAsync(Func<Task> operation, string description)
    {
        try { await operation(); }
        catch (Exception ex)
        {
            Log.Error(description, ex);
            Post(() => Failed?.Invoke($"{description}: {ex.Message}"));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Stop();
        _syncTimer.Stop();
        _saveGate.Dispose();
    }

    private AppSettings MergeDeferredSharedSettings(AppSettings candidate) =>
        _deferredSharedSettings is null || _deferredSharedBaseline is null ? candidate :
        PortableSettingsMerge.Merge(PortableSettings.From(_deferredSharedBaseline), PortableSettings.From(candidate),
            PortableSettings.From(_deferredSharedSettings)).ApplyTo(candidate);

    private void ClearDeferredSharedSettings()
    {
        _deferredSharedBaseline = null;
        _deferredSharedSettings = null;
    }

    public void Post(Action action)
    {
        if (!Dispatcher.TryEnqueue(() => action()))
        {
            Log.Warn("Dispatcher rejected a work item (shutting down?)");
        }
    }
}
