using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class LibraryConsolidationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hotshot-consolidation-" + Guid.NewGuid().ToString("N"));
    public LibraryConsolidationTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private HistoryStore Store() => new(Path.Combine(_directory, "history.json"), 100, true);
    private HistoryItem Item(string id) => new() { Id = id, Path = Path.Combine(_directory, id + ".png"), Width = 2, Height = 2 };

    [Fact]
    public void JournalPersistsSmallMutationsWithoutRewritingCheckpoint()
    {
        var store = Store();
        store.Add(Item("one"));
        var checkpoint = File.ReadAllBytes(store.CheckpointPath);
        store.Add(Item("two"));
        store.UpdateOrganization("one", true, ["tag"], "Project");
        Assert.Equal(checkpoint, File.ReadAllBytes(store.CheckpointPath));
        var loaded = Store(); loaded.Load();
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal("Project", loaded.Items.Single(item => item.Id == "one").Project);
        loaded.Checkpoint();
        Assert.False(File.Exists(loaded.CheckpointPath + ".journal"));
    }

    [Fact]
    public void InterruptedJournalTailRecoversCompletedTransactionsAndRetainsEvidence()
    {
        var store = Store(); store.Add(Item("one")); store.Add(Item("two"));
        File.AppendAllText(store.CheckpointPath + ".journal", "{\"Upserts\":");
        var loaded = Store(); loaded.Load();
        Assert.Equal(2, loaded.Items.Count);
        Assert.Single(Directory.GetFiles(_directory, "*.journal.interrupted-*"));
        loaded.Add(Item("three"));
        var restart = Store(); restart.Load();
        Assert.Equal(3, restart.Items.Count);
    }

    [Fact]
    public async Task BackupRestoreCreatesNewFilesAndDoesNotOverwriteExistingIds()
    {
        var paths = new AppPaths(Path.Combine(_directory, "data")); paths.EnsureCreated();
        var store = new HistoryStore(paths.HistoryFile, 100, true);
        var item = Item("capture");
        await File.WriteAllBytesAsync(item.Path, [1, 2, 3], TestContext.Current.CancellationToken);
        store.Add(item);
        await File.WriteAllTextAsync(Path.Combine(paths.OriginalsDirectory, "capture.json"), "private annotations", TestContext.Current.CancellationToken);
        var backup = Path.Combine(_directory, "backup.zip");
        await LibraryMaintenance.BackupAsync(backup, store, paths, false, TestContext.Current.CancellationToken);
        var preview = LibraryMaintenance.PreviewRestore(backup);
        Assert.Equal(1, preview.Captures);
        Assert.False(preview.IncludesOriginals);
        Assert.Equal(1, await LibraryMaintenance.RestoreAsync(backup, _directory, store, paths, false, TestContext.Current.CancellationToken));
        Assert.Equal(2, store.Items.Count);
        Assert.Equal(2, store.Items.Select(item => item.Id).Distinct().Count());
        Assert.All(store.Items, item => Assert.True(File.Exists(item.Path)));
    }

    [Fact]
    public void RestoreRejectsPathsOutsideManifest()
    {
        var backup = Path.Combine(_directory, "unsafe.zip");
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("history.json").Open(), Encoding.UTF8))
                writer.Write("[]");
            zip.CreateEntry("../outside.txt");
        }
        Assert.Throws<InvalidDataException>(() => LibraryMaintenance.PreviewRestore(backup));
    }

    [Fact]
    public void CleanupIsReviewedScopedAndDetectsChangedFiles()
    {
        var paths = new AppPaths(Path.Combine(_directory, "data")); paths.EnsureCreated();
        var original = Path.Combine(paths.OriginalsDirectory, "capture.png");
        File.WriteAllText(original, "private");
        var reviewed = LibraryMaintenance.PreviewCleanup(paths, [], false);
        Assert.Single(reviewed);
        File.AppendAllText(original, "changed");
        Assert.Throws<IOException>(() => LibraryMaintenance.DeleteReviewed(paths, reviewed));
        Assert.True(File.Exists(original));
        Assert.Throws<InvalidOperationException>(() => LibraryMaintenance.DeleteReviewed(paths, [new CleanupFile(Path.Combine(_directory, "outside.png"), 0, "bad")]));
    }

    [Fact]
    public void TenThousandCaptureBenchmarkMeetsBoundedPersistenceAndWarmSearchTargets()
    {
        var store = Store();
        var timer = Stopwatch.StartNew();
        store.AddRange(Enumerable.Range(0, 10000).Select(index => new HistoryItem
        {
            Id = "item" + index, Path = Path.Combine(_directory, $"capture-{index}.png"), Width = 2, Height = 2,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-index), Project = "project" + index % 10,
            RecognizedText = "Synthetic evidence invoice " + index,
        }));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Bulk persistence took {timer.Elapsed}.");
        timer.Restart();
        Assert.Single(store.Search("capture-9999.png"));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Cold search took {timer.Elapsed}.");
        timer.Restart();
        for (var i = 0; i < 100; i++) Assert.Single(store.Search("capture-9999.png"));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"100 warm queries took {timer.Elapsed}.");
        timer.Restart();
        store.UpdateOrganization("item9999", true, ["baseline"]);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Journal update took {timer.Elapsed}.");
        var loaded = Store(); loaded.Load();
        Assert.Equal(10000, loaded.Items.Count);
        Assert.True(loaded.Items.Single(item => item.Id == "item9999").IsFavorite);
    }

    [Fact]
    public void ExportPresetsAndSessionPolicyRoundTripWithoutChangingDefaults()
    {
        var settings = new AppSettings();
        settings.Export.Last = new ExportStyle(1, 800, 12, 4, false, 1);
        settings.Export.Presets.Add(new ExportPreset("Documentation", settings.Export.Last));
        settings.Agents.SessionAccessOnly = true;
        settings.Agents.ApproveRequests = true;
        var clone = settings.Clone();
        Assert.Equal(settings.Export.Last, clone.Export.Last);
        Assert.Equal("Documentation", Assert.Single(clone.Export.Presets).Name);
        Assert.True(clone.Agents.SessionAccessOnly);
        Assert.True(clone.Agents.ApproveRequests);
    }

    [Fact]
    public void CorruptCompletedJournalPausesWritesAndPreservesOriginalEvidence()
    {
        var history = Store(); history.Add(Item("one")); history.Add(Item("two"));
        File.AppendAllText(history.CheckpointPath + ".journal", "invalid transaction\n");
        var before = File.ReadAllBytes(history.CheckpointPath + ".journal");
        var loaded = Store(); loaded.Load();
        Assert.Contains("writes are paused", loaded.LastError!);
        Assert.Equal(2, loaded.Items.Count);
        loaded.Add(Item("three"));
        Assert.Equal(before, File.ReadAllBytes(history.CheckpointPath + ".journal"));
        Assert.Throws<IOException>(() => loaded.Checkpoint());
    }

    [Fact]
    public async Task CanceledBackupLeavesNoPartialArchiveOrTemporaryFiles()
    {
        var paths = new AppPaths(Path.Combine(_directory, "data")); paths.EnsureCreated();
        var history = new HistoryStore(paths.HistoryFile, 100, true);
        var image = Item("one"); File.WriteAllBytes(image.Path, [1, 2, 3]); history.Add(image);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var backup = Path.Combine(_directory, "canceled.zip");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LibraryMaintenance.BackupAsync(backup, history, paths, false, cancellation.Token));
        Assert.False(File.Exists(backup));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task BackupOriginalsAreOptInAndRestoreNeverReusesOriginalPaths()
    {
        var paths = new AppPaths(Path.Combine(_directory, "data")); paths.EnsureCreated();
        var history = new HistoryStore(paths.HistoryFile, 100, true);
        var image = Item("one"); File.WriteAllBytes(image.Path, [1, 2, 3]); history.Add(image);
        File.WriteAllBytes(Path.Combine(paths.OriginalsDirectory, "one.png"), [4, 5, 6]);
        File.WriteAllText(Path.Combine(paths.OriginalsDirectory, "one.json"), "editable");
        var backup = Path.Combine(_directory, "with-originals.zip");
        await LibraryMaintenance.BackupAsync(backup, history, paths, true, TestContext.Current.CancellationToken);
        Assert.True(LibraryMaintenance.PreviewRestore(backup).IncludesOriginals);
        await LibraryMaintenance.RestoreAsync(backup, _directory, history, paths, true, TestContext.Current.CancellationToken);
        var restored = history.Items.Single(item => item.Id != "one");
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(paths.OriginalsDirectory, restored.Id + ".png")));
        Assert.True(File.Exists(Path.Combine(paths.OriginalsDirectory, "one.png")));
    }

    [Fact]
    public void CleanupNeverDeletesCapturesStoredInsideAnArtifactFolder()
    {
        var paths = new AppPaths(Path.Combine(_directory, "data")); paths.EnsureCreated();
        var file = Path.Combine(paths.OriginalsDirectory, "saved.png");
        File.WriteAllText(file, "capture");
        var capture = new HistoryItem { Id = "other-id", Path = file, Width = 2, Height = 2 };
        Assert.Empty(LibraryMaintenance.PreviewCleanup(paths, [capture], true));
        Assert.Throws<InvalidOperationException>(() => LibraryMaintenance.DeleteReviewed(paths,
            [new CleanupFile(file, new FileInfo(file).Length, "old preview")], [file]));
        Assert.True(File.Exists(file));
    }
}
