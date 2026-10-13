using Hotshot.Core.Settings;

namespace Hotshot.Core.Descriptions;

public static class CaptureAnalysisPolicy
{
    // Older releases stored workflow labels in AppName; those are not source-app provenance.
    private static readonly string[] LegacySourceLabels = ["Video frame", "Imported image", "Scrolling capture"];

    public static bool HasKnownSource(string? app) =>
        !string.IsNullOrWhiteSpace(app) && !LegacySourceLabels.Contains(app.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsExcluded(GeneralSettings settings, string? app) =>
        !string.IsNullOrWhiteSpace(app) && (settings.AiExcludedApps ?? [])
            .Any(name => string.Equals(Path.GetFileNameWithoutExtension(name.Trim()), app, StringComparison.OrdinalIgnoreCase));

    public static bool CanAnalyzeAutomatically(GeneralSettings settings, string? app) =>
        settings.DescribeScreenshots && !settings.ReviewBeforeAiUpload && HasKnownSource(app) && !IsExcluded(settings, app);

    public static bool RequiresReview(GeneralSettings settings, string? app) =>
        settings.DescribeScreenshots && (settings.ReviewBeforeAiUpload || !HasKnownSource(app)) && !IsExcluded(settings, app);
}
