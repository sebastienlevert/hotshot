using System.Text.Json;
using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class OneDriveSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hotshot-onedrive-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _cloud;
    public OneDriveSettingsTests() { _cloud = Path.Combine(_directory, "OneDrive"); Directory.CreateDirectory(_cloud); }
    public void Dispose() => Directory.Delete(_directory, true);
    private string Local(string machine) => Path.Combine(_directory, machine, "settings.json");
    private RoamingSettingsStore Store(string machine) => new(Local(machine), () => _cloud);

    [Fact]
    public void FreshProfileIsReadyToCaptureAndUsesOneDriveWithoutSetupOrPermissionGrants()
    {
        var store = Store("fresh");
        var settings = store.Load();
        Assert.Equal(SettingsSyncMode.Automatic, store.Mode);
        Assert.NotNull(store.SharedFilePath);
        Assert.True(File.Exists(store.SharedFilePath));
        Assert.True(settings.General.CopyToClipboard);
        Assert.True(settings.General.SaveToFile);
        Assert.False(settings.General.ShowPreview);
        Assert.False(settings.General.OpenEditorAfterCapture);
        Assert.True(settings.Capture.SnapToWindows);
        Assert.Equal(AppTheme.System, settings.General.Theme);
        Assert.Equal(RecordingTimerSize.Mini, settings.Recording.TimerSize);
        Assert.Equal(RecordingTimerPlacement.TopRight, settings.Recording.TimerPlacement);
        Assert.False(settings.General.DescribeScreenshots);
        Assert.False(settings.General.RecognizeScreenshotText);
        Assert.False(settings.Agents.Enabled);
        Assert.False(settings.Agents.AllowHistoryAccess);
        Assert.Equal(AppPaths.DefaultSaveFolder, settings.General.SaveFolder);
    }

    [Fact]
    public void MigratesPortablePreferencesAndNeverUploadsDeviceDataOrPermissions()
    {
        var settings = new AppSettings();
        settings.General.Theme = AppTheme.Dark;
        settings.General.SaveFolder = Path.Combine(_directory, "device-captures");
        settings.General.Counter = 123;
        settings.General.FirstRunCompleted = true;
        settings.General.DescribeScreenshots = true;
        settings.General.ReviewBeforeAiUpload = true;
        settings.General.CopilotRuntimePath = Path.Combine(_directory, "runtime.exe");
        settings.Agents.Enabled = settings.Agents.AllowHistoryAccess = true;
        settings.Recording.MicrophoneDeviceId = "private-device-id";
        settings.Layout.Windows["workspace"] = new WindowLayout(10, 20, 500, 400, 2);
        settings.Library.Baselines.Add(new ProjectBaseline("local-project", "private-capture-id"));
        new SettingsStore(Local("A")).Save(settings);
        var first = Store("A");
        var loaded = first.Load();
        Assert.Equal(AppTheme.Dark, loaded.General.Theme);
        Assert.True(loaded.Agents.Enabled);
        var shared = File.ReadAllText(first.SharedFilePath!);
        foreach (var forbidden in new[] { "device-captures", "private-device-id", "private-capture-id", "runtime.exe",
            "describeScreenshots", "reviewBeforeAiUpload", "allowHistoryAccess", "\"counter\"", "\"layout\"", "saveFolder", "firstRunCompleted" })
            Assert.DoesNotContain(forbidden, shared, StringComparison.OrdinalIgnoreCase);
        var second = Store("B");
        var elsewhere = second.Load();
        Assert.Equal(AppTheme.Dark, elsewhere.General.Theme);
        Assert.False(elsewhere.General.DescribeScreenshots);
        Assert.False(elsewhere.Agents.Enabled);
        Assert.Null(elsewhere.Recording.MicrophoneDeviceId);
        Assert.Equal(AppPaths.DefaultSaveFolder, elsewhere.General.SaveFolder);
        Assert.Empty(elsewhere.Layout.Windows);
    }

    [Fact]
    public void NonconflictingChangesFromTwoMachinesAreMergedAtFieldLevel()
    {
        var a = Store("A"); var first = a.Load();
        var b = Store("B"); var second = b.Load();
        first.General.Theme = AppTheme.Dark;
        a.Save(first);
        second.Hotkeys.RegionScreenshot = "Ctrl+Alt+F9";
        var merged = b.Save(second);
        Assert.Equal(AppTheme.Dark, merged.General.Theme);
        Assert.Equal("Ctrl+Alt+F9", merged.Hotkeys.RegionScreenshot);
        var refreshed = a.Refresh(first);
        Assert.NotNull(refreshed);
        Assert.Equal("Ctrl+Alt+F9", refreshed.Hotkeys.RegionScreenshot);
    }

    [Fact]
    public void SimultaneousSameFieldEditsArePreservedAndRequireResolution()
    {
        var a = Store("A"); var first = a.Load();
        var b = Store("B"); var second = b.Load();
        first.General.Theme = AppTheme.Dark; a.Save(first);
        second.General.Theme = AppTheme.Light;
        var before = File.ReadAllBytes(a.SharedFilePath!);
        Assert.Throws<SettingsSyncConflictException>(() => b.Save(second));
        Assert.True(b.HasConflict);
        Assert.Equal(before, File.ReadAllBytes(a.SharedFilePath!));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Local("B"))!, "*.local-conflict-*.json"));
        var chosen = b.ResolveConflict(second, useThisMachine: false);
        Assert.Equal(AppTheme.Dark, chosen.General.Theme);
        Assert.False(b.HasConflict);
    }

    [Fact]
    public void OfflineEditsRemainLocalAndMergeWhenOneDriveReturns()
    {
        var a = Store("A"); var first = a.Load();
        var b = Store("B"); var second = b.Load();
        var hidden = _cloud + "-offline";
        Directory.Move(_cloud, hidden);
        try { first.General.Theme = AppTheme.Dark; a.Save(first); }
        finally { Directory.Move(hidden, _cloud); }
        second.Hotkeys.RegionScreenshot = "Ctrl+Alt+F9"; b.Save(second);
        var updated = a.Refresh(first)!;
        Assert.Equal(AppTheme.Dark, updated.General.Theme);
        Assert.Equal("Ctrl+Alt+F9", updated.Hotkeys.RegionScreenshot);
        var c = Store("C").Load();
        Assert.Equal(AppTheme.Dark, c.General.Theme);
        Assert.Equal("Ctrl+Alt+F9", c.Hotkeys.RegionScreenshot);
    }

    [Fact]
    public void DeviceOnlyChangesDoNotRewriteOrRoamSharedPreferences()
    {
        var a = Store("A"); var settings = a.Load();
        var before = File.ReadAllBytes(a.SharedFilePath!);
        var modified = File.GetLastWriteTimeUtc(a.SharedFilePath!);
        settings.General.Counter = 999;
        settings.Agents.Enabled = true;
        settings.General.SaveFolder = Path.Combine(_directory, "new-local-folder");
        a.Save(settings);
        Assert.Equal(before, File.ReadAllBytes(a.SharedFilePath!));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(a.SharedFilePath!));
        var elsewhere = Store("B").Load();
        Assert.Equal(0, elsewhere.General.Counter);
        Assert.False(elsewhere.Agents.Enabled);
    }

    [Fact]
    public void MalformedSharedFileIsNotReplacedAndLocalCacheRemainsActive()
    {
        var store = Store("A"); var settings = store.Load();
        settings.General.Theme = AppTheme.Dark;
        store.Save(settings);
        File.WriteAllText(store.SharedFilePath!, "{invalid shared json");
        settings.Hotkeys.RegionScreenshot = "Ctrl+Alt+F9";
        var result = store.Save(settings);
        Assert.Equal("Ctrl+Alt+F9", result.Hotkeys.RegionScreenshot);
        Assert.Equal("{invalid shared json", File.ReadAllText(store.SharedFilePath!));
        var restart = Store("A").Load();
        Assert.Equal(AppTheme.Dark, restart.General.Theme);
        Assert.Equal("Ctrl+Alt+F9", restart.Hotkeys.RegionScreenshot);
    }

    [Fact]
    public void LocalModeDoesNotDeleteSharedFileAndSurvivesRestart()
    {
        var store = Store("A"); var settings = store.Load();
        var shared = store.SharedFilePath!;
        store.Configure(settings, SettingsSyncMode.Local, null);
        settings.General.Theme = AppTheme.Light; store.Save(settings);
        Assert.True(File.Exists(shared));
        var restart = Store("A"); var local = restart.Load();
        Assert.Equal(SettingsSyncMode.Local, restart.Mode);
        Assert.Null(restart.SharedFilePath);
        Assert.Equal(AppTheme.Light, local.General.Theme);
    }

    [Fact]
    public void SharedDocumentCannotEnableAgentOrCloudPermissionsEvenIfFieldsAreInjected()
    {
        var store = Store("A"); store.Load();
        var json = File.ReadAllText(store.SharedFilePath!);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["preferences"]!["general"]!["describeScreenshots"] = true;
        node["preferences"]!["agents"] = System.Text.Json.Nodes.JsonNode.Parse("""{"enabled":true,"allowHistoryAccess":true}""");
        File.WriteAllText(store.SharedFilePath!, node.ToJsonString());
        var machine = Store("B").Load();
        Assert.False(machine.General.DescribeScreenshots);
        Assert.False(machine.Agents.Enabled);
    }

    [Fact]
    public void SharedChangesPreserveExistingDevicePermissionsPathsAndRuntime()
    {
        var a = Store("A"); var settings = a.Load();
        var local = new AppSettings();
        local.General.SaveFolder = Path.Combine(_directory, "B-captures");
        local.General.DescribeScreenshots = true;
        local.Agents.Enabled = true;
        local.Recording.MicrophoneDeviceId = "B-mic";
        new SettingsStore(Local("B")).Save(local);
        var b = Store("B"); var device = b.Load();
        settings.General.Theme = AppTheme.Dark; a.Save(settings);
        var updated = b.Refresh(device)!;
        Assert.True(updated.General.DescribeScreenshots);
        Assert.True(updated.Agents.Enabled);
        Assert.Equal(local.General.SaveFolder, updated.General.SaveFolder);
        Assert.Equal("B-mic", updated.Recording.MicrophoneDeviceId);
    }

    [Fact]
    public void OneDriveConflictCopiesPauseSharedWritesInsteadOfChoosingSilently()
    {
        var store = Store("A"); var settings = store.Load();
        File.Copy(store.SharedFilePath!, Path.Combine(Path.GetDirectoryName(store.SharedFilePath!)!, "settings-other-machine.json"));
        var before = File.ReadAllBytes(store.SharedFilePath!);
        settings.General.Theme = AppTheme.Dark;
        Assert.Throws<SettingsSyncConflictException>(() => store.Save(settings));
        Assert.Equal(before, File.ReadAllBytes(store.SharedFilePath!));
        Assert.True(store.HasConflict);
    }

    [Fact]
    public void ExplicitInvalidFileResolutionPreservesTheOriginalAndKnownBackup()
    {
        var store = Store("A"); var local = store.Load();
        local.General.Theme = AppTheme.Dark; store.Save(local);
        var previousBackup = File.ReadAllBytes(store.SharedFilePath! + ".backup");
        File.WriteAllText(store.SharedFilePath!, """{"schemaVersion":"broken","preferences":null}""");
        Assert.Null(store.Refresh(local));
        Assert.True(store.HasConflict);
        var resolved = store.ResolveConflict(local, useThisMachine: true);
        Assert.Equal(AppTheme.Dark, resolved.General.Theme);
        Assert.Equal(previousBackup, File.ReadAllBytes(store.SharedFilePath! + ".backup"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.SharedFilePath!)!, "settings.json.invalid-*"));
        Assert.Equal(AppTheme.Dark, Store("B").Load().General.Theme);
    }

    [Theory]
    [InlineData("""{"schemaVersion":"invalid","preferences":{}}""")]
    [InlineData("""{"schemaVersion":2,"preferences":{}}""")]
    [InlineData("""{"schemaVersion":1,"preferences":null}""")]
    [InlineData("""{"schemaVersion":1,"preferences":{}}""")]
    public void UnsupportedSharedSchemasNeverResetTheLocalSettings(string json)
    {
        var store = Store("A"); var settings = store.Load();
        settings.General.Theme = AppTheme.Dark; store.Save(settings);
        File.WriteAllText(store.SharedFilePath!, json);
        Assert.Equal(AppTheme.Dark, Store("A").Load().General.Theme);
        Assert.Equal(json, File.ReadAllText(store.SharedFilePath!));
    }

    [Fact]
    public void OfflineEditsBeforeFirstConnectionRequireExplicitResolution()
    {
        var connected = Store("A");
        var remote = connected.Load();
        remote.General.Theme = AppTheme.Dark;
        connected.Save(remote);
        var hidden = _cloud + "-offline";
        Directory.Move(_cloud, hidden);
        var offline = Store("B");
        AppSettings local;
        try
        {
            local = offline.Load();
            local.Hotkeys.RegionScreenshot = "Ctrl+Alt+F9";
            offline.Save(local);
        }
        finally { Directory.Move(hidden, _cloud); }
        var before = File.ReadAllBytes(connected.SharedFilePath!);
        Assert.Null(offline.Refresh(local));
        Assert.True(offline.HasConflict);
        Assert.Equal(before, File.ReadAllBytes(connected.SharedFilePath!));
        Assert.Equal("Ctrl+Alt+F9", new SettingsStore(Local("B")).Load().Hotkeys.RegionScreenshot);
        Assert.Equal(AppTheme.Dark, offline.ResolveConflict(local, useThisMachine: false).General.Theme);
    }

    [Fact]
    public void OversizedSharedFilesArePreservedWithoutResettingLocalPreferences()
    {
        var store = Store("A");
        var local = store.Load();
        local.General.Theme = AppTheme.Dark;
        store.Save(local);
        var oversized = new string(' ', 2 * 1024 * 1024 + 1);
        File.WriteAllText(store.SharedFilePath!, oversized);
        Assert.Null(store.Refresh(local));
        Assert.True(store.HasConflict);
        Assert.Equal(AppTheme.Dark, Store("A").Load().General.Theme);
        Assert.Equal(oversized, File.ReadAllText(store.SharedFilePath!));
    }

    [Fact]
    public void InvalidSyncConfigurationKeepsLocalPreferencesAndBothDocumentsUntouched()
    {
        var store = Store("A");
        var local = store.Load();
        local.General.Theme = AppTheme.Dark;
        store.Save(local);
        var configuration = Path.Combine(Path.GetDirectoryName(Local("A"))!, "settings-sync.json");
        File.WriteAllText(configuration, "{invalid configuration");
        var sharedBefore = File.ReadAllBytes(store.SharedFilePath!);
        var restarted = Store("A");
        var restored = restarted.Load();
        Assert.Equal(AppTheme.Dark, restored.General.Theme);
        restored.Hotkeys.RegionScreenshot = "Ctrl+Alt+F9";
        restarted.Save(restored);
        Assert.Equal("{invalid configuration", File.ReadAllText(configuration));
        Assert.Equal(sharedBefore, File.ReadAllBytes(store.SharedFilePath!));
        Assert.Equal("Ctrl+Alt+F9", new SettingsStore(Local("A")).Load().Hotkeys.RegionScreenshot);
    }
}
