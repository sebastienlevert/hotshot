using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hotshot.Core.Agents;

public sealed record AgentRect(int X, int Y, int Width, int Height)
{
    public void Validate()
    {
        if (Width < 1 || Height < 1 || Width > 32768 || Height > 32768 || (long)Width * Height > 64_000_000 ||
            (long)X + Width > int.MaxValue || (long)Y + Height > int.MaxValue ||
            X is < -32768 or > 32768 || Y is < -32768 or > 32768)
            throw new ArgumentException("Capture region must use valid physical-pixel coordinates and be at most 64 megapixels.");
    }
}

public sealed record AgentRequest
{
    public string Operation { get; init; } = "";
    public string? WindowId { get; init; }
    public int? MonitorIndex { get; init; }
    public AgentRect? Region { get; init; }
    public string? CaptureId { get; init; }
    public string? BeforeId { get; init; }
    public int Offset { get; init; }
    public int Tolerance { get; init; } = 8;
    public string Query { get; init; } = "";
    public int Limit { get; init; } = 10;
    public int MaxImageSize { get; init; } = 1600;
    public bool IncludeImage { get; init; } = true;

    public void Validate()
    {
        if (Operation is not ("status" or "windows" or "monitors" or "capture_window" or "capture_region" or
            "capture_monitor" or "search_history" or "get_capture" or "get_capture_region" or "compare_captures" or "read_capture_text" or "activity")) throw new ArgumentException("Unknown agent operation.");
        if (Limit is < 1 or > 50 || MaxImageSize is < 256 or > 2048 || Query is null || Query.Length > 500 ||
            WindowId?.Length > 32 || CaptureId?.Length > 64 || BeforeId?.Length > 64 || Offset is < 0 or > 100000 ||
            Tolerance is < 0 or > 255) throw new ArgumentException("Agent request exceeds supported limits.");
        if (Operation == "capture_region") (Region ?? throw new ArgumentException("A region is required.")).Validate();
        if (Operation == "capture_window" && string.IsNullOrWhiteSpace(WindowId)) throw new ArgumentException("A window ID is required.");
        if (Operation == "capture_monitor" && MonitorIndex is not > 0) throw new ArgumentException("A monitor index is required.");
        if (Operation is "get_capture" or "get_capture_region" or "compare_captures" or "read_capture_text" && string.IsNullOrWhiteSpace(CaptureId))
            throw new ArgumentException("A capture ID is required.");
        if (Operation == "get_capture_region")
        {
            (Region ?? throw new ArgumentException("An image region is required.")).Validate();
            if (Region.X < 0 || Region.Y < 0) throw new ArgumentException("Image regions use non-negative image coordinates.");
        }
        if (Operation == "compare_captures" && string.IsNullOrWhiteSpace(BeforeId)) throw new ArgumentException("A before-capture ID is required.");
    }
}

public sealed record AgentCapture(string Id, string Path, string Kind, int Width, int Height, DateTimeOffset CreatedAt,
    string? Summary, string? Description, string? RecognizedText, string? ImageBase64 = null,
    int? ImageWidth = null, int? ImageHeight = null, AgentRect? ImageRegion = null);
public sealed record AgentComparison(string BeforeId, string AfterId, long ChangedPixels, double ChangedPercent, AgentRect? ChangedBounds, int Tolerance);
public sealed record AgentWord(string Text, AgentRect Bounds);
public sealed record AgentText(int Width, int Height, string Text, AgentWord[] Words);
public sealed record AgentActivity(DateTimeOffset Timestamp, string Operation, bool Success, string? ErrorCode);
public sealed record AgentWindow(string Id, string Title, string App, AgentRect Bounds);
public sealed record AgentMonitor(int Index, string Name, AgentRect Bounds, double Scale, bool Primary);
public sealed record AgentStatus(bool Enabled, bool HistoryAccess, string Version, string? SaveFolder = null,
    bool? SaveToFile = null, bool? CopyToClipboard = null, bool? IncludeCursor = null,
    int? DelaySeconds = null, bool? LocalOcr = null, bool? CopilotDescriptions = null,
    string[]? AllowedApps = null, bool? RegionCapture = null, bool? MonitorCapture = null);
public sealed record AgentResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public AgentStatus? Status { get; init; }
    public AgentWindow[]? Windows { get; init; }
    public AgentMonitor[]? Monitors { get; init; }
    public AgentCapture? Capture { get; init; }
    public AgentCapture[]? Captures { get; init; }
    public string[]? Warnings { get; init; }
    public AgentComparison? Comparison { get; init; }
    public AgentText? Text { get; init; }
    public AgentActivity[]? Activity { get; init; }
    public static AgentResponse Failure(string code, string message) => new() { ErrorCode = code, Error = message };
}

public static class AgentProtocol
{
    public const int MaxRequestBytes = 16 * 1024;
    public const int MaxResponseBytes = 32 * 1024 * 1024;
    public static string PipeName(string userIdentity, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userIdentity);
        return "Hotshot.Agent.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userIdentity)))[..24] + "." + sessionId;
    }
    public static byte[] Encode(AgentRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, AgentJsonContext.Default.AgentRequest);
    public static byte[] Encode(AgentResponse response) => JsonSerializer.SerializeToUtf8Bytes(response, AgentJsonContext.Default.AgentResponse);
    public static AgentRequest DecodeRequest(byte[] bytes) => JsonSerializer.Deserialize(bytes, AgentJsonContext.Default.AgentRequest)
        ?? throw new InvalidDataException("Agent request is empty.");
    public static AgentResponse DecodeResponse(byte[] bytes) => JsonSerializer.Deserialize(bytes, AgentJsonContext.Default.AgentResponse)
        ?? throw new InvalidDataException("Agent response is empty.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentResponse))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
