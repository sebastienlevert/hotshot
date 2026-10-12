using Hotshot.Core.Settings;
using Hotshot.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.Windows.Storage.Pickers;

namespace Hotshot.Views;

internal sealed partial class SettingsWindow
{
    private void SettingsSynchronization(StackPanel panel)
    {
        Section(panel, "Settings across devices");
        var body = new StackPanel { Spacing = 8 };
        _syncStatus = UiStyles.Description(string.Empty);
        AutomationProperties.SetAutomationId(_syncStatus, "SettingsSyncStatus");
        RefreshSyncStatus();
        body.Children.Add(_syncStatus);
        var folders = OneDriveFolders.GetAvailable().ToArray();
        var choice = new ComboBox { ItemsSource = folders, DisplayMemberPath = "Label", SelectedIndex = folders.Length > 0 ? 0 : -1,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choice, "OneDrive settings folder");
        body.Children.Add(choice);
        var use = new Button { Content = "Use OneDrive preferences", IsEnabled = folders.Length > 0 };
        use.Click += async (_, _) =>
        {
            if (choice.SelectedItem is OneDriveFolder folder) await ChangeSettingsLocationAsync(SettingsSyncMode.OneDrive, folder.Path);
        };
        body.Children.Add(use);
        var choose = new Button { Content = "Choose another OneDrive folder" };
        choose.Click += async (_, _) =>
        {
            var selected = await new FolderPicker(AppWindow.Id) { Title = "Choose the local OneDrive root folder" }.PickSingleFolderAsync();
            if (selected is not null) await ChangeSettingsLocationAsync(SettingsSyncMode.OneDrive, selected.Path);
        };
        body.Children.Add(choose);
        var local = new Button { Content = "Use local preferences only" };
        local.Click += async (_, _) => await ChangeSettingsLocationAsync(SettingsSyncMode.Local, null);
        body.Children.Add(local);
        var reload = new Button { Content = "Reload shared preferences" };
        reload.Click += async (_, _) =>
        {
            if (HasPendingChanges)
            {
                _error.Title = "Settings have unapplied changes";
                _error.Message = "Fix or apply your edits before reloading shared preferences.";
                _error.IsOpen = true;
                return;
            }
            await _app.ReloadSharedSettingsAsync(manual: true);
        };
        body.Children.Add(reload);
        var resolveMine = new Button { Content = "Resolve conflict using this machine's preferences" };
        resolveMine.Click += async (_, _) => await ResolveSharedConflictAsync(true);
        var resolveRemote = new Button { Content = "Resolve conflict using OneDrive preferences" };
        resolveRemote.Click += async (_, _) => await ResolveSharedConflictAsync(false);
        body.Children.Add(resolveMine); body.Children.Add(resolveRemote);
        body.Children.Add(UiStyles.Description("OneDrive stores Apps\\Hotshot\\settings.json. Theme, hotkeys, naming, capture/recording options, saved searches and export presets roam. Paths, layouts, microphone IDs, counters, AI upload/review and agent permissions remain local. Capture files, originals, history and credentials are not synchronized."));
        body.Children.Add(UiStyles.Description("OneDrive's client handles cloud transport. Valid changes reload when this app is idle and Settings is not being edited. Simultaneous offline edits can conflict; local proposals and the shared backup are retained instead of silently choosing a winner."));
        Card(panel, "Settings across devices", body);
    }

    private void RefreshSyncStatus()
    {
        if (_closed) return;
        _syncStatus.Text = _app.SettingsSyncStatus + "\nShared file: " + (_app.SharedSettingsFile ?? "None") +
            "\nLocal cache and device overrides: " + _app.Paths.SettingsFile;
    }

    private void ReloadSharedPreferences()
    {
        if (_closed || HasPendingChanges) return;
        _draft = _app.Settings.Clone(); _draftBaseline = _app.Settings.Clone();
        if (_navigation.SelectedItem is NavigationViewItem { Tag: SettingsPage page }) RenderPage(page);
    }

    private async Task ChangeSettingsLocationAsync(SettingsSyncMode mode, string? root)
    {
        await PersistAsync();
        if (HasPendingChanges) return;
        var message = mode == SettingsSyncMode.Local
            ? "Keep the current preferences on this machine. The existing OneDrive file will not be deleted."
            : $"Store portable preferences under {root}\\Apps\\Hotshot\\settings.json. If a shared file already exists, load it while retaining this machine's paths and permissions.";
        if (await new ContentDialog { Title = "Change settings location?", Content = message, PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel", XamlRoot = _navigation.XamlRoot }.ShowAsync() != ContentDialogResult.Primary) return;
        if (!await _app.ConfigureSettingsSyncAsync(mode, root))
        { _error.Title = "Settings location could not be changed"; _error.Message = _app.SettingsSyncStatus; _error.IsOpen = true; }
        RefreshSyncStatus();
    }

    private async Task ResolveSharedConflictAsync(bool local)
    {
        if (!_app.SettingsSyncConflict) { RefreshSyncStatus(); return; }
        var message = local ? "Replace the shared portable preferences with your current edits on this machine. Device-specific values are not uploaded." :
            "Discard local portable edits and use the current OneDrive preferences. Device paths and permissions remain local.";
        if (await new ContentDialog { Title = "Resolve shared-settings conflict?", Content = message, PrimaryButtonText = "Use selected preferences",
            CloseButtonText = "Cancel", XamlRoot = _navigation.XamlRoot }.ShowAsync() != ContentDialogResult.Primary) return;
        var source = local ? _draft.Clone() : null;
        if (await _app.ConfigureSettingsSyncAsync(_app.SettingsSyncMode, null, useLocalConflict: local, proposed: source))
        {
            _savedVersion = _editVersion;
            ReloadSharedPreferences();
            _error.IsOpen = false;
        }
        RefreshSyncStatus();
    }
}
