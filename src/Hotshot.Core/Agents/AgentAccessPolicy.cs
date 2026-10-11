using Hotshot.Core.Settings;

namespace Hotshot.Core.Agents;

public static class AgentAccessPolicy
{
    public static bool AllowsApp(AgentSettings settings, string? app) =>
        settings.AllowedApps.Length == 0 || settings.AllowedApps.Any(name =>
            string.Equals(Path.GetFileNameWithoutExtension(name.Trim()), app, StringComparison.OrdinalIgnoreCase));
    public static bool AllowsRegion(AgentSettings settings) => settings.AllowRegionCapture && settings.AllowedApps.Length == 0;
    public static bool AllowsMonitor(AgentSettings settings) => settings.AllowMonitorCapture && settings.AllowedApps.Length == 0;
}
