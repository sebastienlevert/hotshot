using Hotshot.Core.Settings;

namespace Hotshot.Core.Descriptions;

public static class CaptureAnalysisPolicy
{
    public static bool IsExcluded(GeneralSettings settings, string? app) =>
        !string.IsNullOrWhiteSpace(app) && (settings.AiExcludedApps ?? [])
            .Any(name => string.Equals(Path.GetFileNameWithoutExtension(name.Trim()), app, StringComparison.OrdinalIgnoreCase));

    public static bool CanAnalyzeAutomatically(GeneralSettings settings, string? app) =>
        settings.DescribeScreenshots && !settings.ReviewBeforeAiUpload && !IsExcluded(settings, app);
}
