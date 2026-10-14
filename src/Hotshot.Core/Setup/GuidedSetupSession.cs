using Hotshot.Core.Settings;

namespace Hotshot.Core.Setup;

public enum SetupStep
{
    Welcome,
    Folder,
    Shortcuts,
    Output,
    OneDrive,
    Privacy,
    Connection,
    Finish,
}

public enum SetupOutcome
{
    NotStarted,
    InProgress,
    Completed,
    Cancelled,
    OpenedSettings,
}

public enum SetupStepProgress { Pending, Reviewed, Skipped }
public enum SetupCheckResult { NotChecked, Passed, Failed }

/// <summary>A device-local, staged setup session. Navigation never writes settings or grants access.</summary>
public sealed class GuidedSetupSession
{
    private readonly AppSettings _initial;
    private readonly SetupStepProgress[] _progress = new SetupStepProgress[StepCount];
    private string _folderPath;

    public GuidedSetupSession(AppSettings settings)
    {
        _initial = settings.Clone();
        _folderPath = _initial.General.SaveFolder;
        CopyToClipboard = _initial.General.CopyToClipboard;
        SaveToFile = _initial.General.SaveToFile;
    }

    public const int StepCount = 8;
    public SetupStep CurrentStep { get; private set; }
    public SetupOutcome Outcome { get; private set; } = SetupOutcome.InProgress;
    public int StepNumber => (int)CurrentStep + 1;
    public bool CanGoBack => Outcome == SetupOutcome.InProgress && CurrentStep != SetupStep.Welcome;
    public bool CanSkip => Outcome == SetupOutcome.InProgress && CurrentStep is not (SetupStep.Welcome or SetupStep.Finish);
    public IReadOnlyList<SetupStep> SkippedSteps => Enum.GetValues<SetupStep>()
        .Where(step => GetProgress(step) == SetupStepProgress.Skipped).ToArray();

    public string FolderPath
    {
        get => _folderPath;
        set
        {
            EnsureActive();
            if (_folderPath == value) return;
            _folderPath = value;
            FolderCheck = SetupCheckResult.NotChecked;
            FolderCheckMessage = "This folder has not been checked.";
        }
    }

    public bool CopyToClipboard { get; set; }
    public bool SaveToFile { get; set; }
    public bool TurnOffAiDescriptions { get; set; }
    public bool TurnOffTextRecognition { get; set; }
    public bool TurnOffAgentAccess { get; set; }
    public bool TurnOffAgentHistoryAccess { get; set; }
    public bool InitialAiDescriptions => _initial.General.DescribeScreenshots;
    public bool InitialTextRecognition => _initial.General.RecognizeScreenshotText;
    public bool InitialAgentAccess => _initial.Agents.Enabled;
    public bool InitialAgentHistoryAccess => _initial.Agents.AllowHistoryAccess;
    public bool InitialAiReview => _initial.General.ReviewBeforeAiUpload;
    public SetupCheckResult FolderCheck { get; private set; }
    public string FolderCheckMessage { get; private set; } = "This folder has not been checked.";
    public SetupCheckResult ConnectionCheck { get; private set; }
    public string ConnectionCheckMessage { get; private set; } = "Optional test has not run.";
    public int RegisteredShortcuts { get; private set; }
    public int ShortcutsNeedingAttention { get; private set; }
    public bool ShortcutsChecked { get; private set; }
    public bool HotkeysPaused { get; private set; }

    public bool HasChanges => !SameFolder(FolderPath, _initial.General.SaveFolder) ||
        CopyToClipboard != _initial.General.CopyToClipboard || SaveToFile != _initial.General.SaveToFile ||
        TurnOffAiDescriptions && InitialAiDescriptions || TurnOffTextRecognition && InitialTextRecognition ||
        TurnOffAgentAccess && InitialAgentAccess || TurnOffAgentHistoryAccess && InitialAgentHistoryAccess;

    public SetupStepProgress GetProgress(SetupStep step) => _progress[(int)step];

    public static string? ValidateFolderPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                return @"Choose a full folder path, such as C:\Pictures.";
            _ = Path.GetFullPath(path);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The folder path is invalid: " + ex.Message;
        }
    }

    public bool RecordFolderCheck(string checkedPath, bool passed, string message)
    {
        if (Outcome != SetupOutcome.InProgress || !SameFolder(checkedPath, FolderPath)) return false;
        var invalid = ValidateFolderPath(checkedPath);
        FolderCheck = passed && invalid is null ? SetupCheckResult.Passed : SetupCheckResult.Failed;
        FolderCheckMessage = invalid ?? message;
        return true;
    }

    public void RecordShortcuts(int registered, int needingAttention, bool paused)
    {
        EnsureActive();
        ArgumentOutOfRangeException.ThrowIfNegative(registered);
        ArgumentOutOfRangeException.ThrowIfNegative(needingAttention);
        RegisteredShortcuts = registered;
        ShortcutsNeedingAttention = needingAttention;
        HotkeysPaused = paused;
        ShortcutsChecked = true;
    }

    public void RecordConnectionCheck(bool passed, string message)
    {
        EnsureActive();
        ConnectionCheck = passed ? SetupCheckResult.Passed : SetupCheckResult.Failed;
        ConnectionCheckMessage = message;
    }

    public bool Next(out string? error)
    {
        error = null;
        if (Outcome != SetupOutcome.InProgress || CurrentStep == SetupStep.Finish) return false;
        if (CurrentStep == SetupStep.Folder && FolderCheck != SetupCheckResult.Passed)
        {
            error = "Check that the folder is writable, or skip this step to keep your current folder.";
            return false;
        }
        _progress[(int)CurrentStep] = SetupStepProgress.Reviewed;
        CurrentStep++;
        return true;
    }

    public bool Back()
    {
        if (!CanGoBack) return false;
        CurrentStep--;
        return true;
    }

    public bool Skip()
    {
        if (!CanSkip) return false;
        switch (CurrentStep)
        {
            case SetupStep.Folder:
                FolderPath = _initial.General.SaveFolder;
                break;
            case SetupStep.Output:
                CopyToClipboard = _initial.General.CopyToClipboard;
                SaveToFile = _initial.General.SaveToFile;
                break;
            case SetupStep.Privacy:
                ResetPrivacyChoices();
                break;
        }
        _progress[(int)CurrentStep] = SetupStepProgress.Skipped;
        CurrentStep++;
        return true;
    }

    public string? ValidateCompletion()
    {
        if (Outcome != SetupOutcome.InProgress || CurrentStep != SetupStep.Finish)
            return "Finish the setup steps before completing setup.";
        var folderChanged = !SameFolder(FolderPath, _initial.General.SaveFolder);
        if (folderChanged && ValidateFolderPath(FolderPath) is { } invalid) return invalid;
        if ((folderChanged || SaveToFile && (!_initial.General.SaveToFile ||
                GetProgress(SetupStep.Folder) != SetupStepProgress.Skipped)) && FolderCheck != SetupCheckResult.Passed)
            return "Go back to Save folder and check it before finishing.";
        return null;
    }

    public AppSettings CreateCandidate(AppSettings current)
    {
        EnsureActive();
        var candidate = current.Clone();
        // Only explicit setup deltas apply; live settings and every permission not turned off survive.
        if (!SameFolder(FolderPath, _initial.General.SaveFolder)) candidate.General.SaveFolder = FolderPath;
        if (CopyToClipboard != _initial.General.CopyToClipboard) candidate.General.CopyToClipboard = CopyToClipboard;
        if (SaveToFile != _initial.General.SaveToFile) candidate.General.SaveToFile = SaveToFile;
        if (TurnOffAiDescriptions) candidate.General.DescribeScreenshots = false;
        if (TurnOffTextRecognition) candidate.General.RecognizeScreenshotText = false;
        if (TurnOffAgentAccess) candidate.Agents.Enabled = false;
        if (TurnOffAgentHistoryAccess) candidate.Agents.AllowHistoryAccess = false;
        return candidate;
    }

    public bool Complete(out string? error)
    {
        error = ValidateCompletion();
        if (error is not null) return false;
        _progress[(int)SetupStep.Finish] = SetupStepProgress.Reviewed;
        Outcome = SetupOutcome.Completed;
        return true;
    }

    public void Cancel() => EndWithoutSaving(SetupOutcome.Cancelled);
    public void OpenSettings() => EndWithoutSaving(SetupOutcome.OpenedSettings);

    private void EndWithoutSaving(SetupOutcome outcome)
    {
        if (Outcome != SetupOutcome.InProgress) return;
        FolderPath = _initial.General.SaveFolder;
        CopyToClipboard = _initial.General.CopyToClipboard;
        SaveToFile = _initial.General.SaveToFile;
        ResetPrivacyChoices();
        Outcome = outcome;
    }

    private void ResetPrivacyChoices()
    {
        TurnOffAiDescriptions = TurnOffTextRecognition = TurnOffAgentAccess = TurnOffAgentHistoryAccess = false;
    }

    private void EnsureActive()
    {
        if (Outcome != SetupOutcome.InProgress) throw new InvalidOperationException("This setup session has ended.");
    }

    private static bool SameFolder(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
