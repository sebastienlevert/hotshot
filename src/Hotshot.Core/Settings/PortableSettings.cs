using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Hotshot.Core.Settings;

public sealed class PortableGeneralSettings
{
    public AppTheme Theme { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    public bool SaveToFile { get; set; } = true;
    public int HistorySize { get; set; } = 60;
    public bool RecognizeScreenshotText { get; set; }
    public string[] AiExcludedApps { get; set; } = [];
}

public sealed class PortableRecordingSettings
{
    public int FramesPerSecond { get; set; } = 30;
    public RecordingQuality Quality { get; set; } = RecordingQuality.High;
    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; }
    public bool IncludeCursor { get; set; } = true;
    public int CountdownSeconds { get; set; } = 3;
    public bool ShowStartBar { get; set; } = true;
    public bool AlsoCreateGif { get; set; }
}

public sealed class PortableLibrarySettings
{
    public bool KeepSavedCaptures { get; set; } = true;
    public List<SavedCaptureSearch> SavedSearches { get; set; } = [];
}

public sealed class PortableSettings
{
    public PortableGeneralSettings General { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public NamingSettings Naming { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public PortableRecordingSettings Recording { get; set; } = new();
    public GifSettings Gif { get; set; } = new();
    public PortableLibrarySettings Library { get; set; } = new();
    public ExportSettings Export { get; set; } = new();

    public static PortableSettings From(AppSettings settings)
    {
        var clone = settings.Clone();
        return new PortableSettings
        {
            General = new()
            {
                Theme = clone.General.Theme, StartWithWindows = clone.General.StartWithWindows,
                CopyToClipboard = clone.General.CopyToClipboard, SaveToFile = clone.General.SaveToFile,
                HistorySize = clone.General.HistorySize, RecognizeScreenshotText = clone.General.RecognizeScreenshotText,
                AiExcludedApps = clone.General.AiExcludedApps,
            },
            Hotkeys = clone.Hotkeys, Naming = clone.Naming, Capture = clone.Capture, Gif = clone.Gif, Export = clone.Export,
            Recording = new()
            {
                FramesPerSecond = clone.Recording.FramesPerSecond, Quality = clone.Recording.Quality,
                CaptureSystemAudio = clone.Recording.CaptureSystemAudio, CaptureMicrophone = clone.Recording.CaptureMicrophone,
                IncludeCursor = clone.Recording.IncludeCursor, CountdownSeconds = clone.Recording.CountdownSeconds,
                ShowStartBar = clone.Recording.ShowStartBar, AlsoCreateGif = clone.Recording.AlsoCreateGif,
            },
            Library = new() { KeepSavedCaptures = clone.Library.KeepSavedCaptures, SavedSearches = clone.Library.SavedSearches },
        };
    }

    public AppSettings ApplyTo(AppSettings device)
    {
        if (General is null || Hotkeys is null || Naming is null || Capture is null || Recording is null || Gif is null ||
            Library is null || Export is null) throw new InvalidDataException("Shared settings contain a missing section.");
        var result = device.Clone();
        result.General.Theme = General.Theme;
        result.General.StartWithWindows = General.StartWithWindows;
        result.General.CopyToClipboard = General.CopyToClipboard;
        result.General.SaveToFile = General.SaveToFile;
        result.General.HistorySize = General.HistorySize;
        result.General.RecognizeScreenshotText = General.RecognizeScreenshotText;
        result.General.AiExcludedApps = General.AiExcludedApps ?? [];
        result.Hotkeys = Hotkeys; result.Naming = Naming; result.Capture = Capture; result.Gif = Gif; result.Export = Export;
        result.Recording.FramesPerSecond = Recording.FramesPerSecond;
        result.Recording.Quality = Recording.Quality;
        result.Recording.CaptureSystemAudio = Recording.CaptureSystemAudio;
        result.Recording.CaptureMicrophone = Recording.CaptureMicrophone;
        result.Recording.IncludeCursor = Recording.IncludeCursor;
        result.Recording.CountdownSeconds = Recording.CountdownSeconds;
        result.Recording.ShowStartBar = Recording.ShowStartBar;
        result.Recording.AlsoCreateGif = Recording.AlsoCreateGif;
        result.Library.KeepSavedCaptures = Library.KeepSavedCaptures;
        result.Library.SavedSearches = Library.SavedSearches ?? [];
        result.Normalize();
        var errors = result.Validate();
        if (errors.Count > 0) throw new InvalidDataException("Shared settings are invalid: " + string.Join(" ", errors));
        return result;
    }
}

public sealed class SharedSettingsDocument
{
    public int SchemaVersion { get; set; } = 1;
    public PortableSettings Preferences { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SharedSettingsDocument))]
[JsonSerializable(typeof(PortableSettings))]
[JsonSerializable(typeof(SettingsSyncConfiguration))]
public sealed partial class SharedSettingsJson : JsonSerializerContext;

public static class PortableSettingsMerge
{
    public static PortableSettings Merge(PortableSettings baseline, PortableSettings local, PortableSettings remote)
    {
        var old = JsonSerializer.SerializeToNode(baseline, SharedSettingsJson.Default.PortableSettings)!;
        var mine = JsonSerializer.SerializeToNode(local, SharedSettingsJson.Default.PortableSettings)!;
        var theirs = JsonSerializer.SerializeToNode(remote, SharedSettingsJson.Default.PortableSettings)!;
        var conflicts = new List<string>();
        var result = MergeNode(old, mine, theirs, "", conflicts);
        if (conflicts.Count > 0) throw new SettingsSyncConflictException(conflicts);
        return JsonSerializer.Deserialize(result, SharedSettingsJson.Default.PortableSettings)
            ?? throw new InvalidDataException("Merged shared settings are empty.");
    }

    private static JsonNode? MergeNode(JsonNode? old, JsonNode? mine, JsonNode? theirs, string path, List<string> conflicts)
    {
        if (JsonNode.DeepEquals(mine, old)) return theirs?.DeepClone();
        if (JsonNode.DeepEquals(theirs, old) || JsonNode.DeepEquals(mine, theirs)) return mine?.DeepClone();
        if (old is JsonObject oldObject && mine is JsonObject mineObject && theirs is JsonObject theirObject)
        {
            var result = new JsonObject();
            foreach (var key in oldObject.Select(entry => entry.Key).Union(mineObject.Select(entry => entry.Key)).Union(theirObject.Select(entry => entry.Key)))
                result[key] = MergeNode(oldObject[key], mineObject[key], theirObject[key], path.Length == 0 ? key : path + "." + key, conflicts);
            return result;
        }
        conflicts.Add(path);
        return mine?.DeepClone();
    }
}

public sealed class SettingsSyncConflictException(IReadOnlyList<string> fields)
    : IOException("OneDrive preferences changed on both machines: " + string.Join(", ", fields) + ". Resolve the conflict in Settings > General.")
{
    public IReadOnlyList<string> Fields { get; } = fields;
}
