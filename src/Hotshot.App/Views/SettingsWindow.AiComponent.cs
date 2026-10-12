using Hotshot.Core.Updates;
using Hotshot.Descriptions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Hotshot.Views;

internal sealed partial class SettingsWindow
{
    private CopilotRuntimeManager? _aiComponent;
    private bool _ownsAiComponent;
    private CancellationTokenSource? _componentCancellation;
    private TextBlock? _componentStatus;
    private Button? _componentInstall;
    private Button? _componentRemove;
    private Button? _componentCancel;
    private ProgressBar? _componentProgress;
    public bool IsAiComponentBusy => _componentCancellation is not null || _aiComponent?.IsBusy == true;

    // Called by the Capture settings page; no setting or permission is changed by these actions.
    private void RenderAiComponent(StackPanel panel, CopilotRuntimeManager? manager = null)
    {
        if (_aiComponent is null)
        {
            _ownsAiComponent = manager is null;
            _aiComponent = manager ?? new CopilotRuntimeManager(_app.Paths.DataDirectory,
                () => _app.Settings.General.CopilotRuntimePath, () => _app.Descriptions?.IsBusy != true);
            _aiComponent.StatusChanged += AiComponentStatusChanged;
        }
        _componentStatus = UiStyles.Description(_aiComponent.Status);
        _componentInstall = new Button { Content = "Install optional Copilot component" };
        _componentRemove = new Button { Content = "Remove optional Copilot component" };
        _componentCancel = new Button { Content = "Cancel download", IsEnabled = false };
        _componentProgress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_componentInstall, "InstallCopilotComponent");
        AutomationProperties.SetAutomationId(_componentRemove, "RemoveCopilotComponent");
        AutomationProperties.SetAutomationId(_componentCancel, "CancelCopilotComponent");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_componentInstall);
        actions.Children.Add(_componentRemove);
        actions.Children.Add(_componentCancel);
        Card(panel, "Optional Copilot component", _componentStatus, actions);
        panel.Children.Add(_componentProgress);
        Note(panel, "Core omits Copilot; Full includes it. Installing explicitly downloads a version-pinned component over HTTPS from official Hotshot GitHub Releases " +
            "(up to 256 MiB downloaded / 512 MiB installed; exact bytes appear during download), checks SHA256, and tests local stdio startup. " +
            "This test does not sign in, query accounts/models, create sessions, or upload images. Screenshot descriptions still require a signed-in GitHub Copilot account " +
            "and can consume your plan's quota. Installation never enables descriptions, changes review/exclusion rules, or grants agent access. " +
            "Your selected external runtime always takes priority. Managed components and their local receipts are separate from OneDrive settings and survive app updates.");
        _componentInstall.Click += async (_, _) => await ChangeAiComponentAsync(install: true);
        _componentRemove.Click += async (_, _) => await ChangeAiComponentAsync(install: false);
        _componentCancel.Click += (_, _) => _componentCancellation?.Cancel();
        RefreshAiComponentActions();
        _ = RefreshAiComponentAsync();
    }

    private async Task RefreshAiComponentAsync()
    {
        try
        {
            await _aiComponent!.RefreshAsync();
            if (!_closed && _componentStatus is not null) _componentStatus.Text = _aiComponent.Status;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            if (!_closed && _componentStatus is not null) _componentStatus.Text = $"Component unavailable: {ex.Message}";
        }
        finally { if (!_closed) RefreshAiComponentActions(); }
    }

    private void RefreshAiComponentActions()
    {
        if (_aiComponent is null) return;
        var busy = _componentCancellation is not null || _aiComponent.IsBusy || _app.Descriptions?.IsBusy == true;
        if (_componentInstall is not null) _componentInstall.IsEnabled = !busy && !_aiComponent.HasBundledRuntime;
        if (_componentRemove is not null) _componentRemove.IsEnabled = !busy && _aiComponent.HasManagedComponent;
        if (_componentCancel is not null) _componentCancel.IsEnabled = _componentCancellation is not null;
    }

    private void AiComponentStatusChanged() => _app.Post(() =>
    {
        if (_closed) return;
        RefreshAiComponentActions();
    });

    private async Task ChangeAiComponentAsync(bool install)
    {
        if (_aiComponent is null || _componentCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _componentCancellation = cancellation;
        RefreshAiComponentActions();
        if (_componentProgress is not null)
        {
            _componentProgress.Visibility = Visibility.Visible;
            _componentProgress.IsIndeterminate = true;
        }
        var progress = new Progress<ComponentProgress>(value => _app.Post(() =>
        {
            if (_closed) return;
            if (_componentStatus is not null) _componentStatus.Text = value.TotalBytes > 0
                ? $"{value.Stage}: {value.Bytes:N0} / {value.TotalBytes:N0} bytes ({value.Percent}%)." : value.Stage;
            if (_componentProgress is not null)
            {
                _componentProgress.IsIndeterminate = value.TotalBytes == 0;
                _componentProgress.Value = value.Percent;
            }
        }));
        try
        {
            if (install) await _aiComponent.InstallAsync(progress, cancellation.Token);
            else if (_app.Descriptions is { } descriptions)
                await descriptions.RunProviderMaintenanceAsync(() => _aiComponent.RemoveAsync(cancellation.Token));
            else await _aiComponent.RemoveAsync(cancellation.Token);
            if (!_closed && _componentStatus is not null) _componentStatus.Text = _aiComponent.Status;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed && _componentStatus is not null) _componentStatus.Text = "Installation cancelled; no partial component is selected. Permissions are unchanged.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or
            System.Text.Json.JsonException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or AggregateException)
        {
            Log.Error("Optional Copilot component operation failed", ex);
            if (!_closed && _componentStatus is not null) _componentStatus.Text = $"Component operation failed: {ex.Message}";
        }
        finally
        {
            _componentCancellation = null;
            if (!_closed)
            {
                if (_componentProgress is not null) _componentProgress.Visibility = Visibility.Collapsed;
                RefreshAiComponentActions();
            }
            else if (_ownsAiComponent) _aiComponent.Dispose();
        }
    }

    internal void CloseAiComponent()
    {
        if (_aiComponent is not null) _aiComponent.StatusChanged -= AiComponentStatusChanged;
        _componentCancellation?.Cancel();
        // Active downloads own the HTTP client until they finish; do not dispose it underneath a request.
        if (_componentCancellation is null && _ownsAiComponent) _aiComponent?.Dispose();
    }
}
