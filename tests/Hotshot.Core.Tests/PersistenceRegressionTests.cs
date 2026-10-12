using System.Globalization;
using System.IO.Compression;
using Hotshot.Core.History;
using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class PersistenceRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));

    public PersistenceRegressionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private HistoryStore Store(int maxItems = 10) => new(Path.Combine(_directory, "history.json"), maxItems, keepSavedCaptures: true);

    private HistoryItem Capture(string id, bool temporary = false)
    {
        var path = Path.Combine(_directory, id + ".png");
        File.WriteAllBytes(path, [1, 2, 3]);
        return new HistoryItem { Id = id, Path = path, Width = 1, Height = 1, IsTemporary = temporary };
    }

    [Fact]
    public void ReplacingCaptureAtSamePathDoesNotResurrectPreviousIdOnReload()
    {
        var history = Store();
        var original = Capture("original");
        history.Add(original);
        history.Add(new HistoryItem { Id = "replacement", Path = original.Path });

        var reloaded = Store();
        reloaded.Load();

        Assert.Equal("replacement", Assert.Single(history.Items).Id);
        Assert.Equal("replacement", Assert.Single(reloaded.Items).Id);
    }

    [Fact]
    public void ReAddingIdAtDifferentPathKeepsMemoryAndJournalConsistent()
    {
        var history = Store();
        history.Add(Capture("original"));
        var replacement = Capture("replacement");
        replacement.Id = "original";
        history.Add(replacement);

        Assert.Equal(replacement.Path, Assert.Single(history.Items).Path);
        var reloaded = Store();
        reloaded.Load();
        Assert.Equal(replacement.Path, Assert.Single(reloaded.Items).Path);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("trim")]
    [InlineData("prune")]
    public void FailedPersistenceNeverDeletesCaptureArtifacts(string operation)
    {
        var history = Store(maxItems: 1);
        var original = Capture("original", temporary: true);
        original.ThumbnailPath = Path.Combine(_directory, "thumbnail.png");
        File.WriteAllBytes(original.ThumbnailPath, [4, 5, 6]);
        history.Add(original);
        using var lockedJournal = new FileStream(history.CheckpointPath + ".journal", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        if (operation == "remove") history.Remove(original.Id);
        else if (operation == "trim") history.Add(Capture("replacement", temporary: true));
        else
        {
            history.MaxItems = 0;
            history.Prune();
        }

        Assert.NotNull(history.LastError);
        Assert.True(File.Exists(original.Path));
        Assert.True(File.Exists(original.ThumbnailPath));
    }

    [Fact]
    public void RemovalNeverDeletesCaptureReAddedDuringArtifactCleanup()
    {
        var history = Store();
        var original = Capture("original", temporary: true);
        original.ThumbnailPath = Path.Combine(_directory, "thumbnail.png");
        File.WriteAllBytes(original.ThumbnailPath, [4, 5, 6]);
        history.Add(original);
        using var lockedThumbnail = new FileStream(original.ThumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        history.Failed += _ => history.Add(new HistoryItem
        {
            Id = "replacement", Path = original.Path, ThumbnailPath = original.ThumbnailPath, IsTemporary = true,
        });

        history.Remove(original.Id);

        Assert.Equal("replacement", Assert.Single(history.Items).Id);
        Assert.True(File.Exists(original.Path));
    }

    [Fact]
    public void RemovalPreservesThumbnailStillReferencedByAnotherCapture()
    {
        var history = Store();
        var original = Capture("original", temporary: true);
        var remaining = Capture("remaining");
        original.ThumbnailPath = remaining.ThumbnailPath = Path.Combine(_directory, "shared-thumbnail.png");
        File.WriteAllBytes(original.ThumbnailPath, [4, 5, 6]);
        history.Add(original);
        history.Add(remaining);

        history.Remove(original.Id);

        Assert.True(File.Exists(remaining.ThumbnailPath));
        Assert.False(File.Exists(original.Path));
    }

    [Theory]
    [InlineData("""{"upserts":[],"removed":[null]}""")]
    [InlineData("""{"upserts":[],"removed":[""]}""")]
    [InlineData("""{"upserts":[],"removed":[" "]}""")]
    public void InvalidCompletedJournalRemovalPausesWritesAndPreservesEvidence(string transaction)
    {
        var history = Store();
        history.Add(Capture("original"));
        var journal = history.CheckpointPath + ".journal";
        File.WriteAllText(journal, transaction + "\n");
        var evidence = File.ReadAllBytes(journal);

        var reloaded = Store();
        reloaded.Load();

        Assert.NotNull(reloaded.LastError);
        Assert.Equal("original", Assert.Single(reloaded.Items).Id);
        Assert.Throws<IOException>(() => reloaded.Checkpoint());
        Assert.Equal(evidence, File.ReadAllBytes(journal));
    }

    [Fact]
    public void ReloadDiscardsUncommittedRemovalBeforeAppendingNewChanges()
    {
        var history = Store();
        history.Add(Capture("original"));
        using (var lockedJournal = new FileStream(history.CheckpointPath + ".journal", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
            history.Remove("original");
        Assert.NotNull(history.LastError);

        history.Load();
        history.Add(Capture("replacement"));

        var reloaded = Store();
        reloaded.Load();
        Assert.Equal(history.Items.Select(item => item.Id), reloaded.Items.Select(item => item.Id));
        Assert.Equal(2, reloaded.Items.Count);
    }

    [Theory]
    [InlineData("", "capture.png")]
    [InlineData("capture", "")]
    [InlineData(" ", "capture.png")]
    [InlineData("capture", " ")]
    public void InvalidCaptureCannotEnterPersistedHistory(string id, string path)
    {
        var history = Store();
        Assert.Throws<ArgumentException>(() => history.Add(new HistoryItem { Id = id, Path = path }));
        Assert.Empty(history.Items);
        Assert.False(File.Exists(history.CheckpointPath));
    }

    [Fact]
    public void InvalidUpdateCannotReplaceValidCapture()
    {
        var history = Store();
        var original = Capture("original");
        history.Add(original);

        Assert.Throws<ArgumentException>(() => history.Update(new HistoryItem { Id = original.Id, Path = "" }));

        Assert.Equal(original.Path, Assert.Single(history.Items).Path);
        Assert.False(File.Exists(history.CheckpointPath + ".journal"));
    }

    [Fact]
    public void NullBatchEntryCannotPartiallyChangeHistory()
    {
        var history = Store();
        history.Add(Capture("original"));

        Assert.Throws<ArgumentNullException>(() => history.AddRange([Capture("replacement"), null!]));

        Assert.Equal("original", Assert.Single(history.Items).Id);
    }

    [Fact]
    public void SettingsSaveNeverConsumesAnotherWritersStagingFile()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var otherStaging = store.FilePath + ".tmp";
        File.WriteAllText(otherStaging, "another writer");

        store.Save(new AppSettings());

        Assert.True(File.Exists(otherStaging));
        Assert.Equal("another writer", File.ReadAllText(otherStaging));
        Assert.NotNull(store.Load());
    }

    [Fact]
    public void FailedSettingsReplacementPreservesOriginalAndCleansOwnStagingFile()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        store.Save(new AppSettings());
        var original = File.ReadAllBytes(store.FilePath);
        using var lockedSettings = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var changed = new AppSettings();
        changed.General.Theme = AppTheme.Dark;

        var error = Record.Exception(() => store.Save(changed));
        Assert.True(error is IOException or UnauthorizedAccessException);

        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void SettingsReadFailureIsNotMisreportedAsCorruptionOrDefaults()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = new AppSettings();
        settings.General.Theme = AppTheme.Dark;
        store.Save(settings);
        using (var lockedSettings = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => store.Load());
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.False(File.Exists(store.FilePath + ".corrupt"));
        }
        Assert.Equal(AppTheme.Dark, store.Load().General.Theme);
    }

    [Fact]
    public void FailedCorruptSettingsPreservationIsReportedInsteadOfReturningDefaults()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        File.WriteAllText(store.FilePath, "{invalid settings");
        var backup = store.FilePath + ".corrupt";
        File.WriteAllText(backup, "previous corruption evidence");
        using (var lockedBackup = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => store.Load());
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("{invalid settings", File.ReadAllText(store.FilePath));
        Assert.Equal("previous corruption evidence", File.ReadAllText(backup));
    }

    [Fact]
    public void ApplyingPortableSettingsDoesNotNormalizeOrShareItsMutableSections()
    {
        var portable = PortableSettings.From(new AppSettings());
        portable.Gif.FramesPerSecond = 99;
        portable.General.AiExcludedApps = ["private"];
        portable.Export.Presets.Add(new ExportPreset("Invalid", new ExportStyle(Padding: 1000)));
        portable.Library.SavedSearches.Add(new SavedCaptureSearch("Original", "tag:original"));

        var applied = portable.ApplyTo(new AppSettings());

        Assert.Equal(30, applied.Gif.FramesPerSecond);
        Assert.Empty(applied.Export.Presets);
        Assert.Equal(99, portable.Gif.FramesPerSecond);
        Assert.Single(portable.Export.Presets);
        applied.Hotkeys.RegionScreenshot = "Ctrl+Alt+F9";
        applied.General.AiExcludedApps[0] = "changed";
        applied.Library.SavedSearches.Clear();
        Assert.Equal("PrintScreen", portable.Hotkeys.RegionScreenshot);
        Assert.Equal("private", portable.General.AiExcludedApps[0]);
        Assert.Single(portable.Library.SavedSearches);
    }

    [Fact]
    public void InvalidSyncModeCannotChangeConfigurationOrSharedPreferences()
    {
        var cloud = Path.Combine(_directory, "cloud");
        Directory.CreateDirectory(cloud);
        var local = Path.Combine(_directory, "local", "settings.json");
        var store = new RoamingSettingsStore(local, () => cloud);
        var settings = store.Load();
        var configuration = Path.Combine(Path.GetDirectoryName(local)!, "settings-sync.json");
        var configurationBefore = File.ReadAllBytes(configuration);
        var sharedBefore = File.ReadAllBytes(store.SharedFilePath!);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Configure(settings, (SettingsSyncMode)123, null));

        Assert.Equal(SettingsSyncMode.Automatic, store.Mode);
        Assert.Equal(configurationBefore, File.ReadAllBytes(configuration));
        Assert.Equal(sharedBefore, File.ReadAllBytes(store.SharedFilePath!));
    }

    [Fact]
    public async Task AlreadyCanceledEmptyRestoreDoesNotCreateFilesOrPersistHistory()
    {
        var archive = Path.Combine(_directory, "empty.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("history.json").Open()))
            writer.Write("[]");
        var history = Store();
        var paths = new AppPaths(Path.Combine(_directory, "data"));
        paths.EnsureCreated();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LibraryMaintenance.RestoreAsync(
            archive, _directory, history, paths, originals: false, cancellation.Token));

        Assert.False(File.Exists(history.CheckpointPath));
        Assert.Empty(Directory.GetDirectories(_directory, "Restored-*"));
    }

    [Fact]
    public void CorruptionEvidenceTimestampUsesGregorianCalendarInEveryCulture()
    {
        var history = Store();
        history.Add(Capture("original"));
        File.WriteAllText(history.CheckpointPath, "invalid history");
        var previous = CultureInfo.CurrentCulture;
        var started = DateTime.UtcNow;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            history.Load();
        }
        finally { CultureInfo.CurrentCulture = previous; }

        var evidence = Assert.Single(Directory.GetFiles(_directory, "history.json.corrupt-*"));
        var timestamp = Path.GetFileName(evidence)["history.json.corrupt-".Length..].Split('-')[0];
        var recorded = DateTime.ParseExact(timestamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        Assert.InRange(recorded, started.AddSeconds(-1), DateTime.UtcNow);
    }

    [Fact]
    public async Task RestoreDirectoryTimestampUsesGregorianCalendarInEveryCulture()
    {
        var archive = Path.Combine(_directory, "empty.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("history.json").Open()))
            writer.Write("[]");
        var paths = new AppPaths(Path.Combine(_directory, "data"));
        paths.EnsureCreated();
        var previous = CultureInfo.CurrentCulture;
        var started = DateTime.Now;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            await LibraryMaintenance.RestoreAsync(archive, _directory, Store(), paths,
                originals: false, TestContext.Current.CancellationToken);
        }
        finally { CultureInfo.CurrentCulture = previous; }

        var restored = Assert.Single(Directory.GetDirectories(_directory, "Restored-*"));
        var timestamp = Path.GetFileName(restored).Substring("Restored-".Length, "yyyyMMdd-HHmmss".Length);
        var recorded = DateTime.ParseExact(timestamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        Assert.InRange(recorded, started.AddSeconds(-1), DateTime.Now);
    }
}
