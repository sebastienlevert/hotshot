using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Hotshot.Core.Descriptions;

namespace Hotshot.Descriptions;

// The pinned SDK's interactive account contract is experimental; unknown steps fail closed.
#pragma warning disable GHCP001
internal sealed class CopilotAccountClient(CopilotClient client) : ICopilotSignInClient
{
    private CopilotSession? _session;

    public static async Task<ICopilotSignInClient> ConnectAsync(string directory, Func<string> resolveRuntime,
        CancellationToken cancellationToken)
    {
        var client = await Task.Run(() => CopilotConnection.Create(directory, resolveRuntime()), cancellationToken);
        try
        {
            await client.StartAsync(cancellationToken);
            return new CopilotAccountClient(client);
        }
        catch (Exception failure)
        {
            try { await client.DisposeAsync(); }
            catch (Exception cleanupFailure) { failure.Data["Hotshot.SignInConnectionCleanupError"] = cleanupFailure; }
            throw;
        }
    }

    public async Task<CopilotAccountStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_session is not null)
        {
            var value = await _session.Rpc.Accounts.GetAsync(new AuthReadQueryStatus(), cancellationToken);
            if (value is not AuthReadValueStatus status)
                throw new InvalidDataException("The Copilot runtime returned an unsupported account-status response.");
            return new(status.Status.IsAuthenticated, status.Status.ActiveLogin, status.Status.ActiveHost);
        }
        var auth = await client.GetAuthStatusAsync(cancellationToken);
        return new(auth.IsAuthenticated, auth.Login, auth.Host, auth.AuthType);
    }

    public async Task<CopilotSignInStart> BeginAsync(CancellationToken cancellationToken)
    {
        if (_session is not null) throw new InvalidOperationException("A sign-in flow already owns this connection.");
        _session = await client.CreateSessionAsync(new SessionConfig
        {
            AvailableTools = [],
            SkipCustomInstructions = true,
            EnableSkills = false,
            EnableSessionStore = false,
            EnableHostGitOperations = false,
            EnableFileHooks = false,
            OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Account sign-in cannot execute tools.")),
        }, cancellationToken);
        var available = await _session.Rpc.Accounts.EnumerateAsync(new AuthEnumerateQueryProviders { BrokerAvailable = false }, cancellationToken);
        if (available is not AuthEnumerateValueProviders providers ||
            !providers.Items.Any(provider => provider.Kind == LoginProviderKind.GitHubDotCom && provider.Available))
            throw new CopilotSignInException(CopilotSignInError.RuntimeUnsupported);
        var started = await _session.Rpc.Accounts.Login.BeginAsync(LoginProviderKind.GitHubDotCom, cancellationToken);
        return new(started.FlowId, ConvertStep(started.Step));
    }

    public async Task<CopilotSignInStep> AdvanceAsync(string flowId, string? selectionId, CancellationToken cancellationToken) =>
        ConvertStep(await Session.Rpc.Accounts.Login.AdvanceAsync(flowId, selectionId, cancellationToken));

    public Task CancelAsync(string flowId, CancellationToken cancellationToken) =>
        Session.Rpc.Accounts.Login.CancelAsync(flowId, cancellationToken);

    private CopilotSession Session => _session ?? throw new InvalidOperationException("No sign-in flow is active.");

    private static CopilotSignInStep ConvertStep(AuthLoginStep step) => step switch
    {
        AuthLoginStepOpenUrl browser => new(CopilotSignInStepKind.OpenBrowser) { Url = browser.Url },
        AuthLoginStepAwaiting => new(CopilotSignInStepKind.Awaiting),
        AuthLoginStepCompleted completed when completed.Result.Status == AuthLoginResultStatus.Completed =>
            new(CopilotSignInStepKind.Completed)
            {
                Account = new(true, completed.Result.Login, completed.Result.Host),
            },
        AuthLoginStepCompleted completed when completed.Result.Status == AuthLoginResultStatus.NeedsPlaintextConsent =>
            new(CopilotSignInStepKind.PlaintextRequired),
        AuthLoginStepCompleted completed when completed.Result.Status == AuthLoginResultStatus.NeedsAccountSelection =>
            new(CopilotSignInStepKind.SelectAccount)
            {
                Accounts = completed.Result.Accounts?.Where(account => account.Kind == AccountKind.GitHubDotCom)
                    .Select(account => new CopilotAccountChoice(account.SelectionId, account.Login, account.Host)).ToArray() ?? [],
            },
        AuthLoginStepCompleted completed when completed.Result.Status == AuthLoginResultStatus.Declined =>
            new(CopilotSignInStepKind.Failed),
        AuthLoginStepError => new(CopilotSignInStepKind.Failed),
        _ => new(CopilotSignInStepKind.Failed),
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_session is not null)
            {
                var session = _session;
                _session = null;
                try { await session.DisposeAsync(); }
                finally
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await client.DeleteSessionAsync(session.SessionId, cleanup.Token);
                }
            }
        }
        finally { await client.DisposeAsync(); }
    }
}
#pragma warning restore GHCP001
