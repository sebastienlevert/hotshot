using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hotshot.Core.Settings;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Loads and atomically saves <see cref="AppSettings"/>.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly string? _defaultSaveFolder;
    private readonly Lock _gate = new();

    public SettingsStore(string path, string? defaultSaveFolder = null)
    {
        if (defaultSaveFolder is not null && !Path.IsPathFullyQualified(defaultSaveFolder))
            throw new ArgumentException("The default captures folder must be an absolute path.", nameof(defaultSaveFolder));
        _path = path;
        _defaultSaveFolder = defaultSaveFolder;
    }

    public event EventHandler<AppSettings>? Saved;

    public string FilePath => _path;

    public static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

    public static AppSettings Deserialize(string json) =>
        (JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings()).Normalize();

    /// <summary>Loads settings; a corrupt file is backed up and defaults are returned.</summary>
    /// <exception cref="IOException">Existing settings could not be read or preserved.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to existing settings or their backup was denied.</exception>
    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                return Deserialize(File.ReadAllText(_path));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return CreateDefaults();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                File.Copy(_path, _path + ".corrupt", overwrite: true);
                return CreateDefaults();
            }
        }
    }

    private AppSettings CreateDefaults()
    {
        var settings = new AppSettings();
        if (_defaultSaveFolder is not null) settings.General.SaveFolder = _defaultSaveFolder;
        return settings.Normalize();
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, Serialize(settings.Normalize()));
                File.Move(temp, _path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        Saved?.Invoke(this, settings);
    }
}
