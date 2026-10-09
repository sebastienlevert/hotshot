using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hotshot.Core.Naming;

/// <summary>
/// Expands naming patterns such as <c>{yyyy}/{MM}/{timestamp}</c> into safe, collision-free file paths.
/// '/' or '\' in a pattern creates sub-folders; the extension is appended automatically.
/// </summary>
public static class FileNameTemplate
{
    public const string DefaultPattern = "{yyyy}/{MM}/{timestamp}";
    private const int MaxSegmentLength = 120;
    private const string RandomAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly HashSet<char> InvalidChars = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '|', '?', '*'];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    private static readonly string[] KnownExtensions = [".png", ".jpg", ".jpeg", ".mp4", ".gif"];

    public static IReadOnlyList<TokenInfo> Tokens { get; } =
    [
        new("{yyyy}", "Year", "2026"),
        new("{yy}", "Year, 2 digits", "26"),
        new("{MM}", "Month", "10"),
        new("{MMM}", "Month, short name", "Oct"),
        new("{MMMM}", "Month, full name", "October"),
        new("{dd}", "Day", "09"),
        new("{ddd}", "Weekday, short name", "Fri"),
        new("{HH}", "Hour (24h)", "17"),
        new("{hh}", "Hour (12h)", "05"),
        new("{tt}", "AM / PM", "PM"),
        new("{mm}", "Minute", "51"),
        new("{ss}", "Second", "58"),
        new("{fff}", "Milliseconds", "448"),
        new("{timestamp}", "Date and time", "2026-10-09_17-51-58"),
        new("{date}", "Date", "2026-10-09"),
        new("{time}", "Time", "17-51-58"),
        new("{unix}", "Unix time (seconds)", "1791582718"),
        new("{now:format}", "Custom .NET date format", "{now:yyyyMMdd} → 20261009"),
        new("{type}", "Capture type", "region, window, monitor, screen, recording, gif"),
        new("{app}", "Foreground / captured app", "msedge"),
        new("{title}", "Window title", "Inbox - Outlook"),
        new("{monitor}", "Monitor number", "1"),
        new("{width}", "Width in pixels", "1280"),
        new("{height}", "Height in pixels", "720"),
        new("{counter}", "Incrementing number ({counter:4} pads to 4 digits)", "0042"),
        new("{rand}", "Random characters ({rand:8} for 8)", "k3x9qa"),
        new("{guid}", "Unique id", "3f2b…"),
        new("{computer}", "Computer name", Environment.MachineName),
        new("{user}", "User name", Environment.UserName),
    ];

    public static bool UsesCounter(string pattern) =>
        pattern.Contains("{counter", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the unknown or malformed tokens in a pattern (empty when valid).</summary>
    public static IReadOnlyList<string> FindInvalidTokens(string pattern)
    {
        var invalid = new List<string>();
        foreach (var (token, _) in EnumerateTokens(pattern))
        {
            if (token is null)
            {
                invalid.Add("{");
                continue;
            }

            if (!TryExpandToken(token, NamingContext.Sample(), out _))
            {
                invalid.Add("{" + token + "}");
            }
        }

        return invalid;
    }

    /// <summary>Expands tokens without sanitizing; '/' separators are preserved.</summary>
    public static string Expand(string pattern, NamingContext context)
    {
        var sb = new StringBuilder(pattern.Length + 32);
        var last = 0;
        foreach (var (token, start) in EnumerateTokens(pattern))
        {
            sb.Append(pattern, last, start - last);
            if (token is null)
            {
                sb.Append('{');
                last = start + 1;
                continue;
            }

            sb.Append(TryExpandToken(token, context, out var value) ? value.Replace('\\', '/') : "{" + token + "}");
            last = start + token.Length + 2;
        }

        sb.Append(pattern, last, pattern.Length - last);
        return sb.ToString();
    }

    /// <summary>Builds the relative path (sub-folders + file name + extension) for a capture.</summary>
    public static string BuildRelativePath(string pattern, NamingContext context, string? extension = null)
    {
        extension ??= context.Kind.Extension();
        var expanded = Expand(string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern, context);
        var segments = expanded
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeSegment)
            .Where(s => s.Length > 0 && s != "." && s != "..")
            .ToList();

        if (segments.Count == 0)
        {
            segments.Add("capture");
        }

        var fileName = segments[^1];
        foreach (var known in KnownExtensions)
        {
            if (fileName.Length > known.Length && fileName.EndsWith(known, StringComparison.OrdinalIgnoreCase))
            {
                fileName = fileName[..^known.Length].TrimEnd('.', ' ');
                break;
            }
        }

        segments[^1] = (fileName.Length == 0 ? "capture" : fileName) + extension;
        return Path.Combine([.. segments]);
    }

    /// <summary>Builds an absolute, non-existing path under <paramref name="root"/>.</summary>
    public static string BuildPath(string root, string pattern, NamingContext context, string? extension = null,
        Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, BuildRelativePath(pattern, context, extension)));

        if (!candidate.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(fullRoot, Path.GetFileName(candidate));
        }

        return MakeUnique(candidate, exists);
    }

    public static string MakeUnique(string path, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        if (!exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var next = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!exists(next))
            {
                return next;
            }
        }
    }

    public static string SanitizeSegment(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            sb.Append(InvalidChars.Contains(ch) || char.IsControl(ch) ? '-' : ch);
        }

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > MaxSegmentLength)
        {
            result = result[..MaxSegmentLength].TrimEnd('.', ' ');
        }

        var stem = result.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            result = "_" + result;
        }

        return result;
    }

    private static IEnumerable<(string? Token, int Start)> EnumerateTokens(string pattern)
    {
        var i = 0;
        while (i < pattern.Length)
        {
            var open = pattern.IndexOf('{', i);
            if (open < 0)
            {
                yield break;
            }

            var close = pattern.IndexOf('}', open + 1);
            var nextOpen = pattern.IndexOf('{', open + 1);
            if (close < 0 || (nextOpen >= 0 && nextOpen < close))
            {
                yield return (null, open);
                i = open + 1;
                continue;
            }

            yield return (pattern.Substring(open + 1, close - open - 1), open);
            i = close + 1;
        }
    }

    private static bool TryExpandToken(string token, NamingContext ctx, out string value)
    {
        var ts = ctx.Timestamp;
        var inv = CultureInfo.InvariantCulture;
        var name = token;
        string? arg = null;
        var colon = token.IndexOf(':');
        if (colon >= 0)
        {
            name = token[..colon];
            arg = token[(colon + 1)..];
        }

        // Date/time tokens are case-sensitive (MM = month, mm = minute).
        value = name switch
        {
            "yyyy" => ts.ToString("yyyy", inv),
            "yy" => ts.ToString("yy", inv),
            "MM" => ts.ToString("MM", inv),
            "MMM" => ts.ToString("MMM", inv),
            "MMMM" => ts.ToString("MMMM", inv),
            "dd" => ts.ToString("dd", inv),
            "ddd" => ts.ToString("ddd", inv),
            "dddd" => ts.ToString("dddd", inv),
            "HH" => ts.ToString("HH", inv),
            "hh" => ts.ToString("hh", inv),
            "tt" => ts.ToString("tt", inv),
            "mm" => ts.ToString("mm", inv),
            "ss" => ts.ToString("ss", inv),
            "fff" => ts.ToString("fff", inv),
            _ => null!,
        };
        if (value is not null && arg is null)
        {
            return true;
        }

        switch (name.ToLowerInvariant())
        {
            case "timestamp" when arg is null:
                value = ts.ToString("yyyy-MM-dd_HH-mm-ss", inv);
                return true;
            case "date" when arg is null:
                value = ts.ToString("yyyy-MM-dd", inv);
                return true;
            case "time" when arg is null:
                value = ts.ToString("HH-mm-ss", inv);
                return true;
            case "unix" when arg is null:
                value = new DateTimeOffset(ts).ToUnixTimeSeconds().ToString(inv);
                return true;
            case "now" when !string.IsNullOrEmpty(arg):
                try
                {
                    value = ts.ToString(arg, inv);
                    return true;
                }
                catch (FormatException)
                {
                    value = string.Empty;
                    return false;
                }
            case "type" when arg is null:
                value = ctx.Kind.TokenValue();
                return true;
            case "app" when arg is null:
                value = string.IsNullOrWhiteSpace(ctx.AppName) ? "unknown" : ctx.AppName!;
                return true;
            case "title" when arg is null:
                value = string.IsNullOrWhiteSpace(ctx.WindowTitle) ? (ctx.AppName ?? "untitled") : ctx.WindowTitle!;
                value = value.Replace('/', '-').Replace('\\', '-');
                return true;
            case "monitor" when arg is null:
                value = ctx.MonitorIndex?.ToString(inv) ?? "all";
                return true;
            case "width" when arg is null:
                value = ctx.Width.ToString(inv);
                return true;
            case "height" when arg is null:
                value = ctx.Height.ToString(inv);
                return true;
            case "counter":
                var pad = ParseArg(arg, 1, 1, 12);
                if (pad < 0) break;
                value = ctx.Counter.ToString(new string('0', pad), inv);
                return true;
            case "rand":
                var len = ParseArg(arg, 6, 1, 32);
                if (len < 0) break;
                value = RandomNumberGenerator.GetString(RandomAlphabet, len);
                return true;
            case "guid" when arg is null:
                value = Guid.NewGuid().ToString("N");
                return true;
            case "computer" when arg is null:
                value = ctx.ComputerName ?? Environment.MachineName;
                return true;
            case "user" when arg is null:
                value = ctx.UserName ?? Environment.UserName;
                return true;
        }

        value = string.Empty;
        return false;
    }

    private static int ParseArg(string? arg, int fallback, int min, int max)
    {
        if (arg is null)
        {
            return fallback;
        }

        return int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? n
            : -1;
    }
}
