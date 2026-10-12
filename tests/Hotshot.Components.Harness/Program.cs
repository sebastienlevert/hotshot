using System.Runtime.InteropServices;
using System.Text.Json;
using GitHub.Copilot;
using Hotshot.Core.Updates;

if (args.Length != 2) throw new ArgumentException("Usage: Hotshot.Components.Harness <publish directory> <component manifest>");
var publish = Path.GetFullPath(args[0]);
var manifestPath = Path.GetFullPath(args[1]);
var metadata = DistributionMetadata.Read(publish, RuntimeInformation.ProcessArchitecture)
    ?? throw new InvalidDataException("A packaged public edition requires distribution metadata.");
var manifest = JsonSerializer.Deserialize<CopilotComponentManifest>(await File.ReadAllBytesAsync(manifestPath), ComponentJson.Options)
    ?? throw new InvalidDataException("Missing component manifest.");
manifest.Validate(metadata.Runtime, metadata.CopilotSdkVersion, metadata.AppVersion);
if (metadata.CopilotSdkVersion != CopilotComponentManifest.SdkVersion)
    throw new InvalidDataException("The runtime manager's pinned SDK version must be updated with the package reference.");
if (!File.Exists(Path.Combine(publish, "Mcp", "Hotshot.Mcp.exe")) ||
    !Directory.Exists(Path.Combine(publish, "agents"))) throw new InvalidDataException("Missing packaged MCP integration.");

var work = Path.Combine(Environment.CurrentDirectory, "artifacts", "component-validation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    var native = Path.Combine(publish, "runtimes", metadata.Runtime, "native");
    if (metadata.DistributionEdition == DistributionEdition.Full)
    {
        if (File.Exists(Path.Combine(native, "copilot_runtime.dll")))
            throw new InvalidDataException("The Full runtime still contains its duplicate in-process alias.");
        await HealthAsync(Path.Combine(native, "copilot-runtime.exe"), CancellationToken.None);
    }
    else
    {
        if (Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).Any(path =>
            Path.GetFileName(path) is "copilot-runtime.exe" or "copilot_runtime.dll" or "runtime.node"))
            throw new InvalidDataException("Core contains a bundled Copilot runtime.");
    }
    using var http = new HttpClient(new LocalReleaseHandler(Path.GetDirectoryName(manifestPath)!));
    var store = new CopilotComponentStore(Path.Combine(work, "components"), metadata.AppVersion,
        metadata.Runtime, metadata.CopilotSdkVersion);
    await store.InstallAsync(new ReleaseAssetDownloader(http), HealthAsync, null, CancellationToken.None);
    var resolved = store.ResolveRuntime() ?? throw new InvalidDataException("Installed component could not be resolved.");
    await HealthAsync(resolved, CancellationToken.None);
    await store.RemoveAsync();
    if (store.HasReceipt || store.ResolveRuntime() is not null) throw new InvalidDataException("Managed component removal failed.");
    Console.WriteLine($"PASS {metadata.Edition} / {metadata.Runtime}: metadata/channel, MCP presence, SHA256/extraction, SDK stdio startup and exact managed removal; no account, models, sessions or image upload.");
}
finally { SafeComponentPaths.DeleteTree(Path.GetDirectoryName(work)!, work); }

async Task HealthAsync(string runtime, CancellationToken cancellationToken)
{
    var health = Path.Combine(work, "health-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(health);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(45));
    await using var client = new CopilotClient(new CopilotClientOptions
    {
        Mode = CopilotClientMode.Empty,
        Connection = RuntimeConnection.ForStdio(runtime),
        WorkingDirectory = health, BaseDirectory = Path.Combine(health, "copilot"), UseLoggedInUser = false,
    });
    await client.StartAsync(timeout.Token);
    await client.StopAsync();
}

sealed class LocalReleaseHandler(string directory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidDataException("Missing official download URL.");
        if (uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/sebastienlevert/hotshot/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected component download URL.");
        var name = Uri.UnescapeDataString(uri.Segments[^1]);
        CopilotComponentManifest.ValidateRelativePath(name);
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(File.OpenRead(Path.Combine(directory, name))),
        };
        return Task.FromResult(response);
    }
}
