using Microsoft.Win32;

namespace Hotshot.Settings;

internal sealed record OneDriveFolder(string Label, string Path);

internal static class OneDriveFolders
{
    public static IReadOnlyList<OneDriveFolder> GetAvailable()
    {
        var folders = new List<OneDriveFolder>();
        void Add(string label, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || !Directory.Exists(path) ||
                folders.Any(folder => folder.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
            folders.Add(new OneDriveFolder(label, System.IO.Path.GetFullPath(path)));
        }
        Add("OneDrive personal", Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        Add("OneDrive work or school", Environment.GetEnvironmentVariable("OneDriveCommercial"));
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts is not null)
                foreach (var name in accounts.GetSubKeyNames().OrderBy(name => name == "Personal" ? 0 : 1))
                {
                    using var account = accounts.OpenSubKey(name);
                    Add(name == "Personal" ? "OneDrive personal" : "OneDrive work or school", account?.GetValue("UserFolder") as string);
                }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { Log.Warn("OneDrive folder discovery failed: " + ex.Message); }
        return folders;
    }

    public static string? GetDefault() => GetAvailable().FirstOrDefault()?.Path;
}
