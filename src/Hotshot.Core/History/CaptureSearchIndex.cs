namespace Hotshot.Core.History;

internal sealed class CaptureSearchIndex
{
    private readonly Dictionary<string, HashSet<int>> _grams = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<HistoryItem> _items;
    public CaptureSearchIndex(IReadOnlyList<HistoryItem> items)
    {
        _items = items;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var text = string.Join("\n", new[] { item.FileName, item.Kind.ToString(), item.Kind.DisplayName(),
                item.AppName, item.WindowTitle, item.Project, item.Summary, item.Description, item.RecognizedText }
                .Concat(item.Tags ?? [])).ToUpperInvariant();
            foreach (var gram in Grams(text))
            {
                if (!_grams.TryGetValue(gram, out var ids)) _grams.Add(gram, ids = []);
                ids.Add(i);
            }
        }
    }
    public IEnumerable<HistoryItem> Search(string query)
    {
        var plain = query.Trim();
        if (plain.Length < 3 || plain.Contains(':') || plain.Contains('"') || plain.Any(char.IsWhiteSpace))
            return _items.Where(item => item.MatchesSearch(query));
        HashSet<int>? candidates = null;
        foreach (var gram in Grams(plain.ToUpperInvariant()))
        {
            if (!_grams.TryGetValue(gram, out var ids)) return [];
            if (candidates is null) candidates = new HashSet<int>(ids);
            else candidates.IntersectWith(ids);
        }
        return (candidates ?? []).Order().Select(index => _items[index]).Where(item => item.MatchesSearch(query));
    }
    private static IEnumerable<string> Grams(string text)
    {
        for (var i = 0; i + 3 <= text.Length; i++) yield return text.Substring(i, 3);
    }
}
