using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hotshot.Core.Settings;
using Hotshot.Core.Updates;

namespace Hotshot.Core.Tests;

public sealed class DistributionComponentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.CurrentDirectory, ".component-tests-" + Guid.NewGuid().ToString("N"));
    public DistributionComponentTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => SafeComponentPaths.DeleteTree(Environment.CurrentDirectory, _directory);

    [Theory]
    [InlineData(Architecture.X64, "full", "win-x64")]
    [InlineData(Architecture.Arm64, "full", "win-arm64")]
    [InlineData(Architecture.X64, "core", "core-win-x64")]
    [InlineData(Architecture.Arm64, "core", "core-win-arm64")]
    public void EditionChannelsPreserveFullAndUseProcessArchitecture(Architecture architecture, string edition, string expected)
    {
        var metadata = Metadata(architecture, edition);
        Assert.Equal(expected, DistributionMetadata.Channel(architecture, metadata));
    }

    [Fact]
    public void LegacyMissingMetadataAlwaysMeansFull()
    {
        Assert.Null(DistributionMetadata.Read(_directory, Architecture.X64));
        Assert.Equal("win-x64", DistributionMetadata.Channel(Architecture.X64, null));
        Assert.Equal("win-arm64", DistributionMetadata.Channel(Architecture.Arm64, null));
    }

    [Fact]
    public void MetadataRoundTripsAndRejectsWrongArchitectureOrUnknownEdition()
    {
        File.WriteAllText(Path.Combine(_directory, DistributionMetadata.FileName),
            JsonSerializer.Serialize(Metadata(Architecture.Arm64, "core"), ComponentJson.Options));
        Assert.Equal("core", DistributionMetadata.Read(_directory, Architecture.Arm64)!.Edition);
        Assert.Throws<InvalidDataException>(() => DistributionMetadata.Read(_directory, Architecture.X64));
        Assert.Throws<InvalidDataException>(() => (Metadata(Architecture.X64, "other")).Validate(Architecture.X64));
        Assert.Throws<InvalidDataException>(() => (Metadata(Architecture.X64, "core") with { SchemaVersion = 2 }).Validate(Architecture.X64));
        Assert.Throws<PlatformNotSupportedException>(() => DistributionMetadata.Channel(Architecture.X86, null));
    }

    [Theory]
    [InlineData("../image.png")]
    [InlineData("nested/../../image.png")]
    [InlineData("C:/images/photo.png")]
    [InlineData("/absolute")]
    [InlineData("nested\\file")]
    [InlineData("file:stream")]
    [InlineData("CON.txt")]
    [InlineData("lpt1")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("a//b")]
    public void UnsafePathsAreRejected(string path) =>
        Assert.Throws<InvalidDataException>(() => CopilotComponentManifest.ValidateRelativePath(path));

    [Fact]
    public void ManifestRejectsWrongArchitectureVersionSdkHashLimitsAndAlias()
    {
        var (_, manifest) = Fixture();
        Assert.Throws<InvalidDataException>(() => manifest.Validate("win-arm64", CopilotComponentManifest.SdkVersion));
        Assert.Throws<InvalidDataException>(() => manifest.Validate("win-x64", "2.0.0"));
        Assert.Throws<InvalidDataException>(() => manifest.Validate("win-x64", CopilotComponentManifest.SdkVersion, "0.1.14"));
        foreach (var invalid in new[]
        {
            manifest with { Sha256 = "not-a-hash" },
            manifest with { SchemaVersion = 2 },
            manifest with { FileName = "../component.zip" },
            manifest with { ArchiveBytes = CopilotComponentManifest.MaximumArchiveBytes + 1 },
            manifest with { UnpackedBytes = CopilotComponentManifest.MaximumUnpackedBytes + 1 },
            manifest with { Files = [manifest.Files[0], manifest.Files[0]] },
            manifest with { Files = [manifest.Files[0] with { Path = "copilot_runtime.dll" }, manifest.Files[1]] },
            manifest with { Files = [manifest.Files[0] with { Sha256 = new string('x', 64) }, manifest.Files[1]] },
        }) Assert.Throws<InvalidDataException>(() => invalid.Validate("win-x64", CopilotComponentManifest.SdkVersion));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedInstallationPreservesPermissionsAndExternalRuntimeAndSurvivesCompatibleUpdate(bool enabled)
    {
        var (archive, manifest) = Fixture();
        var settings = new AppSettings();
        settings.General.DescribeScreenshots = enabled;
        settings.General.ReviewBeforeAiUpload = true;
        settings.General.AiExcludedApps = ["password-manager"];
        settings.Agents.Enabled = enabled;
        settings.Agents.AllowHistoryAccess = enabled;
        settings.Agents.SessionAccessOnly = true;
        settings.Agents.ApproveRequests = true;
        var external = Path.Combine(_directory, "external.exe");
        File.WriteAllText(external, "external");
        settings.General.CopilotRuntimePath = external;
        var before = SettingsStore.Serialize(settings);
        using var http = Client(manifest, archive);
        var store = Store();
        var calls = 0;
        await store.InstallAsync(new ReleaseAssetDownloader(http), (runtime, token) =>
        {
            Assert.True(File.Exists(runtime));
            Assert.False(store.HasReceipt);
            token.ThrowIfCancellationRequested();
            calls++;
            return Task.CompletedTask;
        }, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.True(store.HasReceipt);
        var resolved = store.ResolveRuntime();
        Assert.NotNull(resolved);
        Assert.Equal(external, CopilotRuntimeResolver.Resolve(external, "missing-bundled", store.ResolveRuntime));
        var compatible = Store("0.1.14");
        Assert.Equal(resolved, compatible.ResolveRuntime());
        Assert.Throws<InvalidDataException>(() => Store("0.1.14", "2.0.0").ResolveRuntime());
        await store.RemoveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(store.HasReceipt);
        Assert.False(File.Exists(resolved));
        Assert.True(File.Exists(external));
        Assert.Equal(before, SettingsStore.Serialize(settings));
    }

    [Fact]
    public void MissingConfiguredExternalRuntimeNeverFallsBack()
    {
        var bundled = Path.Combine(_directory, "bundled.exe");
        File.WriteAllText(bundled, "bundled");
        var consultedManaged = false;
        Assert.Throws<FileNotFoundException>(() => CopilotRuntimeResolver.Resolve(Path.Combine(_directory, "missing.exe"), bundled,
            () => { consultedManaged = true; return bundled; }));
        Assert.False(consultedManaged);
        Assert.Equal(bundled, CopilotRuntimeResolver.Resolve(null, bundled, () => throw new InvalidOperationException()));
        Assert.Throws<FileNotFoundException>(() => CopilotRuntimeResolver.Resolve(null, "missing-bundled", () => null));
    }

    [Fact]
    public async Task InvalidArchiveHashNeverRunsHealthOrWritesReceipt()
    {
        var (archive, manifest) = Fixture();
        manifest = manifest with { Sha256 = new string('0', 64) };
        using var http = Client(manifest, archive);
        var store = Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(new ReleaseAssetDownloader(http),
            (_, _) => throw new InvalidOperationException("Health must not run."), null, TestContext.Current.CancellationToken));
        Assert.False(store.HasReceipt);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_directory, "managed")));
    }

    [Fact]
    public async Task ArchiveRejectsTraversalDuplicatePathsUnexpectedFilesAndLinks()
    {
        foreach (var extra in new[] { "../escape.exe", "COPILOT-RUNTIME.EXE", "unexpected.png", "linked" })
        {
            var (archive, manifest) = Fixture(extra, extra == "linked");
            var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".zip");
            await File.WriteAllBytesAsync(path, archive, TestContext.Current.CancellationToken);
            var destination = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
            await Assert.ThrowsAsync<InvalidDataException>(() => CopilotComponentArchive.ExtractAsync(
                path, destination, manifest, null, TestContext.Current.CancellationToken));
        }
        Assert.False(File.Exists(Path.Combine(_directory, "escape.exe")));
    }

    [Fact]
    public async Task ArchiveEntryCountsAndPerFileHashesAreBounded()
    {
        var (archive, manifest) = Fixture(extraCount: CopilotComponentManifest.MaximumFiles * 2);
        var path = Path.Combine(_directory, "too-many.zip");
        await File.WriteAllBytesAsync(path, archive, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => CopilotComponentArchive.ExtractAsync(path,
            Path.Combine(_directory, "too-many"), manifest, null, TestContext.Current.CancellationToken));
        (archive, manifest) = Fixture();
        manifest = manifest with { Files = [manifest.Files[0] with { Sha256 = new string('0', 64) }, manifest.Files[1]] };
        path = Path.Combine(_directory, "bad-file.zip");
        await File.WriteAllBytesAsync(path, archive, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => CopilotComponentArchive.ExtractAsync(path,
            Path.Combine(_directory, "bad-file"), manifest, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationAndHealthFailureNeverPublishAnInstallation()
    {
        var (archive, manifest) = Fixture();
        using var http = Client(manifest, archive);
        var store = Store();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InstallAsync(new ReleaseAssetDownloader(http),
            (_, _) => Task.CompletedTask, null, cancellation.Token));
        Assert.False(store.HasReceipt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(new ReleaseAssetDownloader(http),
            (_, _) => throw new InvalidOperationException("SDK failed to start."), null, TestContext.Current.CancellationToken));
        Assert.False(store.HasReceipt);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InstallAsync(new ReleaseAssetDownloader(http),
            (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }, null, cancellation.Token));
        Assert.False(store.HasReceipt);
    }

    [Fact]
    public async Task CancellationDuringHealthPreservesPreviouslyInstalledReceipt()
    {
        var (archive, manifest) = Fixture();
        using var http = Client(manifest, archive);
        var store = Store();
        await store.InstallAsync(new ReleaseAssetDownloader(http), (_, _) => Task.CompletedTask, null,
            TestContext.Current.CancellationToken);
        var previous = store.ResolveRuntime();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InstallAsync(new ReleaseAssetDownloader(http),
            (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }, null, cancellation.Token));
        Assert.Equal(previous, store.ResolveRuntime());
    }

    [Fact]
    public async Task HealthUsesFinalCachePathAndInstallationDoesNotMoveAnOpenedExecutable()
    {
        var (archive, manifest) = Fixture();
        using var http = Client(manifest, archive);
        var store = Store();
        var expected = Path.Combine(_directory, "managed", CopilotComponentManifest.SdkVersion,
            "win-x64", "0.1.13", "copilot-runtime.exe");
        FileStream? openedRuntime = null;
        try
        {
            await store.InstallAsync(new ReleaseAssetDownloader(http), (runtime, _) =>
            {
                Assert.Equal(expected, runtime);
                Assert.False(store.HasReceipt);
                Assert.Null(store.ResolveRuntime());
                openedRuntime = new FileStream(runtime, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.CompletedTask;
            }, null, TestContext.Current.CancellationToken);
            Assert.NotNull(openedRuntime);
            Assert.Equal(openedRuntime.Name, store.ResolveRuntime());
        }
        finally { openedRuntime?.Dispose(); }
        await store.RemoveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(File.Exists(expected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledStartupNeverSelectsTheNewCacheDirectory(bool cancel)
    {
        var (archive, manifest) = Fixture();
        using var http = Client(manifest, archive);
        using var cancellation = new CancellationTokenSource();
        var store = Store();
        Task Install() => store.InstallAsync(new ReleaseAssetDownloader(http), (runtime, _) =>
        {
            Assert.True(File.Exists(runtime));
            Assert.False(store.HasReceipt);
            Assert.Null(store.ResolveRuntime());
            if (!cancel) throw new InvalidOperationException("Synthetic startup failure.");
            cancellation.Cancel();
            return Task.CompletedTask;
        }, null, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Install);
        else await Assert.ThrowsAsync<InvalidOperationException>(Install);
        Assert.False(store.HasReceipt);
        Assert.Null(store.ResolveRuntime());
        Assert.False(Directory.Exists(Path.Combine(_directory, "managed", CopilotComponentManifest.SdkVersion,
            "win-x64", "0.1.13")));
    }

    [Fact]
    public async Task BusyAnalysisBlocksInstallAndRemoveAndArbitraryPathsCannotBeDeleted()
    {
        var (archive, manifest) = Fixture();
        using var http = Client(manifest, archive);
        var store = new CopilotComponentStore(Path.Combine(_directory, "managed"), "0.1.13", "win-x64", canChange: () => false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(new ReleaseAssetDownloader(http),
            (_, _) => Task.CompletedTask, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => SafeComponentPaths.DeleteTree(Path.Combine(_directory, "managed"), _directory));
        Assert.Throws<InvalidDataException>(() => SafeComponentPaths.Child(_directory, "../images"));
    }

    [Fact]
    public async Task DownloadRejectsDeclaredOrActualExcessBytesAndNonOfficialRedirects()
    {
        var (archive, manifest) = Fixture();
        using var http = Client(manifest, [.. archive, 42]);
        var store = Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(new ReleaseAssetDownloader(http),
            (_, _) => Task.CompletedTask, null, TestContext.Current.CancellationToken));
        using var chunked = new HttpClient(new Handler((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                ? new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(manifest, ComponentJson.Options))
                : new ChunkedContent([.. archive, 42]),
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(new ReleaseAssetDownloader(chunked),
            (_, _) => Task.CompletedTask, null, TestContext.Current.CancellationToken));
        using var redirected = new HttpClient(new Handler((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://github.com/not-https"),
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ReleaseAssetDownloader(redirected).ReadManifestAsync(
            "0.1.13", "manifest.json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovingDamagedManagedFilesIsPossibleButUnknownImagesAreNotDeleted()
    {
        var (archive, manifest) = Fixture();
        using var http = Client(manifest, archive);
        var store = Store();
        await store.InstallAsync(new ReleaseAssetDownloader(http), (_, _) => Task.CompletedTask, null,
            TestContext.Current.CancellationToken);
        var runtime = store.ResolveRuntime()!;
        await File.WriteAllTextAsync(runtime, "damaged", TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => store.ResolveRuntime());
        var image = Path.Combine(Path.GetDirectoryName(runtime)!, "user-image.png");
        await File.WriteAllTextAsync(image, "user image", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(File.Exists(image));
        File.Delete(image);
        await store.RemoveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(File.Exists(runtime));
    }

    [Fact]
    public async Task RedirectsMustStayOnOfficialHttpsHostsAndHaveABoundedCount()
    {
        var calls = 0;
        using var unsafeRedirect = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = new Uri("https://not-hotshot.example/component.zip");
            return response;
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ReleaseAssetDownloader(unsafeRedirect).ReadManifestAsync(
            "0.1.13", "manifest.json", TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        calls = 0;
        using var loop = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = request.RequestUri;
            return response;
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ReleaseAssetDownloader(loop).ReadManifestAsync(
            "0.1.13", "manifest.json", TestContext.Current.CancellationToken));
        Assert.Equal(6, calls);
    }

    private CopilotComponentStore Store(string version = "0.1.13", string sdk = CopilotComponentManifest.SdkVersion) =>
        new(Path.Combine(_directory, "managed"), version, "win-x64", sdk);

    private static DistributionMetadata Metadata(Architecture architecture, string edition) => new()
    {
        Edition = edition, Runtime = UpdateChannel.ForArchitecture(architecture),
        AppVersion = "0.1.13", CopilotSdkVersion = CopilotComponentManifest.SdkVersion,
    };

    private static (byte[] Archive, CopilotComponentManifest Manifest) Fixture(string? extra = null, bool linked = false, int extraCount = 0)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["copilot-runtime.exe"] = Encoding.UTF8.GetBytes("synthetic executable"),
            ["runtime.node"] = Encoding.UTF8.GetBytes("synthetic stdio support"),
        };
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var file in files)
            {
                using var output = zip.CreateEntry(file.Key).Open();
                output.Write(file.Value);
            }
            if (extra is not null)
            {
                var entry = zip.CreateEntry(extra);
                if (linked) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            }
            for (var i = 0; i < extraCount; i++) zip.CreateEntry($"extra-{i}");
        }
        var archive = memory.ToArray();
        return (archive, new()
        {
            AppVersion = "0.1.13", Runtime = "win-x64", CopilotSdkVersion = CopilotComponentManifest.SdkVersion,
            FileName = CopilotComponentManifest.AssetName("0.1.13", "win-x64", "zip"),
            ArchiveBytes = archive.LongLength, UnpackedBytes = files.Values.Sum(bytes => bytes.LongLength),
            Sha256 = Convert.ToHexString(SHA256.HashData(archive)),
            Files = files.Select(file => new CopilotComponentFile
            {
                Path = file.Key, Bytes = file.Value.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(file.Value)),
            }).ToArray(),
        });
    }

    private static HttpClient Client(CopilotComponentManifest manifest, byte[] archive) =>
        new(new Handler((request, _) =>
        {
            Assert.StartsWith("https://github.com/sebastienlevert/hotshot/releases/download/v0.1.13/", request.RequestUri!.AbsoluteUri);
            var bytes = request.RequestUri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                ? JsonSerializer.SerializeToUtf8Bytes(manifest, ComponentJson.Options) : archive;
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        }));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request, cancellationToken));
        }
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();
    }
}
