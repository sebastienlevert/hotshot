namespace Hotshot.Core.Descriptions;

public sealed record CopilotAccountStatus(bool IsAuthenticated, string? Login = null, string? Host = null, string? Source = null);
public sealed record CopilotAccountChoice(string SelectionId, string Login, string Host);
public enum CopilotSignInStepKind { OpenBrowser, Awaiting, Completed, SelectAccount, PlaintextRequired, Failed }
public sealed record CopilotSignInStep(CopilotSignInStepKind Kind)
{
    public string? Url { get; init; }
    public CopilotAccountStatus? Account { get; init; }
    public IReadOnlyList<CopilotAccountChoice> Accounts { get; init; } = [];
}
public sealed record CopilotSignInStart(string FlowId, CopilotSignInStep Step);
public enum CopilotSignInError { AuthorizationFailed, AccountNotVerified, SecureStorageUnavailable, RuntimeUnsupported }
public sealed class CopilotSignInException(CopilotSignInError error) : InvalidOperationException(error switch
{
    CopilotSignInError.AccountNotVerified => "Could not verify your GitHub account. Try signing in again.",
    CopilotSignInError.SecureStorageUnavailable => "Windows cannot save your sign-in securely. Hotshot will not save it.",
    CopilotSignInError.RuntimeUnsupported => "This Copilot installation cannot sign in. Use Hotshot's Copilot.",
    _ => "GitHub sign-in did not finish. Try again.",
})
{
}

/// <summary>Credential-free access to one owned runtime connection and its interactive login.</summary>
public interface ICopilotSignInClient : IAsyncDisposable
{
    Task<CopilotAccountStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<CopilotSignInStart> BeginAsync(CancellationToken cancellationToken);
    Task<CopilotSignInStep> AdvanceAsync(string flowId, string? selectionId, CancellationToken cancellationToken);
    Task CancelAsync(string flowId, CancellationToken cancellationToken);
}

public static class CopilotSignIn
{
    public static async Task<CopilotAccountStatus> RunAsync(ICopilotSignInClient client,
        Func<Uri, CancellationToken, Task> openBrowser,
        Func<IReadOnlyList<CopilotAccountChoice>, CancellationToken, Task<string?>> selectAccount,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(openBrowser);
        ArgumentNullException.ThrowIfNull(selectAccount);
        string? flowId = null;
        var completed = false;
        Exception? primaryFailure = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var start = await client.BeginAsync(deadline.Token);
            if (string.IsNullOrWhiteSpace(start.FlowId))
                throw new InvalidDataException("The Copilot runtime did not return an owned sign-in flow.");
            flowId = start.FlowId;
            var step = start.Step;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string? selection = null;
                switch (step.Kind)
                {
                    case CopilotSignInStepKind.OpenBrowser:
                        var url = AuthorizationUri(step.Url);
                        progress?.Report("Finish sign-in in your browser.");
                        await openBrowser(url, deadline.Token);
                        break;
                    case CopilotSignInStepKind.Awaiting:
                        progress?.Report("Waiting for sign-in...");
                        await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
                        break;
                    case CopilotSignInStepKind.SelectAccount:
                        if (step.Accounts.Count == 0 || step.Accounts.Any(account =>
                            string.IsNullOrWhiteSpace(account.SelectionId) || string.IsNullOrWhiteSpace(account.Login) ||
                            !IsGitHubHost(account.Host)) ||
                            step.Accounts.Select(account => account.SelectionId).Distinct(StringComparer.Ordinal).Count() != step.Accounts.Count)
                            throw new InvalidDataException("GitHub returned an unsupported account selection.");
                        selection = await selectAccount(step.Accounts, deadline.Token);
                        if (selection is null) throw new OperationCanceledException("Account selection cancelled.", deadline.Token);
                        if (!step.Accounts.Any(account => account.SelectionId == selection))
                            throw new InvalidDataException("The selected account was not offered by this sign-in flow.");
                        break;
                    case CopilotSignInStepKind.Completed:
                        var expected = step.Account;
                        var actual = await client.GetStatusAsync(deadline.Token);
                        VerifyAccount(expected, actual);
                        completed = true;
                        return actual;
                    case CopilotSignInStepKind.PlaintextRequired:
                        throw new CopilotSignInException(CopilotSignInError.SecureStorageUnavailable);
                    case CopilotSignInStepKind.Failed:
                        throw new CopilotSignInException(CopilotSignInError.AuthorizationFailed);
                    default:
                        throw new InvalidDataException("The Copilot runtime returned an unsupported sign-in step.");
                }
                step = await client.AdvanceAsync(flowId, selection, deadline.Token);
            }
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            if (flowId is not null && !completed)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.CancelAsync(flowId, cleanup.Token); }
                catch (Exception cleanupFailure) when (primaryFailure is not null)
                {
                    primaryFailure.Data["Hotshot.SignInCancellationError"] = cleanupFailure;
                }
            }
        }
    }

    public static void VerifyAccount(CopilotAccountStatus? expected, CopilotAccountStatus actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (expected is not { IsAuthenticated: true } || string.IsNullOrWhiteSpace(expected.Login) ||
            !IsGitHubHost(expected.Host) || !actual.IsAuthenticated || !IsGitHubHost(actual.Host) ||
            !string.Equals(expected.Login, actual.Login, StringComparison.OrdinalIgnoreCase))
            throw new CopilotSignInException(CopilotSignInError.AccountNotVerified);
    }

    public static Uri AuthorizationUri(string? value)
    {
        if (value is null || value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/login/oauth/authorize")
            throw new InvalidDataException("The Copilot runtime returned an untrusted GitHub authorization address.");
        return uri;
    }

    private static bool IsGitHubHost(string? host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
        Uri.TryCreate(host, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
