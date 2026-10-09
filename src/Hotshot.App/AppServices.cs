using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Core.Settings;
using Microsoft.UI.Dispatching;
using Hotshot.Updates;

namespace Hotshot;

/// <summary>Process-wide state shared by the controller, capture flows and windows. UI-thread affine.</summary>
internal sealed class AppServices
{
    private readonly SettingsStore _settingsStore;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public AppServices(AppPaths paths, DispatcherQueue dispatcher)
    {
        Paths = paths;
        Dispatcher = dispatcher;
        paths.EnsureCreated();
        _settingsStore = new SettingsStore(paths.SettingsFile);
        Settings = _settingsStore.Load();
        History = new HistoryStore(paths.HistoryFile, Settings.General.HistorySize);
        History.Load();

        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await SaveAsync();
    }

    public AppPaths Paths { get; }

    public DispatcherQueue Dispatcher { get; }

    public AppSettings Settings { get; private set; }

    public HistoryStore History { get; }
    public AutomaticUpdates? Updates { get; set; }
    public bool IsSaving => _saveGate.CurrentCount == 0 || _saveTimer.IsRunning;

    /// <summary>HWND that owns clipboard data and receives hotkeys/tray messages.</summary>
    public nint MessageWindowHandle { get; set; }

    /// <summary>Raised (debounced) after settings were changed and persisted.</summary>
    public event Action? SettingsChanged;
    public event Action<string>? Failed;

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
            History.MaxItems = Settings.General.HistorySize;
            var snapshot = Settings.Clone();
            await Task.Run(() => _settingsStore.Save(snapshot));
            SettingsChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
            Failed?.Invoke($"Could not save settings: {ex.Message}");
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task<bool> ApplySettingsAsync(AppSettings draft)
    {
        await _saveGate.WaitAsync();
        try
        {
            var candidate = draft.Clone();
            candidate.General.Counter = Settings.General.Counter;
            candidate.General.FirstRunCompleted = Settings.General.FirstRunCompleted;
            await Task.Run(() => _settingsStore.Save(candidate));
            var counter = Math.Max(candidate.General.Counter, Settings.General.Counter);
            Settings = candidate;
            if (counter != candidate.General.Counter)
            {
                candidate.General.Counter = counter;
                SaveSettings();
            }

            History.MaxItems = Settings.General.HistorySize;
            await Task.Run(History.Prune);
            SettingsChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply settings", ex);
            Failed?.Invoke($"Could not save settings: {ex.Message}");
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Post(Action action)
    {
        if (!Dispatcher.TryEnqueue(() => action()))
        {
            Log.Warn("Dispatcher rejected a work item (shutting down?)");
        }
    }
}
