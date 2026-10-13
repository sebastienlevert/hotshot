using Hotshot.Core.Descriptions;

namespace Hotshot.Core.Tests;

public sealed class CopilotSignInTests
{
    private const string BrowserUrl = "https://github.com/login/oauth/authorize?state=generated&code_challenge=generated";
    private static readonly CopilotAccountStatus Account = new(true, "generated-user", "https://github.com");

    [Fact]
    public async Task BrowserLoginAdvancesOnlyItsOwnedFlowAndVerifiesTheAccount()
    {
        await using var client = new Client(new(CopilotSignInStepKind.OpenBrowser) { Url = BrowserUrl },
            new CopilotSignInStep(CopilotSignInStepKind.Completed) { Account = Account });
        var opened = new List<Uri>();
        var result = await RunAsync(client, (uri, _) => { opened.Add(uri); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.Equal(Account, result);
        Assert.Equal(BrowserUrl, Assert.Single(opened).AbsoluteUri);
        Assert.Equal("generated-flow", Assert.Single(client.Advanced));
        Assert.Empty(client.Cancelled);
    }

    [Theory]
    [InlineData("http://github.com/login/oauth/authorize")]
    [InlineData("https://github.com.attacker.example/login/oauth/authorize")]
    [InlineData("https://github.com@attacker.example/login/oauth/authorize")]
    [InlineData("https://user@github.com/login/oauth/authorize")]
    [InlineData("https://github.com:444/login/oauth/authorize")]
    [InlineData("https://github.com/login/oauth/authorize#fragment")]
    [InlineData("https://github.com/redirect")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData(null)]
    public async Task UntrustedBrowserAddressesNeverReachTheBrowserAndCancelTheOwnedFlow(string? url)
    {
        await using var client = new Client(new(CopilotSignInStepKind.OpenBrowser) { Url = url });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(client,
            (_, _) => throw new InvalidOperationException("Must not launch."), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(url ?? "generated-secret", error.Message, StringComparison.Ordinal);
        Assert.Equal("generated-flow", Assert.Single(client.Cancelled));
        Assert.Empty(client.Advanced);
    }

    [Fact]
    public async Task CancellationDuringBrowserAuthorizationCancelsTheExactFlow()
    {
        await using var client = new Client(new(CopilotSignInStepKind.OpenBrowser) { Url = BrowserUrl });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(client, (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, cancellation.Token));
        Assert.Equal("generated-flow", Assert.Single(client.Cancelled));
        Assert.Empty(client.Advanced);
    }

    [Fact]
    public async Task CancelledBeforeStartingDoesNotAllocateAFlow()
    {
        await using var client = new Client(new(CopilotSignInStepKind.Awaiting));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(client, cancellationToken: cancellation.Token));
        Assert.Equal(0, client.BeginCount);
        Assert.Empty(client.Cancelled);
    }

    [Fact]
    public async Task PlaintextStorageIsRefusedWithoutAdvancingOrGrantingConsent()
    {
        await using var client = new Client(new(CopilotSignInStepKind.PlaintextRequired));
        var error = await Assert.ThrowsAsync<CopilotSignInException>(() => RunAsync(client, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("securely", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Advanced);
        Assert.Equal("generated-flow", Assert.Single(client.Cancelled));
    }

    [Fact]
    public async Task AccountSelectionRequiresAnExplicitChoiceFromTheOriginalFlow()
    {
        var choices = new[] { new CopilotAccountChoice("selected-generated-id", "generated-user", "https://github.com") };
        await using var client = new Client(new(CopilotSignInStepKind.SelectAccount) { Accounts = choices },
            new CopilotSignInStep(CopilotSignInStepKind.Completed) { Account = Account });
        var result = await CopilotSignIn.RunAsync(client, (_, _) => Task.CompletedTask,
            (offered, _) => { Assert.Equal(choices, offered); return Task.FromResult<string?>(offered[0].SelectionId); },
            null, TestContext.Current.CancellationToken);
        Assert.Equal(Account, result);
        Assert.Equal("selected-generated-id", Assert.Single(client.Selections));
        Assert.Empty(client.Cancelled);
    }

    [Fact]
    public async Task AnUnrelatedAccountSelectionIsRejected()
    {
        await using var client = new Client(new(CopilotSignInStepKind.SelectAccount)
        {
            Accounts = [new("offered-id", "generated-user", "https://github.com")],
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => CopilotSignIn.RunAsync(client, (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>("not-offered"), null, TestContext.Current.CancellationToken));
        Assert.Empty(client.Advanced);
        Assert.Single(client.Cancelled);
    }

    [Fact]
    public async Task ChoosingCancelDoesNotSilentlySelectAnAccount()
    {
        await using var client = new Client(new(CopilotSignInStepKind.SelectAccount)
        {
            Accounts = [new("offered-id", "generated-user", "https://github.com")],
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CopilotSignIn.RunAsync(client, (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>(null), null, TestContext.Current.CancellationToken));
        Assert.Empty(client.Advanced);
        Assert.Single(client.Cancelled);
    }

    [Theory]
    [InlineData(false, "generated-user", "https://github.com")]
    [InlineData(true, "different-user", "https://github.com")]
    [InlineData(true, "generated-user", "https://attacker.example")]
    public async Task ACompletedResponseIsNotSuccessUntilTheSelectedAccountIsVerified(bool authenticated, string login, string host)
    {
        await using var client = new Client(new(CopilotSignInStepKind.Completed) { Account = Account })
        {
            Status = new(authenticated, login, host),
        };
        await Assert.ThrowsAsync<CopilotSignInException>(() => RunAsync(client, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(client.Cancelled);
    }

    [Fact]
    public async Task UnknownStepsFailClosedAndReleaseTheFlow()
    {
        await using var client = new Client(new((CopilotSignInStepKind)999));
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(client, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(client.Cancelled);
    }

    [Fact]
    public async Task CleanupFailureDoesNotHideTheOriginalFailure()
    {
        var cleanupFailure = new IOException("Generated cleanup failure");
        await using var client = new Client(new(CopilotSignInStepKind.PlaintextRequired)) { CancelFailure = cleanupFailure };
        var error = await Assert.ThrowsAsync<CopilotSignInException>(() => RunAsync(client, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("securely", error.Message, StringComparison.Ordinal);
        Assert.Same(cleanupFailure, error.Data["Hotshot.SignInCancellationError"]);
    }

    private static Task<CopilotAccountStatus> RunAsync(Client client, Func<Uri, CancellationToken, Task>? browser = null,
        CancellationToken cancellationToken = default) => CopilotSignIn.RunAsync(client,
            browser ?? ((_, _) => Task.CompletedTask), (_, _) => Task.FromResult<string?>(null), null, cancellationToken);

    private sealed class Client(CopilotSignInStep first, params CopilotSignInStep[] following) : ICopilotSignInClient
    {
        private readonly Queue<CopilotSignInStep> _steps = new(following);
        public int BeginCount { get; private set; }
        public List<string> Advanced { get; } = [];
        public List<string?> Selections { get; } = [];
        public List<string> Cancelled { get; } = [];
        public CopilotAccountStatus Status { get; init; } = Account;
        public Exception? CancelFailure { get; init; }
        public Task<CopilotAccountStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
        public Task<CopilotSignInStart> BeginAsync(CancellationToken cancellationToken)
        {
            BeginCount++;
            return Task.FromResult(new CopilotSignInStart("generated-flow", first));
        }
        public Task<CopilotSignInStep> AdvanceAsync(string flowId, string? selectionId, CancellationToken cancellationToken)
        {
            Advanced.Add(flowId);
            Selections.Add(selectionId);
            return Task.FromResult(_steps.Dequeue());
        }
        public Task CancelAsync(string flowId, CancellationToken cancellationToken)
        {
            Cancelled.Add(flowId);
            return CancelFailure is null ? Task.CompletedTask : Task.FromException(CancelFailure);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
