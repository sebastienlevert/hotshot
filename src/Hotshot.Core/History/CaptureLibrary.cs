namespace Hotshot.Core.History;

public enum CaptureDateRange { All, Today, LastWeek, LastMonth }
public enum CaptureTypeFilter { All, Screenshots, Recordings, Gifs }

public sealed record CaptureLibraryFilter(string Query = "", bool FavoritesOnly = false,
    CaptureTypeFilter Type = CaptureTypeFilter.All, CaptureDateRange Date = CaptureDateRange.All)
{
    public bool Matches(HistoryItem item, DateTimeOffset now)
    {
        if (!item.MatchesSearch(Query) || FavoritesOnly && !item.IsFavorite) return false;
        if (Type switch
        {
            CaptureTypeFilter.Screenshots => item.IsVideo,
            CaptureTypeFilter.Recordings => item.Kind != CaptureKind.Recording,
            CaptureTypeFilter.Gifs => item.Kind != CaptureKind.Gif,
            _ => false,
        }) return false;
        return Date switch
        {
            CaptureDateRange.Today => item.CreatedAt.LocalDateTime.Date == now.LocalDateTime.Date,
            CaptureDateRange.LastWeek => item.CreatedAt >= now.AddDays(-7),
            CaptureDateRange.LastMonth => item.CreatedAt >= now.AddDays(-30),
            _ => true,
        };
    }
}

public static class CaptureLibrary
{
    public static string[] NormalizeTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var normalized = tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length > 20 || normalized.Any(tag => tag.Length > 40 || tag.Any(char.IsControl)))
            throw new ArgumentException("Use up to 20 tags, each at most 40 characters with no control characters.");
        return normalized;
    }
}
