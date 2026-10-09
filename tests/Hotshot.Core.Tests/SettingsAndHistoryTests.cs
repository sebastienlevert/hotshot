using Hotshot.Core.History;
using Hotshot.Core.Hotkeys;
using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class SettingsAndHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hotshot-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsAndHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Defaults_MatchProductDecisions()
    {
        var s = new AppSettings().Normalize();
        Assert.Equal("PrintScreen", s.Hotkeys.RegionScreenshot);
        Assert.Equal("Ctrl+PrintScreen", s.Hotkeys.MonitorScreenshot);
        Assert.Equal("Shift+PrintScreen", s.Hotkeys.AllMonitorsScreenshot);
        Assert.Equal("Ctrl+Shift+PrintScreen", s.Hotkeys.ToggleRecording);
        Assert.Equal("{yyyy}/{MM}/{timestamp}", s.Naming.ScreenshotPattern);
        Assert.True(s.General.StartWithWindows);
        Assert.True(s.General.CopyToClipboard);
        Assert.True(s.General.SaveToFile);
        Assert.False(s.General.ShowPreview);
        Assert.False(s.General.OpenEditorAfterCapture);
        Assert.EndsWith("Hotshot", s.General.SaveFolder);
        Assert.Equal(4, s.Hotkeys.GetBindings().Count());
    }

    [Fact]
    public void SettingsStore_RoundTrips_AndRecoversFromCorruption()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var s = store.Load();
        s.Hotkeys.Set(HotkeyAction.RecordGif, "Ctrl+Alt+G");
        s.Recording.Quality = RecordingQuality.Maximum;
        s.Gif.FramesPerSecond = 99;
        store.Save(s);

        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"maximum\"", json, StringComparison.OrdinalIgnoreCase);

        var loaded = store.Load();
        Assert.Equal("Ctrl+Alt+G", loaded.Hotkeys.RecordGif);
        Assert.Equal(RecordingQuality.Maximum, loaded.Recording.Quality);
        Assert.Equal(30, loaded.Gif.FramesPerSecond);

        File.WriteAllText(store.FilePath, "{ not json");
        var recovered = store.Load();
        Assert.Equal("PrintScreen", recovered.Hotkeys.RegionScreenshot);
        Assert.True(File.Exists(store.FilePath + ".corrupt"));
    }

    [Fact]
    public void Settings_MissingSectionsAreFilled()
    {
        var s = SettingsStore.Deserialize("{\"version\":1,\"general\":{\"saveFolder\":\"\"}}");
        Assert.NotNull(s.Hotkeys);
        Assert.False(string.IsNullOrEmpty(s.General.SaveFolder));
    }

    [Fact]
    public void Settings_KeepLegacyCapturesSilent_AndRoundTripTheme()
    {
        var settings = SettingsStore.Deserialize("""
            {"general":{"showPreview":true,"openEditorAfterCapture":true,"theme":"dark"}}
            """);
        Assert.False(settings.General.ShowPreview);
        Assert.False(settings.General.OpenEditorAfterCapture);
        Assert.Equal(AppTheme.Dark, settings.General.Theme);
        Assert.Equal(AppTheme.Dark, SettingsStore.Deserialize(SettingsStore.Serialize(settings)).General.Theme);
    }

    [Fact]
    public void History_AddsNewestFirst_TrimsAndPrunes()
    {
        var store = new HistoryStore(Path.Combine(_dir, "history.json"), maxItems: 3);
        var files = Enumerable.Range(0, 5).Select(i =>
        {
            var p = Path.Combine(_dir, $"f{i}.png");
            File.WriteAllText(p, "x");
            return p;
        }).ToArray();

        var thumb = Path.Combine(_dir, "thumb0.png");
        File.WriteAllText(thumb, "t");
        store.Add(new HistoryItem { Path = files[0], ThumbnailPath = thumb });
        foreach (var f in files.Skip(1))
        {
            store.Add(new HistoryItem { Path = f });
        }

        Assert.Equal([files[4], files[3], files[2]], store.Items.Select(i => i.Path));
        Assert.False(File.Exists(thumb));

        File.Delete(files[3]);
        var reloaded = new HistoryStore(Path.Combine(_dir, "history.json"), 3);
        reloaded.Load();
        Assert.Equal([files[4], files[2]], reloaded.Items.Select(i => i.Path));
    }

    [Fact]
    public void History_ReAddingSamePathMovesToTop()
    {
        var store = new HistoryStore(Path.Combine(_dir, "history.json"), 10);
        store.Add(new HistoryItem { Path = "a.png" });
        store.Add(new HistoryItem { Path = "b.png" });
        store.Add(new HistoryItem { Path = "A.PNG" });
        Assert.Equal(["A.PNG", "b.png"], store.Items.Select(i => i.Path));
    }

    [Fact]
    public void History_PreservesCaptureNamingContext()
    {
        var path = Path.Combine(_dir, "recording.mp4");
        File.WriteAllText(path, "test");
        var historyPath = Path.Combine(_dir, "history.json");
        var createdAt = DateTimeOffset.Now.AddMinutes(-2);
        var store = new HistoryStore(historyPath, 10);
        store.Add(new HistoryItem
        {
            Path = path,
            Kind = CaptureKind.Recording,
            CreatedAt = createdAt,
            AppName = "test-app",
            WindowTitle = "Synthetic capture",
            MonitorIndex = 2,
        });

        var reloaded = new HistoryStore(historyPath, 10);
        reloaded.Load();
        var item = Assert.Single(reloaded.Items);
        Assert.Equal(createdAt, item.CreatedAt);
        Assert.Equal("test-app", item.AppName);
        Assert.Equal("Synthetic capture", item.WindowTitle);
        Assert.Equal(2, item.MonitorIndex);
    }
}
