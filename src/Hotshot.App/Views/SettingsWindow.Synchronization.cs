using Hotshot.Core.Settings;
using Hotshot.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.Windows.Storage.Pickers;

namespace Hotshot.Views;

internal sealed partial class SettingsWindow
{
    private TextBlock? _syncDetails;
    private Button? _syncKeepLocal;
    private Button? _syncUseShared;

    private void SettingsSynchronization(StackPanel panel)
    {
        Section(panel, "OneDrive");
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
        var use = new Button { Content = "Use OneDrive", IsEnabled = folders.Length > 0 };
        use.Click += async (_, _) =>
        {
            if (choice.SelectedItem is OneDriveFolder folder) await ChangeSettingsLocationAsync(SettingsSyncMode.OneDrive, folder.Path);
        };
        body.Children.Add(use);
        var choose = new Button { Content = "Choose OneDrive folder" };
        choose.Click += async (_, _) =>
        {
            var selected = await new FolderPicker(AppWindow.Id) { Title = "Choose your OneDrive folder" }.PickSingleFolderAsync();
            if (selected is not null) await ChangeSettingsLocationAsync(SettingsSyncMode.OneDrive, selected.Path);
        };
        body.Children.Add(choose);
        var local = new Button { Content = "Keep on this device" };
        local.Click += async (_, _) => await ChangeSettingsLocationAsync(SettingsSyncMode.Local, null);
        body.Children.Add(local);
        var reload = new Button { Content = "Reload settings" };
        reload.Click += async (_, _) =>
        {
            if (HasPendingChanges)
            {
                _error.Title = "Save your changes first";
                _error.Message = "Fix any settings errors before reloading.";
                _error.IsOpen = true;
                return;
            }
            await _app.ReloadSharedSettingsAsync(manual: true);
        };
        body.Children.Add(reload);
        var resolveMine = _syncKeepLocal = new Button { Content = "Keep this device's settings" };
        resolveMine.Click += async (_, _) => await ResolveSharedConflictAsync(true);
        var resolveRemote = _syncUseShared = new Button { Content = "Use OneDrive's settings" };
        resolveRemote.Click += async (_, _) => await ResolveSharedConflictAsync(false);
        body.Children.Add(resolveMine); body.Children.Add(resolveRemote);
        body.Children.Add(UiStyles.Description("Share preferences across devices. Captures, sign-ins and permissions stay on this device."));
        _syncDetails = UiStyles.Description(string.Empty);
        body.Children.Add(new Expander { Header = "File locations", Content = _syncDetails,
            HorizontalAlignment = HorizontalAlignment.Stretch });
        Card(panel, "Settings across devices", body);
        RefreshSyncStatus();
    }

    private void RefreshSyncStatus()
    {
        if (_closed) return;
        _syncStatus.Text = _app.SettingsSyncStatus;
        if (_syncDetails is not null)
            _syncDetails.Text = "OneDrive: " + (_app.SharedSettingsFile ?? "Not used") + "\nThis device: " + _app.Paths.SettingsFile;
        if (_syncKeepLocal is not null) _syncKeepLocal.Visibility = _app.SettingsSyncConflict ? Visibility.Visible : Visibility.Collapsed;
        if (_syncUseShared is not null) _syncUseShared.Visibility = _app.SettingsSyncConflict ? Visibility.Visible : Visibility.Collapsed;
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
            ? "Keep settings on this device. Your OneDrive copy won't be deleted."
            : "Share preferences through OneDrive. Existing shared settings will be loaded; this device's folders and permissions stay unchanged.";
        if (await new ContentDialog { Title = "Change where settings are kept?", Content = message, PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel", XamlRoot = _navigation.XamlRoot }.ShowAsync() != ContentDialogResult.Primary) return;
        if (!await _app.ConfigureSettingsSyncAsync(mode, root))
        { _error.Title = "Settings location could not be changed"; _error.Message = _app.SettingsSyncStatus; _error.IsOpen = true; }
        RefreshSyncStatus();
    }

    private async Task ResolveSharedConflictAsync(bool local)
    {
        if (!_app.SettingsSyncConflict) { RefreshSyncStatus(); return; }
        var message = local ? "Replace OneDrive's shared preferences with this device's. Folders and permissions won't be shared." :
            "Replace this device's shared preferences with OneDrive's. Folders and permissions won't change.";
        if (await new ContentDialog { Title = "Which settings should be kept?", Content = message, PrimaryButtonText = "Use these settings",
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
