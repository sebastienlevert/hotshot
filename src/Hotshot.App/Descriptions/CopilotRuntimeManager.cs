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
        Status = HasBundledRuntime ? "Full edition: the stdio Copilot runtime is bundled. No component download is needed." :
            HasManagedComponent ? "A local component receipt exists; compatibility and SHA256 are checked before use." :
            "The optional Copilot component is not installed. Screenshot analysis and coding-agent access remain unchanged.";
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
                    ? "Your configured external runtime remains selected. Installing/removing a managed component never changes that preference."
                    : "Your configured external runtime is missing. No bundled/managed fallback is selected; choose a valid runtime or clear the preference.";
            if (HasBundledRuntime) return "Full edition: bundled Copilot runtime available; no download is needed.";
            var manifest = _store.ReadInstalledManifest();
            return manifest is null ? "Optional Copilot component not installed." :
                $"Local Copilot component from Hotshot {manifest.AppVersion}; SDK {manifest.CopilotSdkVersion}, {manifest.Runtime}. " +
                (manifest.AppVersion == AppVersion ? "Matches this release." : "Retained across app updates: exact SDK/architecture compatibility is required.");
        }, cancellationToken);
        SetStatus(status);
    }

    public async Task InstallAsync(IProgress<ComponentProgress>? progress, CancellationToken cancellationToken)
    {
        if (HasBundledRuntime) throw new InvalidOperationException("The Full edition already includes the runtime.");
        SetStatus("Preparing the explicitly requested component installation...");
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
        SetStatus("Removing only verified, managed component directories...");
        try
        {
            await _store.RemoveAsync(cancellationToken: cancellationToken);
            SetStatus("Managed Copilot component removed. External/bundled runtimes, images, AI settings and agent permissions are unchanged.");
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
