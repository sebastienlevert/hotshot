using System.Text.Json;

namespace Hotshot.Core.Descriptions;

public sealed record CaptureDescription(string Summary, string Description)
{
    public static CaptureDescription Parse(string response)
    {
        response = response.Trim();
        if (response.StartsWith("```", StringComparison.Ordinal))
        {
            var lineEnd = response.IndexOf('\n');
            if (lineEnd < 0 || !response.EndsWith("```", StringComparison.Ordinal) ||
                response[..lineEnd].TrimEnd() is not ("```" or "```json"))
                throw new InvalidDataException("Copilot returned an invalid JSON response.");
            response = response[(lineEnd + 1)..^3].Trim();
        }
        using var json = JsonDocument.Parse(response);
        var summary = json.RootElement.GetProperty("summary").GetString()?.Trim();
        var description = json.RootElement.GetProperty("description").GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 160 ||
            string.IsNullOrWhiteSpace(description) || description.Length > 4000 ||
            summary.Contains('\0') || description.Contains('\0'))
            throw new InvalidDataException("Copilot returned an empty or invalid screenshot description.");
        return new CaptureDescription(summary, description);
    }
}

public interface ICaptureDescriptionProvider : IAsyncDisposable
{
    Task<CaptureDescription> DescribeAsync(byte[] png, CancellationToken cancellationToken);
}
