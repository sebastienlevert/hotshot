using System.Text.Json.Serialization;
using Hotshot.Core.Hotkeys;
using Hotshot.Core.Naming;

namespace Hotshot.Core.Settings;

public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public GeneralSettings General { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public NamingSettings Naming { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public GifSettings Gif { get; set; } = new();
    public AgentSettings Agents { get; set; } = new();
    public LibrarySettings Library { get; set; } = new();
    public ExportSettings Export { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();

    /// <summary>Clamps values to supported ranges and fills missing sections after deserialization.</summary>
    public AppSettings Normalize()
    {
        General ??= new();
        Hotkeys ??= new();
        Naming ??= new();
        Capture ??= new();
        Recording ??= new();
        Gif ??= new();
        Agents ??= new();
        Library ??= new();
        Export ??= new();
        Export.Last ??= new();
        Export.Presets ??= [];
        Export.Presets.RemoveAll(preset => preset is null || preset.Style is null || !preset.Style.IsValid() || string.IsNullOrWhiteSpace(preset.Name));
        if (!Export.Last.IsValid()) Export.Last = new();
        Layout ??= new();
        Layout.Windows ??= [];
        foreach (var key in Layout.Windows.Where(entry => entry.Value is null || entry.Value.Width is < 200 or > 16384 ||
            entry.Value.Height is < 150 or > 16384 || !double.IsFinite(entry.Value.Scale) || entry.Value.Scale is < 0.5 or > 8)
            .Select(entry => entry.Key).ToArray()) Layout.Windows.Remove(key);
        Library.Baselines ??= [];
        Library.Baselines.RemoveAll(baseline => baseline is null || string.IsNullOrWhiteSpace(baseline.Project) || string.IsNullOrWhiteSpace(baseline.CaptureId));
        Library.SavedSearches ??= [];
        General.AiExcludedApps ??= [];
        Agents.AllowedApps ??= [];

        if (string.IsNullOrWhiteSpace(General.SaveFolder))
        {
            General.SaveFolder = AppPaths.DefaultSaveFolder;
        }

        General.PreviewSeconds = Math.Clamp(General.PreviewSeconds, 2, 60);
        General.ShowPreview = false;
        General.OpenEditorAfterCapture = false;
        if (!Enum.IsDefined(General.Theme)) General.Theme = AppTheme.System;
        General.HistorySize = Math.Clamp(General.HistorySize, 5, 500);
        if (string.IsNullOrWhiteSpace(Naming.ScreenshotPattern)) Naming.ScreenshotPattern = FileNameTemplate.DefaultPattern;
        if (string.IsNullOrWhiteSpace(Naming.RecordingPattern)) Naming.RecordingPattern = FileNameTemplate.DefaultPattern;
        Recording.FramesPerSecond = Recording.FramesPerSecond is 15 or 24 or 30 or 60 ? Recording.FramesPerSecond : 30;
        Recording.CountdownSeconds = Math.Clamp(Recording.CountdownSeconds, 0, 10);
        Capture.DelaySeconds = Math.Clamp(Capture.DelaySeconds, 0, 30);
        Agents.SessionMinutes = Math.Clamp(Agents.SessionMinutes, 5, 240);
        if (!double.IsFinite(Layout.HistoryWidth)) Layout.HistoryWidth = 220;
        Layout.HistoryWidth = Math.Clamp(Layout.HistoryWidth, 130, 420);
        Gif.FramesPerSecond = Math.Clamp(Gif.FramesPerSecond, 5, 30);
        Gif.MaxWidth = Gif.MaxWidth == 0 ? 0 : Math.Clamp(Gif.MaxWidth, 160, 3840);
        Version = CurrentVersion;
        return this;
    }

    public AppSettings Clone() => SettingsStore.Deserialize(SettingsStore.Serialize(this));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(General.CopilotRuntimePath) && !Path.IsPathFullyQualified(General.CopilotRuntimePath))
            errors.Add("Choose an absolute path for an optional SDK-compatible Copilot runtime.");
        if (Export.Presets.Count > 50 || Export.Presets.Any(preset => string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80))
            errors.Add("Use up to 50 named export presets with names up to 80 characters.");
        if (!Export.Last.IsValid() || Export.Presets.Any(preset => preset.Style is null || !preset.Style.IsValid()))
            errors.Add("Export preset dimensions or style are outside supported limits.");
        if (General.AiExcludedApps.Length > 50 || General.AiExcludedApps.Any(name =>
            name is null || name.Length > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            errors.Add("AI exclusions must be up to 50 process names, without paths or invalid filename characters.");
        if (Agents.AllowedApps.Length > 50 || Agents.AllowedApps.Any(name =>
            name is null || name.Length > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            errors.Add("Agent target restrictions must be up to 50 process names, without paths.");
        if (Library.SavedSearches.Count > 100 || Library.SavedSearches.Any(search =>
            search is null || search.Name is null || search.Query is null || search.Name.Length > 80 || search.Query.Length > 500))
            errors.Add("Use up to 100 saved searches with names up to 80 and queries up to 500 characters.");
        if (Library.Baselines.Count > 1000 || Library.Baselines.Any(baseline =>
            baseline is null || string.IsNullOrWhiteSpace(baseline.Project) || baseline.Project.Length > 80 || string.IsNullOrWhiteSpace(baseline.CaptureId)))
            errors.Add("Project baselines must have valid project names and capture IDs (up to 1000).");
        try
        {
            if (string.IsNullOrWhiteSpace(General.SaveFolder) || !Path.IsPathFullyQualified(General.SaveFolder))
            {
                errors.Add("Choose an absolute path for the captures folder.");
            }
            else
            {
                _ = Path.GetFullPath(General.SaveFolder);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"The captures folder is invalid: {ex.Message}");
        }

        foreach (var (name, pattern) in new[]
                 {
                     ("Screenshot", Naming.ScreenshotPattern),
                     ("Recording", Naming.RecordingPattern),
                 })
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                errors.Add($"{name} naming pattern cannot be empty.");
                continue;
            }

            var invalid = FileNameTemplate.FindInvalidTokens(pattern);
            if (invalid.Count > 0)
            {
                errors.Add($"{name} naming pattern has invalid tokens: {string.Join(", ", invalid)}.");
            }
        }

        var seen = new Dictionary<Hotkey, HotkeyAction>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            if (!Hotkey.TryParse(Hotkeys.Get(action), out var key))
            {
                errors.Add($"{action.DisplayName()}: invalid shortcut.");
            }
            else if (!key.IsEmpty)
            {
                if (seen.TryGetValue(key, out var other))
                {
                    errors.Add($"{action.DisplayName()} uses the same shortcut as {other.DisplayName()}.");
                }
                else
                {
                    seen[key] = action;
                }
            }
        }

        return errors;
    }
}

public sealed class GeneralSettings
{
    public AppTheme Theme { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public string SaveFolder { get; set; } = AppPaths.DefaultSaveFolder;
    public bool CopyToClipboard { get; set; } = true;
    public bool SaveToFile { get; set; } = true;
    public bool ShowPreview { get; set; }
    public int PreviewSeconds { get; set; } = 6;
    public bool OpenEditorAfterCapture { get; set; }
    public int HistorySize { get; set; } = 60;
    public long Counter { get; set; }
    public bool FirstRunCompleted { get; set; }
    public bool DescribeScreenshots { get; set; }
    public bool RecognizeScreenshotText { get; set; }
    public bool ReviewBeforeAiUpload { get; set; }
    public string[] AiExcludedApps { get; set; } = [];
    public string? CopilotRuntimePath { get; set; }
}

public sealed class LibrarySettings
{
    public bool KeepSavedCaptures { get; set; } = true;
    public List<SavedCaptureSearch> SavedSearches { get; set; } = [];
    public List<ProjectBaseline> Baselines { get; set; } = [];
}

public sealed record SavedCaptureSearch(string Name, string Query);
public sealed record ProjectBaseline(string Project, string CaptureId);

public sealed class ExportSettings
{
    public ExportStyle Last { get; set; } = new();
    public List<ExportPreset> Presets { get; set; } = [];
}

public sealed record ExportStyle(int Format = 0, int Width = 0, int Padding = 24, int CornerRadius = 12,
    bool Shadow = true, int Background = 2)
{
    public bool IsValid() => Format is >= 0 and <= 1 && Background is >= 0 and <= 3 && Width is >= 0 and <= 16384 &&
        Padding is >= 0 and <= 400 && CornerRadius is >= 0 and <= 400;
}
public sealed record ExportPreset(string Name, ExportStyle Style);
public sealed record WindowLayout(int X, int Y, int Width, int Height, double Scale);
public sealed class LayoutSettings
{
    public double HistoryWidth { get; set; } = 220;
    public Dictionary<string, WindowLayout> Windows { get; set; } = new();
}

public sealed class AgentSettings
{
    public bool Enabled { get; set; }
    public bool AllowHistoryAccess { get; set; }
    public string[] AllowedApps { get; set; } = [];
    public bool AllowMonitorCapture { get; set; } = true;
    public bool AllowRegionCapture { get; set; } = true;
    public bool SessionAccessOnly { get; set; }
    public bool ApproveRequests { get; set; }
    public int SessionMinutes { get; set; } = 60;
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class HotkeySettings
{
    public string RegionScreenshot { get; set; } = "PrintScreen";
    public string MonitorScreenshot { get; set; } = "Ctrl+PrintScreen";
    public string AllMonitorsScreenshot { get; set; } = "Shift+PrintScreen";
    public string ToggleRecording { get; set; } = "Ctrl+Shift+PrintScreen";
    public string RecordGif { get; set; } = string.Empty;
    public string RepeatLastRegion { get; set; } = string.Empty;
    public string OpenHistory { get; set; } = string.Empty;
    public string OpenLastInEditor { get; set; } = string.Empty;

    public string Get(HotkeyAction action) => action switch
    {
        HotkeyAction.RegionScreenshot => RegionScreenshot,
        HotkeyAction.MonitorScreenshot => MonitorScreenshot,
        HotkeyAction.AllMonitorsScreenshot => AllMonitorsScreenshot,
        HotkeyAction.ToggleRecording => ToggleRecording,
        HotkeyAction.RecordGif => RecordGif,
        HotkeyAction.RepeatLastRegion => RepeatLastRegion,
        HotkeyAction.OpenHistory => OpenHistory,
        HotkeyAction.OpenLastInEditor => OpenLastInEditor,
        _ => string.Empty,
    };

    public void Set(HotkeyAction action, string value)
    {
        switch (action)
        {
            case HotkeyAction.RegionScreenshot: RegionScreenshot = value; break;
            case HotkeyAction.MonitorScreenshot: MonitorScreenshot = value; break;
            case HotkeyAction.AllMonitorsScreenshot: AllMonitorsScreenshot = value; break;
            case HotkeyAction.ToggleRecording: ToggleRecording = value; break;
            case HotkeyAction.RecordGif: RecordGif = value; break;
            case HotkeyAction.RepeatLastRegion: RepeatLastRegion = value; break;
            case HotkeyAction.OpenHistory: OpenHistory = value; break;
            case HotkeyAction.OpenLastInEditor: OpenLastInEditor = value; break;
        }
    }

    /// <summary>Parsed bindings; invalid or empty entries are skipped.</summary>
    public IEnumerable<(HotkeyAction Action, Hotkey Hotkey)> GetBindings()
    {
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            if (Hotkey.TryParse(Get(action), out var hotkey) && !hotkey.IsEmpty)
            {
                yield return (action, hotkey);
            }
        }
    }
}

public sealed class NamingSettings
{
    public string ScreenshotPattern { get; set; } = FileNameTemplate.DefaultPattern;
    public string RecordingPattern { get; set; } = FileNameTemplate.DefaultPattern;
}

public sealed class CaptureSettings
{
    public int DelaySeconds { get; set; }
    public bool IncludeCursor { get; set; }
    public bool ShowMagnifier { get; set; } = true;
    public bool SnapToWindows { get; set; } = true;
    public bool ShowCrosshair { get; set; } = true;
    public bool UnobscuredWindows { get; set; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter<RecordingQuality>))]
public enum RecordingQuality
{
    Standard,
    High,
    Maximum,
}

public sealed class RecordingSettings
{
    public int FramesPerSecond { get; set; } = 30;
    public RecordingQuality Quality { get; set; } = RecordingQuality.High;
    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public bool IncludeCursor { get; set; } = true;
    public int CountdownSeconds { get; set; } = 3;
    public bool ShowStartBar { get; set; } = true;
    public bool AlsoCreateGif { get; set; }
}

public sealed class GifSettings
{
    public int FramesPerSecond { get; set; } = 15;
    public int MaxWidth { get; set; } = 800;
    public bool Dither { get; set; } = true;
    public bool Loop { get; set; } = true;
}
