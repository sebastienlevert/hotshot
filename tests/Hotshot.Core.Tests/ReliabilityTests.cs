using Hotshot.Core.Descriptions;
using Hotshot.Core.History;
using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class ReliabilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hotshot-reliability-" + Guid.NewGuid().ToString("N"));
    public ReliabilityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void DurableLibraryKeepsSavedFilesAndTrimsOnlyTemporaryEntries()
    {
        var history = new HistoryStore(Path.Combine(_dir, "history.json"), 1, keepSavedCaptures: true);
        foreach (var id in new[] { "saved1", "saved2", "temporary1", "temporary2" })
        {
            var path = Path.Combine(_dir, id + ".png");
            File.WriteAllText(path, "test");
            history.Add(new HistoryItem { Id = id, Path = path, IsTemporary = id.StartsWith("temporary", StringComparison.Ordinal) });
        }
        Assert.Equal(3, history.Items.Count);
        Assert.False(File.Exists(Path.Combine(_dir, "temporary1.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "saved1.png")));
    }

    [Fact]
    public void CorruptHistoryIsPreservedAndLastValidBackupRecovered()
    {
        var path = Path.Combine(_dir, "history.json");
        var image = Path.Combine(_dir, "image.png");
        File.WriteAllText(image, "test");
        var history = new HistoryStore(path, 10, true);
        history.Add(new HistoryItem { Id = "saved", Path = image });
        history.UpdateOrganization("saved", true, ["test"]);
        File.WriteAllText(path, "invalid json");
        var recovered = new HistoryStore(path, 10, true);
        recovered.Load();
        Assert.Equal("saved", Assert.Single(recovered.Items).Id);
        Assert.NotNull(recovered.LastError);
        Assert.Single(Directory.GetFiles(_dir, "history.json.corrupt-*"));
        recovered.UpdateOrganization("saved", true, ["recovered"]);
        var reloaded = new HistoryStore(path, 10, true);
        reloaded.Load();
        Assert.Contains("recovered", Assert.Single(reloaded.Items).Tags);
    }

    [Fact]
    public void PersistenceFailureIsVisibleAndDoesNotDeleteCapture()
    {
        var path = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(path, "test");
        var history = new HistoryStore(Path.Combine(path, "history.json"), 10, true);
        string? error = null;
        history.Failed += value => error = value;
        history.Add(new HistoryItem { Path = "saved.png" });
        Assert.NotNull(error);
        Assert.NotNull(history.LastError);
        Assert.Single(history.Items);
    }

    [Fact]
    public void SearchSupportsProjectsTagsQuotedTextAndOldSubstringBehavior()
    {
        var item = new HistoryItem { Path = "dashboard.png", Project = "Hotshot native", Tags = ["bug"],
            RecognizedText = "unexpected error here" };
        Assert.True(item.MatchesSearch("project:Hotshot tag:bug \"unexpected error\""));
        Assert.True(item.MatchesSearch("dash error"));
        Assert.False(item.MatchesSearch("project:Other"));
    }

    [Fact]
    public void AnalysisReviewAndExclusionsNeverUploadAutomatically()
    {
        var settings = new GeneralSettings { DescribeScreenshots = true, AiExcludedApps = ["private.exe"] };
        Assert.False(CaptureAnalysisPolicy.CanAnalyzeAutomatically(settings, "private"));
        Assert.False(CaptureAnalysisPolicy.RequiresReview(settings, "private"));
        Assert.True(CaptureAnalysisPolicy.CanAnalyzeAutomatically(settings, "editor"));
        Assert.False(CaptureAnalysisPolicy.RequiresReview(settings, "editor"));
        settings.ReviewBeforeAiUpload = true;
        Assert.False(CaptureAnalysisPolicy.CanAnalyzeAutomatically(settings, "editor"));
        Assert.True(CaptureAnalysisPolicy.RequiresReview(settings, "editor"));
        Assert.False(CaptureAnalysisPolicy.RequiresReview(settings, "private"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Video frame")]
    [InlineData("Imported image")]
    [InlineData("Scrolling capture")]
    [InlineData(" VIDEO FRAME ")]
    public void UnknownSourceAlwaysRequiresReviewWhenDescriptionsAreEnabled(string? app)
    {
        var settings = new GeneralSettings { DescribeScreenshots = true, ReviewBeforeAiUpload = false };
        Assert.False(CaptureAnalysisPolicy.HasKnownSource(app));
        Assert.False(CaptureAnalysisPolicy.CanAnalyzeAutomatically(settings, app));
        Assert.True(CaptureAnalysisPolicy.RequiresReview(settings, app));
        settings.DescribeScreenshots = false;
        Assert.False(CaptureAnalysisPolicy.CanAnalyzeAutomatically(settings, app));
        Assert.False(CaptureAnalysisPolicy.RequiresReview(settings, app));
    }

    [Fact]
    public void AppRestrictionsDisableBroadCaptureAndFilterTargets()
    {
        var settings = new AgentSettings { AllowedApps = ["editor.exe"] };
        Assert.True(Hotshot.Core.Agents.AgentAccessPolicy.AllowsApp(settings, "editor"));
        Assert.False(Hotshot.Core.Agents.AgentAccessPolicy.AllowsApp(settings, "private"));
        Assert.False(Hotshot.Core.Agents.AgentAccessPolicy.AllowsRegion(settings));
        Assert.False(Hotshot.Core.Agents.AgentAccessPolicy.AllowsMonitor(settings));
    }

    [Fact]
    public void SearchIndexInvalidatesWhenDescriptionsChange()
    {
        var history = new HistoryStore(Path.Combine(_dir, "history.json"), 1, true);
        history.Add(new HistoryItem { Id = "one", Path = "one.png" });
        Assert.Empty(history.Search("revenue"));
        history.UpdateDescription("one", "Revenue", "Updated graph", null);
        Assert.Single(history.Search("revenue"));
        history.UpdateDescription("one", "Costs", "Updated graph", null);
        Assert.Empty(history.Search("revenue"));
    }

    [Fact]
    public async Task DescriptionQueueDeduplicatesAndReportsOverflow()
    {
        var history = new HistoryStore(Path.Combine(_dir, "history.json"), 100, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var descriptions = new ScreenshotDescriptions(history, () => new WaitingProvider(entered), true);
        var first = new HistoryItem { Id = "first", Path = Path.Combine(_dir, "first.png"), AppName = "Synthetic" };
        File.WriteAllBytes(first.Path, [1]);
        history.Add(first);
        var task = descriptions.EnqueueAsync(first);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(task, descriptions.EnqueueAsync(first));
        for (var i = 0; i < ScreenshotDescriptions.QueueCapacity; i++)
            _ = descriptions.EnqueueAsync(new HistoryItem { Id = "queued" + i, Path = first.Path, AppName = first.AppName });
        string? error = null;
        descriptions.Failed += message => error = message;
        Assert.False(await descriptions.EnqueueAsync(new HistoryItem { Id = "overflow", Path = first.Path, AppName = first.AppName }));
        Assert.Contains("queue is full", error!);
        Assert.Equal(ScreenshotDescriptions.QueueCapacity + 1, descriptions.PendingCount);
        descriptions.SetEnabled(false);
        Assert.False(await task);
    }

    private sealed class WaitingProvider(TaskCompletionSource entered) : ICaptureDescriptionProvider
    {
        public async Task<CaptureDescription> DescribeAsync(byte[] png, CancellationToken token)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new CaptureDescription("Never", "Never");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
