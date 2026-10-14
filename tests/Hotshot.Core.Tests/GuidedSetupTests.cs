using Hotshot.Core.Settings;
using Hotshot.Core.Setup;

namespace Hotshot.Core.Tests;

public sealed class GuidedSetupTests
{
    private static AppSettings Settings() => new()
    {
        General = new GeneralSettings { SaveFolder = Path.Combine(Environment.CurrentDirectory, "captures") },
    };

    [Fact]
    public void NavigationTracksReviewedSkippedAndBackWithoutWritingSettings()
    {
        var source = Settings();
        var session = new GuidedSetupSession(source);
        Assert.Equal(SetupStep.Welcome, session.CurrentStep);
        Assert.Equal(1, session.StepNumber);
        Assert.False(session.CanGoBack);
        Assert.False(session.CanSkip);
        Assert.True(session.Next(out _));
        Assert.Equal(SetupStepProgress.Reviewed, session.GetProgress(SetupStep.Welcome));
        Assert.True(session.Skip());
        Assert.Equal(SetupStep.Shortcuts, session.CurrentStep);
        Assert.Equal(SetupStepProgress.Skipped, session.GetProgress(SetupStep.Folder));
        Assert.True(session.Back());
        Assert.Equal(SetupStep.Folder, session.CurrentStep);
        Assert.True(session.RecordFolderCheck(session.FolderPath, true, "Writable"));
        Assert.True(session.Next(out _));
        Assert.Equal(SetupStepProgress.Reviewed, session.GetProgress(SetupStep.Folder));
        Assert.False(session.HasChanges);
        Assert.Equal(source.General.SaveFolder, session.FolderPath);
    }

    [Fact]
    public void InvalidOrUncheckedFolderBlocksNextAndStaleProbeCannotValidateANewPath()
    {
        var session = new GuidedSetupSession(Settings());
        session.Next(out _);
        Assert.False(session.Next(out var error));
        Assert.NotNull(error);
        Assert.NotNull(GuidedSetupSession.ValidateFolderPath("relative-folder"));
        Assert.NotNull(GuidedSetupSession.ValidateFolderPath(""));
        var first = session.FolderPath;
        session.RecordFolderCheck(first, true, "Writable");
        session.FolderPath = Path.Combine(Environment.CurrentDirectory, "different-captures");
        Assert.Equal(SetupCheckResult.NotChecked, session.FolderCheck);
        Assert.False(session.RecordFolderCheck(first, true, "Stale"));
        Assert.False(session.Next(out _));
        session.RecordFolderCheck(session.FolderPath, false, "Access denied");
        Assert.False(session.Next(out _));
        session.RecordFolderCheck(session.FolderPath, true, "Writable");
        Assert.True(session.Next(out _));
    }

    [Fact]
    public void SkippingOutputAndPrivacyDiscardsOnlyThosePendingChoices()
    {
        var session = new GuidedSetupSession(Settings());
        session.Next(out _);
        session.RecordFolderCheck(session.FolderPath, true, "Writable");
        session.Next(out _);
        session.Next(out _);
        session.CopyToClipboard = false;
        session.SaveToFile = false;
        Assert.True(session.Skip());
        Assert.True(session.CopyToClipboard);
        Assert.True(session.SaveToFile);
        session.Next(out _);
        session.TurnOffAgentAccess = session.TurnOffAiDescriptions = true;
        Assert.True(session.Skip());
        Assert.False(session.TurnOffAgentAccess);
        Assert.False(session.TurnOffAiDescriptions);
    }

    [Fact]
    public void SkippingAChangedFolderRestoresTheCurrentPathAndInvalidatesItsProbe()
    {
        var source = Settings();
        var session = new GuidedSetupSession(source);
        session.Next(out _);
        session.FolderPath = Path.Combine(Environment.CurrentDirectory, "selected-folder");
        session.RecordFolderCheck(session.FolderPath, true, "Writable");
        Assert.True(session.Skip());
        Assert.Equal(source.General.SaveFolder, session.FolderPath);
        Assert.Equal(SetupCheckResult.NotChecked, session.FolderCheck);
        Assert.False(session.HasChanges);
    }

    [Fact]
    public void RelativeFolderCannotBeMarkedWritableEvenByAPassedProbeResult()
    {
        var session = new GuidedSetupSession(Settings()) { FolderPath = "relative" };
        Assert.True(session.RecordFolderCheck(session.FolderPath, true, "Writable"));
        Assert.Equal(SetupCheckResult.Failed, session.FolderCheck);
        Assert.Contains("full folder path", session.FolderCheckMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NextAndFinishPreserveAllExistingPrivacyChoices(bool enabled)
    {
        var source = Settings();
        source.General.DescribeScreenshots = enabled;
        source.General.RecognizeScreenshotText = enabled;
        source.General.ReviewBeforeAiUpload = enabled;
        source.General.AiExcludedApps = ["private.exe"];
        source.Agents.Enabled = enabled;
        source.Agents.AllowHistoryAccess = enabled;
        source.Agents.SessionAccessOnly = enabled;
        source.Agents.ApproveRequests = enabled;
        source.Agents.AllowedApps = ["editor.exe"];
        source.Agents.AllowMonitorCapture = false;
        source.Agents.AllowRegionCapture = false;
        var before = SettingsStore.Serialize(source);
        var session = new GuidedSetupSession(source);
        ReachFinish(session);
        Assert.Equal(before, SettingsStore.Serialize(session.CreateCandidate(source)));
        Assert.True(session.Complete(out _));
        Assert.Equal(SetupOutcome.Completed, session.Outcome);
        Assert.Equal(before, SettingsStore.Serialize(source));
        Assert.False(session.Next(out _));
        Assert.False(session.Back());
        Assert.False(session.Skip());
    }

    [Fact]
    public void CandidatePreservesLiveSettingsAndOnlyAppliesExplicitDeltasOrDisables()
    {
        var initial = Settings();
        initial.General.DescribeScreenshots = initial.Agents.Enabled = initial.Agents.AllowHistoryAccess = true;
        var session = new GuidedSetupSession(initial) { CopyToClipboard = false, TurnOffAiDescriptions = true, TurnOffAgentHistoryAccess = true };
        var latest = initial.Clone();
        latest.General.Theme = AppTheme.Dark;
        latest.General.SaveFolder = Path.Combine(Environment.CurrentDirectory, "live-folder");
        latest.General.ReviewBeforeAiUpload = true;
        latest.Agents.SessionAccessOnly = true;
        latest.General.Counter = 42;
        var candidate = session.CreateCandidate(latest);
        Assert.False(candidate.General.CopyToClipboard);
        Assert.False(candidate.General.DescribeScreenshots);
        Assert.False(candidate.Agents.AllowHistoryAccess);
        Assert.True(candidate.Agents.Enabled);
        Assert.True(candidate.Agents.SessionAccessOnly);
        Assert.True(candidate.General.ReviewBeforeAiUpload);
        Assert.Equal(AppTheme.Dark, candidate.General.Theme);
        Assert.Equal(latest.General.SaveFolder, candidate.General.SaveFolder);
        Assert.Equal(42, candidate.General.Counter);
        Assert.True(latest.General.DescribeScreenshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetupPreservesTheLiveDeviceCompletionFlagAndNeverSetsIt(bool completed)
    {
        var initial = Settings();
        initial.General.FirstRunCompleted = !completed;
        var session = new GuidedSetupSession(initial);
        var current = initial.Clone();
        current.General.FirstRunCompleted = completed;
        ReachFinish(session);
        Assert.Equal(completed, session.CreateCandidate(current).General.FirstRunCompleted);
        Assert.True(session.Complete(out _));
        Assert.Equal(completed, current.General.FirstRunCompleted);
        Assert.Equal(!completed, initial.General.FirstRunCompleted);
    }

    [Fact]
    public void ExplicitPrivacyReductionsCannotGrantOtherCaptureOrHistoryPermissions()
    {
        var source = Settings();
        source.General.DescribeScreenshots = source.General.RecognizeScreenshotText = true;
        source.Agents.Enabled = source.Agents.AllowHistoryAccess = true;
        source.General.ReviewBeforeAiUpload = source.Agents.ApproveRequests = source.Agents.SessionAccessOnly = true;
        source.Agents.AllowMonitorCapture = source.Agents.AllowRegionCapture = false;
        var session = new GuidedSetupSession(source)
        {
            TurnOffAiDescriptions = true,
            TurnOffTextRecognition = true,
            TurnOffAgentAccess = true,
            TurnOffAgentHistoryAccess = true,
        };
        var candidate = session.CreateCandidate(source);
        Assert.False(candidate.General.DescribeScreenshots);
        Assert.False(candidate.General.RecognizeScreenshotText);
        Assert.False(candidate.Agents.Enabled);
        Assert.False(candidate.Agents.AllowHistoryAccess);
        Assert.False(candidate.Agents.AllowMonitorCapture);
        Assert.False(candidate.Agents.AllowRegionCapture);
        Assert.True(candidate.General.ReviewBeforeAiUpload);
        Assert.True(candidate.Agents.ApproveRequests);
        Assert.True(candidate.Agents.SessionAccessOnly);
        Assert.True(session.HasChanges);
    }

    [Fact]
    public void EnablingFileOutputAfterSkippingFolderRequiresARealFolderCheck()
    {
        var source = Settings();
        source.General.SaveToFile = false;
        var session = new GuidedSetupSession(source);
        session.Next(out _);
        session.Skip();
        session.Next(out _);
        session.SaveToFile = true;
        while (session.CurrentStep != SetupStep.Finish) Assert.True(session.Next(out _));
        Assert.NotNull(session.ValidateCompletion());
        Assert.False(session.Complete(out _));
        session.RecordFolderCheck(session.FolderPath, true, "Writable");
        Assert.True(session.Complete(out _));
    }

    [Fact]
    public void SkippedChecksAreVisibleAndDoNotInventReadiness()
    {
        var session = new GuidedSetupSession(Settings());
        session.Next(out _);
        while (session.CurrentStep != SetupStep.Finish) Assert.True(session.Skip());
        Assert.Equal(6, session.SkippedSteps.Count);
        Assert.Equal(SetupCheckResult.NotChecked, session.FolderCheck);
        Assert.Equal(SetupCheckResult.NotChecked, session.ConnectionCheck);
        Assert.False(session.ShortcutsChecked);
        Assert.True(session.Complete(out _));
        Assert.False(session.HasChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelOrSettingsHandoffDiscardsPendingChoicesAndCannotComplete(bool openSettings)
    {
        var source = Settings();
        var original = SettingsStore.Serialize(source);
        var session = new GuidedSetupSession(source)
        {
            FolderPath = Path.Combine(Environment.CurrentDirectory, "other-folder"),
            CopyToClipboard = false,
            TurnOffAgentAccess = true,
        };
        if (openSettings) session.OpenSettings(); else session.Cancel();
        Assert.Equal(openSettings ? SetupOutcome.OpenedSettings : SetupOutcome.Cancelled, session.Outcome);
        Assert.False(session.HasChanges);
        Assert.Equal(original, SettingsStore.Serialize(source));
        Assert.False(session.Complete(out _));
        Assert.Throws<InvalidOperationException>(() => session.CreateCandidate(source));
        Assert.False(session.RecordFolderCheck(source.General.SaveFolder, true, "Late result"));
    }

    [Fact]
    public void ConnectionAndShortcutResultsRepresentObservedReadinessWithoutChangingPermissions()
    {
        var source = Settings();
        var session = new GuidedSetupSession(source);
        session.RecordShortcuts(2, 1, true);
        session.RecordConnectionCheck(false, "Companion missing");
        Assert.True(session.ShortcutsChecked);
        Assert.Equal(2, session.RegisteredShortcuts);
        Assert.Equal(1, session.ShortcutsNeedingAttention);
        Assert.True(session.HotkeysPaused);
        Assert.Equal(SetupCheckResult.Failed, session.ConnectionCheck);
        Assert.False(session.CreateCandidate(source).Agents.Enabled);
        session.RecordConnectionCheck(true, "Generated image received");
        Assert.Equal(SetupCheckResult.Passed, session.ConnectionCheck);
        Assert.False(session.HasChanges);
    }

    private static void ReachFinish(GuidedSetupSession session)
    {
        session.Next(out _);
        session.RecordFolderCheck(session.FolderPath, true, "Writable");
        while (session.CurrentStep != SetupStep.Finish) Assert.True(session.Next(out _));
    }
}
