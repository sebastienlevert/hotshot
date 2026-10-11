using Hotshot.Core.History;
using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class CaptureLibraryTests
{
    [Fact]
    public void SearchMatchesTermsAcrossTextTagsAndApplication()
    {
        var item = new HistoryItem
        {
            Path = "2026-10-11.png", RecognizedText = "Quarterly revenue report",
            Tags = ["work"], AppName = "Excel", Summary = "A chart",
        };
        Assert.True(item.MatchesSearch("work revenue EXCEL"));
        Assert.True(item.MatchesSearch("chart report"));
        Assert.False(item.MatchesSearch("work missing"));
        Assert.Equal(["work", "Personal"], CaptureLibrary.NormalizeTags([" work ", "WORK", "", "Personal"]));
        Assert.Throws<ArgumentException>(() => CaptureLibrary.NormalizeTags([new string('x', 41)]));
    }

    [Fact]
    public void FiltersComposeWithoutChangingTheHistory()
    {
        var now = DateTimeOffset.Now;
        var screenshot = new HistoryItem { Path = "shot.png", CreatedAt = now, IsFavorite = true };
        var recording = new HistoryItem { Path = "video.mp4", Kind = CaptureKind.Recording, CreatedAt = now.AddDays(-10) };
        Assert.True(new CaptureLibraryFilter(FavoritesOnly: true, Type: CaptureTypeFilter.Screenshots, Date: CaptureDateRange.Today).Matches(screenshot, now));
        Assert.False(new CaptureLibraryFilter(FavoritesOnly: true).Matches(recording, now));
        Assert.False(new CaptureLibraryFilter(Date: CaptureDateRange.LastWeek).Matches(recording, now));
        Assert.True(new CaptureLibraryFilter(Type: CaptureTypeFilter.Recordings, Date: CaptureDateRange.LastMonth).Matches(recording, now));
    }

    [Fact]
    public void FavoritesSurviveTrimmingAndStaleOcrCannotRestoreRedactedText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hotshot-library-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new HistoryStore(Path.Combine(directory, "history.json"), 1);
            HistoryItem Add(string name)
            {
                var path = Path.Combine(directory, name + ".png");
                File.WriteAllText(path, "test");
                var item = new HistoryItem { Path = path };
                store.Add(item);
                return item;
            }
            var favorite = Add("favorite");
            store.UpdateOrganization(favorite.Id, true, ["saved"]);
            Add("old");
            Add("new");
            Assert.Equal(2, store.Items.Count);
            Assert.Contains(store.Items, item => item.Id == favorite.Id);
            store.UpdateDescription(favorite.Id, "Secret", "Sensitive content", null);
            store.UpdateRecognizedText(favorite.Id, "Sensitive text", null);
            store.RecordImageChange(favorite.Id, clearDescription: true);
            Assert.Null(favorite.Description);
            Assert.Null(favorite.RecognizedText);
            Assert.False(store.UpdateRecognizedText(favorite.Id, "Stale sensitive text", null, expectedRevision: 0));
            Assert.True(store.UpdateRecognizedText(favorite.Id, "Redacted image", null, expectedRevision: 1));
            var reloaded = new HistoryStore(Path.Combine(directory, "history.json"), 1);
            reloaded.Load();
            Assert.True(reloaded.Items.Single(item => item.Id == favorite.Id).IsFavorite);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NewCaptureFeaturesPreserveExistingDefaults()
    {
        var settings = new AppSettings().Normalize();
        Assert.Equal(0, settings.Capture.DelaySeconds);
        Assert.False(settings.General.RecognizeScreenshotText);
        settings.Capture.DelaySeconds = 90;
        Assert.Equal(30, settings.Normalize().Capture.DelaySeconds);
    }
}
