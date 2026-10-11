using System.Text.RegularExpressions;

namespace Hotshot.Core.History;

public static partial class CaptureQuery
{
    public static bool Matches(HistoryItem item, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return Terms().Matches(query).Select(match => match.Value).All(term =>
        {
            var split = term.IndexOf(':');
            var field = split > 0 ? term[..split].ToLowerInvariant() : "";
            var knownField = field is "tag" or "project" or "app" or "type" or "text";
            var value = (knownField ? term[(split + 1)..] : term).Trim('"');
            IEnumerable<string?> values = field switch
            {
                "tag" => item.Tags ?? [],
                "project" => [item.Project],
                "app" => [item.AppName],
                "type" => [item.Kind.ToString(), item.Kind.DisplayName()],
                "text" => [item.RecognizedText],
                _ => new[] { item.FileName, item.Kind.DisplayName(), item.Summary, item.Description,
                    item.RecognizedText, item.AppName, item.WindowTitle, item.Project }.Concat(item.Tags ?? []),
            };
            return values.Any(text => text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true);
        });
    }

    [GeneratedRegex("(?:[a-zA-Z]+:)?\"[^\"]+\"|\\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Terms();
}
