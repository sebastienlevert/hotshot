using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hotshot.Diagnostics;

internal static class AppDiagnostics
{
    public static string Read(AppServices app)
    {
        using var process = Process.GetCurrentProcess();
        var items = app.History.Items;
        var bytes = items.Sum(item => item.FileSize);
        var package = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        return $"Version: {typeof(AppDiagnostics).Assembly.GetName().Version}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\n" +
            $"Edition: {app.CopilotRuntime?.Edition.ToString() ?? "Unmanaged/test"}\nUpdate channel: {app.Updates?.Channel ?? "None"}\n" +
            $"Copilot component: {app.CopilotRuntime?.Status ?? "Not initialized"}\n" +
            $"Windows: {Environment.OSVersion.Version}\nUptime: {DateTime.Now - process.StartTime:g}\n" +
            $"Resident startup: {Program.StartupMilliseconds} ms (includes requested initial window)\n" +
            $"Working set: {process.WorkingSet64 / 1048576d:F1} MB\nPrivate memory: {process.PrivateMemorySize64 / 1048576d:F1} MB\n" +
            $"Installed files: {package / 1048576d:F1} MB\nLibrary: {items.Count} entries / {bytes / 1048576d:F1} MB indexed\n" +
            $"Missing saved files: {items.Count(item => !File.Exists(item.Path))}\nHistory error: {app.History.LastError ?? "None"}\n" +
            $"AI queue: {app.Descriptions?.PendingCount ?? 0}; {app.Descriptions?.ProviderStatus}\n" +
            $"Agent service: {app.AgentServer?.Status}\nUpdates: {app.Updates?.Status}\n" +
            $"Settings roaming: {app.SettingsSyncStatus}\nShared settings: {app.SharedSettingsFile ?? "None"}\n" +
            $"Interrupted recording journal: {(File.Exists(Path.Combine(app.Paths.DataDirectory, "pending-recording.json")) ? "Present - use Recover interrupted recording in the workspace" : "None")}\n" +
            "Packages are unsigned. No signing is configured. Diagnostics stay local.";
    }
}
