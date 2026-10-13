using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Hotshot.Descriptions;

internal static class CopilotConnection
{
    public static CopilotClient Create(string directory, string runtime)
    {
        Directory.CreateDirectory(directory);
        return new CopilotClient(new CopilotClientOptions
        {
            // Empty mode unconditionally disables the keychain; this is a single-user desktop host.
            Mode = CopilotClientMode.CopilotCli,
            Connection = RuntimeConnection.ForStdio(runtime),
            WorkingDirectory = directory,
            BaseDirectory = Path.Combine(directory, "copilot"),
            UseLoggedInUser = true,
            LogLevel = CopilotLogLevel.None,
        });
    }

    public static SessionConfig RestrictedSession() => new()
    {
        AvailableTools = [],
        EnableConfigDiscovery = false,
        EnableExperimentalMode = false,
        EnableSessionTelemetry = false,
        SkipEmbeddingRetrieval = true,
        EmbeddingCacheStorage = EmbeddingCacheStorageMode.InMemory,
        EnableOnDemandInstructionDiscovery = false,
        EnableFileHooks = false,
        EnableHostGitOperations = false,
        EnableSessionStore = false,
        EnableSkills = false,
        Memory = new MemoryConfiguration { Enabled = false },
        McpOAuthTokenStorage = McpOAuthTokenStorageMode.InMemory,
        SkipCustomInstructions = true,
        CustomAgentsLocalOnly = true,
        CoauthorEnabled = false,
        ManageScheduleEnabled = false,
        IncludedBuiltinSkills = [],
        SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Replace,
            Content = "This session is only for GitHub account authorization. Do not use tools, read files or perform model tasks.",
        },
#pragma warning disable GHCP001 // The SDK's explicit deny decision is still marked experimental.
        OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Hotshot sessions cannot execute tools.")),
#pragma warning restore GHCP001
    };

    public static async Task<CopilotSession> CreateRestrictedSessionAsync(CopilotClient client, SessionConfig config,
        CancellationToken cancellationToken)
    {
        var session = await client.CreateSessionAsync(config, cancellationToken);
        try
        {
#pragma warning disable GHCP001 // Clear the ambient plugin list before any auth/model operation.
            await session.Rpc.Options.UpdateAsync(installedPlugins: [], includedBuiltinSkills: [], cancellationToken: cancellationToken);
#pragma warning restore GHCP001
            return session;
        }
        catch (Exception failure)
        {
            try { await session.DisposeAsync(); }
            catch (Exception cleanupFailure) { failure.Data["Hotshot.RestrictedSessionCleanupError"] = cleanupFailure; }
            throw;
        }
    }
}
