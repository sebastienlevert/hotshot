using Microsoft.Win32;

namespace Hotshot.Settings;

internal sealed record OneDriveFolder(string Label, string Path, bool WorkOrSchool = false);

internal static class OneDriveFolders
{
    public static IReadOnlyList<OneDriveFolder> GetAvailable()
    {
        var folders = new List<OneDriveFolder>();
        void Add(string label, string? path, bool workOrSchool = false)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return;
            var fullPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            var existing = folders.FindIndex(folder => folder.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                if (workOrSchool) folders[existing] = new OneDriveFolder(label, fullPath, true);
                return;
            }
            folders.Add(new OneDriveFolder(label, fullPath, workOrSchool));
        }
        Add("OneDrive personal", Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        Add("OneDrive work or school", Environment.GetEnvironmentVariable("OneDriveCommercial"), workOrSchool: true);
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts is not null)
                foreach (var name in accounts.GetSubKeyNames().OrderBy(name => name == "Personal" ? 0 : 1))
                {
                    using var account = accounts.OpenSubKey(name);
                    Add(name == "Personal" ? "OneDrive personal" : "OneDrive work or school",
                        account?.GetValue("UserFolder") as string, workOrSchool: name != "Personal");
                }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { Log.Warn("OneDrive folder discovery failed: " + ex.Message); }
        return folders;
    }

    public static string? GetDefault()
    {
        var folders = GetAvailable();
        return folders.Count > 0 ? folders[0].Path : null;
    }

    public static string GetDefaultCaptureFolder() =>
        GetDefaultCaptureFolder(GetAvailable(), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

    internal static string GetDefaultCaptureFolder(IReadOnlyList<OneDriveFolder> folders, string picturesFolder)
    {
        var oneDrive = folders.FirstOrDefault(folder => folder.WorkOrSchool) ?? (folders.Count > 0 ? folders[0] : null);
        if (oneDrive is null) return System.IO.Path.Combine(picturesFolder, "Hotshot");
        var prefix = System.IO.Path.TrimEndingDirectorySeparator(oneDrive.Path) + System.IO.Path.DirectorySeparatorChar;
        var pictures = System.IO.Path.GetFullPath(picturesFolder).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? picturesFolder : System.IO.Path.Combine(oneDrive.Path, "Pictures");
        return System.IO.Path.Combine(pictures, "Hotshot");
    }
}
