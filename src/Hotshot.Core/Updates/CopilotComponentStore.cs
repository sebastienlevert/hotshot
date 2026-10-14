using System.Security.Cryptography;
using System.Text.Json;

namespace Hotshot.Core.Updates;

/// <summary>Machine-local receipts, never shared settings; installing does not grant AI or agent permissions.</summary>
public sealed class CopilotComponentStore
{
    private static readonly string[] SupportedRuntimes = ["win-x64", "win-arm64"];
    private readonly string _root;
    private readonly string _appVersion;
    private readonly string _runtime;
    private readonly string _sdkVersion;
    private readonly Func<bool> _canChange;
    private int _operationActive;
    private string ReceiptPath => Path.Combine(_root, "installed.json");

    public CopilotComponentStore(string root, string appVersion, string runtime,
        string sdkVersion = CopilotComponentManifest.SdkVersion, Func<bool>? canChange = null)
    {
        if (!Path.IsPathFullyQualified(root) || !ReleaseVersion.IsValid(appVersion) || !ReleaseVersion.IsValid(sdkVersion) ||
            runtime is not ("win-x64" or "win-arm64")) throw new ArgumentException("Invalid component store configuration.");
        _root = Path.GetFullPath(root);
        _appVersion = appVersion;
        _runtime = runtime;
        _sdkVersion = sdkVersion;
        _canChange = canChange ?? (() => true);
        SafeComponentPaths.EnsureNotLinked(_root);
    }

    public bool IsBusy => Volatile.Read(ref _operationActive) != 0;
    public bool HasReceipt => File.Exists(ReceiptPath);

    public CopilotComponentManifest? ReadInstalledManifest()
    {
        SafeComponentPaths.EnsureNotLinked(ReceiptPath);
        if (!File.Exists(ReceiptPath)) return null;
        var manifest = ReadManifest(ReceiptPath);
        // A component from an older app release is reusable only with the exact same SDK and architecture.
        manifest.Validate(_runtime, _sdkVersion);
        return manifest;
    }

    public string? ResolveRuntime()
    {
        var manifest = ReadInstalledManifest();
        if (manifest is null) return null;
        var directory = ComponentDirectory(manifest);
        ValidateInstalledFiles(directory, manifest);
        return SafeComponentPaths.Child(directory, "copilot-runtime.exe");
    }

    public async Task InstallAsync(ReleaseAssetDownloader downloader, Func<string, CancellationToken, Task> healthTest,
        IProgress<ComponentProgress>? progress, CancellationToken cancellationToken)
    {
        BeginOperation(cancellationToken);
        string? staging = null;
        string? createdDirectory = null;
        var committed = false;
        try
        {
            RequireIdle();
            progress?.Report(new("Preparing download"));
            var json = await downloader.ReadManifestAsync(_appVersion,
                CopilotComponentManifest.AssetName(_appVersion, _runtime, "json"), cancellationToken);
            var manifest = JsonSerializer.Deserialize<CopilotComponentManifest>(json, ComponentJson.Options)
                ?? throw new InvalidDataException("Component manifest is empty.");
            manifest.Validate(_runtime, _sdkVersion, _appVersion);
            SafeComponentPaths.EnsureNotLinked(_root);
            Directory.CreateDirectory(_root);
            staging = SafeComponentPaths.Child(_root, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var archive = Path.Combine(staging, "component.zip");
            await downloader.DownloadAsync(manifest, archive, progress, cancellationToken);
            var payload = Path.Combine(staging, "payload");
            await CopilotComponentArchive.ExtractAsync(archive, payload, manifest, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequireIdle();
            await File.WriteAllTextAsync(Path.Combine(payload, CopilotComponentManifest.ReceiptFileName),
                JsonSerializer.Serialize(manifest, ComponentJson.Options), cancellationToken);
            var final = ComponentDirectory(manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            SafeComponentPaths.EnsureNotLinked(final);
            if (Directory.Exists(final))
            {
                var existing = ReadManifest(Path.Combine(final, CopilotComponentManifest.ReceiptFileName));
                existing.Validate(_runtime, _sdkVersion, _appVersion);
                if (!existing.Sha256.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A different component is already installed for this immutable release.");
                ValidateInstalledFiles(final, manifest);
            }
            else
            {
                // Windows can keep a launched image locked after SDK shutdown. Never relocate an executed runtime.
                Directory.Move(payload, final);
                createdDirectory = final;
            }

            progress?.Report(new("Checking installation"));
            await healthTest(Path.Combine(final, "copilot-runtime.exe"), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequireIdle();
            ValidateInstalledFiles(final, manifest);

            // The cache directory is not selected until its startup test passes and the root receipt commits.
            var receipt = Path.Combine(staging, "installed.json");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(manifest, ComponentJson.Options), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SafeComponentPaths.EnsureNotLinked(ReceiptPath);
            File.Move(receipt, ReceiptPath, overwrite: true);
            committed = true;
            progress?.Report(new("Installed", manifest.ArchiveBytes, manifest.ArchiveBytes));
        }
        finally
        {
            try
            {
                try
                {
                    if (!committed && createdDirectory is not null) SafeComponentPaths.DeleteTree(_root, createdDirectory);
                }
                finally { if (staging is not null) SafeComponentPaths.DeleteTree(_root, staging); }
            }
            finally { Volatile.Write(ref _operationActive, 0); }
        }
    }

    public async Task RemoveAsync(Func<Task>? releaseRuntime = null, CancellationToken cancellationToken = default)
    {
        BeginOperation(cancellationToken);
        try
        {
            RequireIdle();
            if (releaseRuntime is not null) await releaseRuntime();
            RequireIdle();
            await Task.Run(() =>
            {
                SafeComponentPaths.EnsureNotLinked(_root);
                if (!Directory.Exists(_root)) return;
                var directories = new List<string>();
                foreach (var sdkDirectory in Directory.EnumerateDirectories(_root).Where(path => ReleaseVersion.IsValid(Path.GetFileName(path))))
                foreach (var runtime in SupportedRuntimes)
                {
                    var runtimeDirectory = SafeComponentPaths.Child(sdkDirectory, runtime);
                    if (!Directory.Exists(runtimeDirectory)) continue;
                    foreach (var directory in Directory.EnumerateDirectories(runtimeDirectory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!ReleaseVersion.IsValid(Path.GetFileName(directory))) continue;
                        var receipt = Path.Combine(directory, CopilotComponentManifest.ReceiptFileName);
                        if (!File.Exists(receipt)) continue;
                        var manifest = ReadManifest(receipt);
                        manifest.Validate(runtime, Path.GetFileName(sdkDirectory), Path.GetFileName(directory));
                        ValidateInstalledFiles(directory, manifest, verifyHashes: false);
                        directories.Add(directory);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                RequireIdle();
                foreach (var directory in directories) SafeComponentPaths.DeleteTree(_root, directory);
                SafeComponentPaths.EnsureNotLinked(ReceiptPath);
                if (File.Exists(ReceiptPath)) File.Delete(ReceiptPath);
            }, cancellationToken);
        }
        finally { Volatile.Write(ref _operationActive, 0); }
    }

    private void BeginOperation(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
            throw new InvalidOperationException("A component operation is already running.");
    }

    private void RequireIdle()
    {
        if (!_canChange()) throw new InvalidOperationException("Wait for screenshot analysis to finish before changing the Copilot component.");
    }

    private string ComponentDirectory(CopilotComponentManifest manifest) =>
        SafeComponentPaths.Child(_root, $"{manifest.CopilotSdkVersion}/{manifest.Runtime}/{manifest.AppVersion}");

    private static CopilotComponentManifest ReadManifest(string path)
    {
        SafeComponentPaths.EnsureNotLinked(path);
        if (new FileInfo(path).Length > CopilotComponentManifest.MaximumManifestBytes)
            throw new InvalidDataException("Local component receipt is too large.");
        return JsonSerializer.Deserialize<CopilotComponentManifest>(File.ReadAllBytes(path), ComponentJson.Options)
            ?? throw new InvalidDataException("Local component receipt is empty.");
    }

    private static void ValidateInstalledFiles(string directory, CopilotComponentManifest manifest, bool verifyHashes = true)
    {
        SafeComponentPaths.EnsureTreeNotLinked(directory);
        var receipt = ReadManifest(Path.Combine(directory, CopilotComponentManifest.ReceiptFileName));
        if (JsonSerializer.Serialize(receipt, ComponentJson.Options) != JsonSerializer.Serialize(manifest, ComponentJson.Options))
            throw new InvalidDataException("The component installation differs from its receipt.");
        foreach (var file in manifest.Files)
        {
            var path = SafeComponentPaths.Child(directory, file.Path);
            if (!verifyHashes) continue; // A damaged component can be removed, but unknown files/links cannot.
            using var input = File.OpenRead(path);
            if (input.Length != file.Bytes || !CopilotComponentManifest.HashMatches(SHA256.HashData(input), file.Sha256))
                throw new InvalidDataException("An installed Copilot runtime file failed integrity verification. Remove and reinstall the component.");
        }
        var expected = manifest.Files.Select(file => file.Path).Append(CopilotComponentManifest.ReceiptFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            if (!expected.Contains(Path.GetRelativePath(directory, file).Replace('\\', '/')))
                throw new InvalidDataException("Unexpected files in the managed component directory; no files were removed.");
    }
}

public static class CopilotRuntimeResolver
{
    public static string Resolve(string? configuredExternalRuntime, string bundledRuntime, Func<string?> installedRuntime)
    {
        if (!string.IsNullOrWhiteSpace(configuredExternalRuntime))
        {
            if (!Path.IsPathFullyQualified(configuredExternalRuntime) || !File.Exists(configuredExternalRuntime))
                throw new FileNotFoundException("The configured external Copilot runtime is missing. It remains authoritative; no other runtime was selected.", configuredExternalRuntime);
            return configuredExternalRuntime;
        }
        if (File.Exists(bundledRuntime)) return bundledRuntime;
        return installedRuntime() ?? throw new FileNotFoundException(
            "Copilot is not installed in Hotshot Core. Install the optional Copilot component in Screenshot settings or choose an SDK-compatible external runtime.");
    }
}
