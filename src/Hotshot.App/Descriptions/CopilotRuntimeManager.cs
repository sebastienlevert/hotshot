using System.Reflection;
using System.Runtime.InteropServices;
using GitHub.Copilot;
using Hotshot.Core.Updates;

namespace Hotshot.Descriptions;

internal sealed class CopilotRuntimeManager : IDisposable
{
    private readonly CopilotComponentStore _store;
    private readonly Func<string?> _externalRuntime;
    private readonly HttpClient _http = ReleaseAssetDownloader.CreateClient();
    private readonly string _bundled;
    public string AppVersion { get; }
    public string Runtime { get; }
    public DistributionEdition Edition { get; }
    public bool IsBusy => _store.IsBusy;
    public bool HasManagedComponent => _store.HasReceipt;
    public bool HasBundledRuntime => File.Exists(_bundled);
    public string Status { get; private set; }
    public event Action? StatusChanged;

    public CopilotRuntimeManager(string dataDirectory, Func<string?> externalRuntime, Func<bool> canChange)
    {
        var metadata = DistributionMetadata.Read(AppContext.BaseDirectory, RuntimeInformation.ProcessArchitecture);
        Runtime = UpdateChannel.ForArchitecture(RuntimeInformation.ProcessArchitecture);
        AppVersion = metadata?.AppVersion ??
            (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? throw new InvalidDataException("The Hotshot application version is unavailable."));
        if (metadata is not null && metadata.CopilotSdkVersion != CopilotComponentManifest.SdkVersion)
            throw new InvalidDataException("The packaged Copilot SDK version does not match this app. Reinstall Hotshot.");
        Edition = metadata?.DistributionEdition ?? DistributionEdition.Full;
        _externalRuntime = externalRuntime;
        _bundled = BundledRuntimePath;
        _store = new CopilotComponentStore(Path.Combine(dataDirectory, "components", "copilot"), AppVersion, Runtime,
            canChange: canChange);
        Status = HasBundledRuntime ? "Included with Hotshot." :
            HasManagedComponent ? "Copilot is installed." : "Install Copilot to use screenshot descriptions.";
    }

    public static string BundledRuntimePath => Path.Combine(AppContext.BaseDirectory, "runtimes",
        UpdateChannel.ForArchitecture(RuntimeInformation.ProcessArchitecture), "native", "copilot-runtime.exe");

    public string ResolveRuntime() => CopilotRuntimeResolver.Resolve(_externalRuntime(), _bundled, _store.ResolveRuntime);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var status = await Task.Run(() =>
        {
            var external = _externalRuntime();
            if (!string.IsNullOrWhiteSpace(external))
                return File.Exists(external)
                    ? "Using your custom Copilot file."
                    : "Custom Copilot file missing. Choose another file or use Hotshot's Copilot.";
            if (HasBundledRuntime) return "Included with Hotshot.";
            var manifest = _store.ReadInstalledManifest();
            return manifest is null ? "Install Copilot to use screenshot descriptions." : "Copilot is installed.";
        }, cancellationToken);
        SetStatus(status);
    }

    public async Task InstallAsync(IProgress<ComponentProgress>? progress, CancellationToken cancellationToken)
    {
        if (HasBundledRuntime) throw new InvalidOperationException("Copilot is already included with Hotshot.");
        SetStatus("Preparing installation...");
        try
        {
            await Task.Run(() => _store.InstallAsync(new ReleaseAssetDownloader(_http), TestRuntimeAsync,
                progress, cancellationToken), cancellationToken);
            await RefreshAsync(CancellationToken.None);
        }
        finally { StatusChanged?.Invoke(); }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        SetStatus("Removing Copilot...");
        try
        {
            await _store.RemoveAsync(cancellationToken: cancellationToken);
            SetStatus("Copilot removed. Your screenshots and settings are unchanged.");
        }
        finally { StatusChanged?.Invoke(); }
    }

    public static async Task TestRuntimeAsync(string runtime, CancellationToken cancellationToken)
    {
        var healthRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(runtime)!, ".."));
        var healthDirectory = Path.Combine(healthRoot, ".health-" + Guid.NewGuid().ToString("N"));
        SafeComponentPaths.EnsureNotLinked(healthDirectory);
        Directory.CreateDirectory(healthDirectory);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await using var client = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                Connection = RuntimeConnection.ForStdio(runtime),
                WorkingDirectory = healthDirectory,
                BaseDirectory = Path.Combine(healthDirectory, "copilot"),
                UseLoggedInUser = false,
            });
            await client.StartAsync(timeout.Token);
            await client.StopAsync();
        }
        finally { SafeComponentPaths.DeleteTree(healthRoot, healthDirectory); }
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    public void Dispose() => _http.Dispose();
}
