using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Views;

namespace Hotshot;

internal enum SettingsPage
{
    General,
    Hotkeys,
    Naming,
    Capture,
    Recording,
    Gif,
    About,
}

internal sealed partial class AppController
{
    private SettingsWindow? _settingsWindow;
    private WorkspaceWindow? _historyWindow;

    private void InitializeViews()
    {
        _actions.EditHandler = async item =>
        {
            ShowHistory();
            await _historyWindow!.SelectAsync(item);
        };
    }

    private void ShowHistory()
    {
        if (_historyWindow is null)
        {
            var window = new WorkspaceWindow(_app, _actions, ExecuteFromView);
            _historyWindow = window;
            window.Closed += (_, _) => _historyWindow = null;
        }

        _historyWindow.Show();
    }

    private void ShowSettings(SettingsPage page = SettingsPage.General)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_app, _hotkeys);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.SelectPage(page);
    }

    private void ExecuteFromView(Shell.AppCommand command)
    {
        if (command == Shell.AppCommand.Settings) { ShowSettings(); return; }
        Delayed(() =>
        {
            Execute(command, secondInstance: false);
            return Task.CompletedTask;
        });
    }

    private void CloseViews()
    {
        _historyWindow?.CloseForShutdown();
        _settingsWindow?.Close();
    }

    private void ApplyViewTheme()
    {
        UiStyles.ApplyTheme(_settingsWindow, _app.Settings.General.Theme);
        UiStyles.ApplyTheme(_historyWindow, _app.Settings.General.Theme);
        UiStyles.ApplyTheme(_recordingWindow, _app.Settings.General.Theme);
    }
}
