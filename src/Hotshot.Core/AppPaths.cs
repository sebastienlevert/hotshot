namespace Hotshot.Core;

/// <summary>Well-known locations for Hotshot's user data.</summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    /// <summary>Default: %AppData%\Hotshot. %LocalAppData%\Hotshot is owned by the installer.</summary>
    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hotshot"));

    public string DataDirectory { get; }
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string HistoryFile => Path.Combine(DataDirectory, "history.json");
    public string ThumbnailsDirectory => Path.Combine(DataDirectory, "thumbnails");
    public string OriginalsDirectory => Path.Combine(DataDirectory, "originals");
    public string LogFile => Path.Combine(DataDirectory, "hotshot.log");
    public static string TempDirectory => Path.Combine(Path.GetTempPath(), "Hotshot");

    public static string DefaultSaveFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Hotshot");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ThumbnailsDirectory);
        Directory.CreateDirectory(OriginalsDirectory);
    }
}
