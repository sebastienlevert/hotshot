using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using GitHub.Copilot;
using Hotshot.Core.Descriptions;
using Hotshot.Descriptions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hotshot.Views;

internal sealed partial class SettingsWindow
{
    private readonly Func<CancellationToken, Task<ICopilotSignInClient>>? _copilotAccountFactory;
    private readonly Func<Uri, CancellationToken, Task>? _copilotBrowser;
    private CancellationTokenSource? _copilotAccountCancellation;
    private Task? _copilotAccountTask;
    private TextBlock? _copilotAccountStatus;
    private Button? _copilotSignIn;
    private Button? _copilotCheckAccount;
    private Button? _copilotCancelSignIn;
    private ProgressBar? _copilotAccountProgress;
    private string _copilotAccountMessage = "Sign in here, or check an existing GitHub/Copilot sign-in. No separate CLI install is required.";
    public bool IsCopilotAccountBusy => _copilotAccountCancellation is not null;

    private void RenderCopilotAccount(StackPanel panel)
    {
        _copilotAccountStatus = UiStyles.Description(_copilotAccountMessage);
        UiStyles.LiveStatus(_copilotAccountStatus, "CopilotAccountStatus");
        _copilotSignIn = new Button { Content = "Sign in with GitHub" };
        _copilotCheckAccount = new Button { Content = "Check sign-in" };
        _copilotCancelSignIn = new Button { Content = "Cancel sign-in" };
        _copilotAccountProgress = new ProgressBar { IsIndeterminate = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_copilotSignIn, "CopilotSignIn");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_copilotCheckAccount, "CopilotCheckSignIn");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_copilotCancelSignIn, "CopilotCancelSignIn");
        var actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(_copilotSignIn);
        actions.Children.Add(_copilotCheckAccount);
        actions.Children.Add(_copilotCancelSignIn);
        Card(panel, "Copilot account", _copilotAccountStatus, actions);
        panel.Children.Add(_copilotAccountProgress);
        Note(panel, "Your browser handles GitHub authorization with PKCE. The Copilot runtime owns credentials in Windows secure storage; Hotshot never puts tokens in settings, OneDrive or logs. " +
            "Sign-in does not enable descriptions, change review/exclusion rules, grant coding-agent access, or send screenshots/model prompts. A GitHub Copilot subscription and an image-capable model are still required for descriptions.");
        _copilotSignIn.Click += async (_, _) => await ChangeCopilotAccountAsync(signIn: true);
        _copilotCheckAccount.Click += async (_, _) => await ChangeCopilotAccountAsync(signIn: false);
        _copilotCancelSignIn.Click += (_, _) => _copilotAccountCancellation?.Cancel();
        RefreshCopilotAccountActions();
    }

    private void RefreshCopilotAccountActions()
    {
        var busy = IsCopilotAccountBusy || IsAiComponentBusy || _app.Descriptions?.IsBusy == true || IsQuickStartBusy;
        if (_copilotSignIn is not null) _copilotSignIn.IsEnabled = !busy;
        if (_copilotCheckAccount is not null) _copilotCheckAccount.IsEnabled = !busy;
        if (_copilotCancelSignIn is not null)
        {
            _copilotCancelSignIn.IsEnabled = IsCopilotAccountBusy;
            _copilotCancelSignIn.Visibility = IsCopilotAccountBusy ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_copilotAccountProgress is not null)
            _copilotAccountProgress.Visibility = IsCopilotAccountBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task ChangeCopilotAccountAsync(bool signIn)
    {
        if (_closed || IsCopilotAccountBusy) return _copilotAccountTask ?? Task.CompletedTask;
        if (IsAiComponentBusy || _app.Descriptions?.IsBusy == true || IsQuickStartBusy)
        {
            SetCopilotAccountMessage("Wait for active analysis, setup or component operations before changing the Copilot account.");
            return Task.CompletedTask;
        }
        var cancellation = new CancellationTokenSource(signIn ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(45));
        _copilotAccountCancellation = cancellation;
        if (_error.Title == "Copilot account") _error.IsOpen = false;
        RefreshCopilotAccountActions();
        RefreshAiComponentActions();
        _copilotAccountTask = RunCopilotAccountAsync(signIn, cancellation);
        return _copilotAccountTask;
    }

    private async Task RunCopilotAccountAsync(bool signIn, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        try
        {
            SetCopilotAccountMessage(signIn ? "Preparing secure GitHub sign-in..." : "Checking authentication only; no screenshot or model request.");
            async Task RunAsync()
            {
                var factory = _copilotAccountFactory ?? (token => CopilotAccountClient.ConnectAsync(
                    Path.Combine(_app.Paths.DataDirectory, "copilot-work"),
                    (_aiComponent ?? throw new InvalidOperationException("The Copilot component is not initialized.")).ResolveRuntime, token));
                var acceptsProgress = true;
                CopilotAccountStatus status;
                try
                {
                    await using var client = await factory(cancellation.Token);
                    status = signIn
                        ? await CopilotSignIn.RunAsync(client, _copilotBrowser ?? OpenCopilotBrowserAsync, SelectCopilotAccountAsync,
                            new Progress<string>(message =>
                            {
                                if (acceptsProgress && ReferenceEquals(_copilotAccountCancellation, cancellation)) SetCopilotAccountMessage(message);
                            }), cancellation.Token)
                        : await client.GetStatusAsync(cancellation.Token);
                    acceptsProgress = false;
                }
                finally { acceptsProgress = false; }
                if (signIn)
                {
                    await using var verification = await factory(cancellation.Token);
                    var persisted = await verification.GetStatusAsync(cancellation.Token);
                    CopilotSignIn.VerifyAccount(status, persisted);
                    status = persisted;
                }
                SetCopilotAccountMessage(status.IsAuthenticated
                    ? $"Signed in{(string.IsNullOrWhiteSpace(status.Login) ? "" : " as " + status.Login)}. Screenshot permissions are unchanged." +
                        (status.Source == "env" ? " An environment credential takes priority over stored sign-ins." : "")
                    : "Not signed in. Choose Sign in with GitHub. Screenshot permissions are unchanged.");
            }
            if (_app.Descriptions is { } descriptions)
                await descriptions.RunProviderMaintenanceAsync(RunAsync);
            else await RunAsync();
        }
        catch (OperationCanceledException)
        {
            SetCopilotAccountMessage("Sign-in cancelled or timed out. Screenshot permissions are unchanged. Check sign-in if GitHub authorization had already completed.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or
            HttpRequestException or JsonException or ArgumentException or TimeoutException or RemoteRpcException or
            AggregateException or Win32Exception or COMException)
        {
            // Authentication exceptions may contain callback addresses or credentials; never log their contents.
            Log.Warn($"Copilot account operation failed ({ex.GetType().Name}).");
            var message = ex is CopilotSignInException ? ex.Message :
                "Could not complete the Copilot account operation. Check the runtime, network and browser, then retry. In Core, install the optional Copilot component first.";
            SetCopilotAccountMessage(message);
            if (!_closed)
            {
                _error.Title = "Copilot account";
                _error.Message = message;
                _error.IsOpen = true;
            }
        }
        finally
        {
            _copilotAccountCancellation = null;
            _copilotAccountTask = null;
            cancellation.Dispose();
            if (!_closed)
            {
                RefreshCopilotAccountActions();
                RefreshAiComponentActions();
                if (!_closing && _copilotSignIn is { IsLoaded: true } button) button.Focus(FocusState.Keyboard);
            }
        }
    }

    private void SetCopilotAccountMessage(string message)
    {
        if (_closed) return;
        _copilotAccountMessage = message;
        if (_copilotAccountStatus is not null) _copilotAccountStatus.Text = message;
    }

    private static async Task OpenCopilotBrowserAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Windows.System.Launcher.LaunchUriAsync(uri))
            throw new InvalidOperationException("The default browser could not be opened.");
    }

    private async Task<string?> SelectCopilotAccountAsync(IReadOnlyList<CopilotAccountChoice> accounts, CancellationToken cancellationToken)
    {
        var choices = new ComboBox { ItemsSource = accounts.Select(account => account.Login).ToArray(), PlaceholderText = "Choose your GitHub account" };
        var dialog = new ContentDialog
        {
            Title = "Choose the GitHub account for Copilot", Content = choices, PrimaryButtonText = "Use account",
            CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false, XamlRoot = _navigation.XamlRoot,
        };
        choices.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.SelectedIndex >= 0;
        using var cancellation = cancellationToken.Register(() => _app.Post(dialog.Hide));
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result == ContentDialogResult.Primary && choices.SelectedIndex >= 0 ? accounts[choices.SelectedIndex].SelectionId : null;
    }

    internal async Task CancelCopilotSignInAsync()
    {
        _copilotAccountCancellation?.Cancel();
        if (_copilotAccountTask is { } task) await task;
    }
}
