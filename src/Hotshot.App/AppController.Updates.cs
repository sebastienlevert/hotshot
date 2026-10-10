using Hotshot.Core.Updates;
using Hotshot.Interop;
using Hotshot.Updates;
using Microsoft.UI.Xaml;
using Velopack;

namespace Hotshot;

internal sealed partial class AppController
{
    private AutomaticUpdates _updates = null!;

    private void InitializeUpdates()
    {
        _updates = new AutomaticUpdates(_app.Dispatcher, () => UpdateActivity().CanRestart, ApplyUpdateAsync);
        _app.Updates = _updates;
    }

    private UpdateActivity UpdateActivity()
    {
        var foreground = Win32.GetForegroundWindow();
        bool Active(Window? window) => window is not null &&
            WinRT.Interop.WindowNative.GetWindowHandle(window) == foreground;
        return new UpdateActivity(
            Capturing: _capture.IsBusy,
            Recording: IsRecording || _startTask is not null || _stopTask is not null,
            Converting: _conversionTask is not null,
            UnsavedEdits: _historyWindow?.HasUnsavedEdits ?? false,
            EditorBusy: _historyWindow?.IsBusy ?? false,
            ActiveWindow: Active(_historyWindow) || Active(_settingsWindow),
            UnsavedSettings: _app.IsSaving || (_settingsWindow?.HasPendingChanges ?? false),
            Exiting: _exiting,
            Describing: _descriptions.IsBusy);
    }

    private async Task<bool> ApplyUpdateAsync(VelopackAsset release)
    {
        if (!UpdateActivity().CanRestart) return false;
        var paused = _hotkeys.IsPaused;
        _hotkeys.SetPaused(true);
        try
        {
            if (!await _app.SaveAsync()) return false;
            if (!UpdateActivity().CanRestart) return false;
            _exiting = true;
            Log.Info($"Applying update {release.Version} silently.");
            _updates.ScheduleRestart(release);
            await _descriptions.DisposeAsync();
            CloseViews();
            Dispose();
            Application.Current.Exit();
            return true;
        }
        catch
        {
            _exiting = false;
            throw;
        }
        finally
        {
            if (!_exiting) _hotkeys.SetPaused(paused);
        }
    }
}
