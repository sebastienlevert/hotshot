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
    private readonly Lock _gate = new();

    public SettingsStore(string path)
    {
        _path = path;
    }

    public event EventHandler<AppSettings>? Saved;

    public string FilePath => _path;

    public static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

    public static AppSettings Deserialize(string json) =>
        (JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings()).Normalize();

    /// <summary>Loads settings; a corrupt file is backed up and defaults are returned.</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new AppSettings().Normalize();
            }

            try
            {
                return Deserialize(File.ReadAllText(_path));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                try
                {
                    File.Copy(_path, _path + ".corrupt", overwrite: true);
                }
                catch (IOException)
                {
                }

                return new AppSettings().Normalize();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, Serialize(settings.Normalize()));
            File.Move(temp, _path, overwrite: true);
        }

        Saved?.Invoke(this, settings);
    }
}
