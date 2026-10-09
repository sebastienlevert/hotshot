namespace Hotshot.Core.Settings;

public static class SettingsSearch
{
    public static int Score(string query, string title, string section, string keywords)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return 0;
        var searchable = $"{title} {section} {keywords}";
        if (!terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase))) return 0;
        var normalized = string.Join(' ', terms);
        if (title.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return 100;
        if (title.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return 80;
        return terms.All(term => title.Contains(term, StringComparison.OrdinalIgnoreCase)) ? 60 : 40;
    }
}
