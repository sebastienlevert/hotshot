namespace Hotshot.Core.Naming;

/// <summary>Everything a naming pattern can reference about a capture.</summary>
public sealed class NamingContext
{
    public required DateTime Timestamp { get; init; }
    public required CaptureKind Kind { get; init; }
    public string? AppName { get; init; }
    public string? WindowTitle { get; init; }
    /// <summary>1-based monitor index, or null for multi-monitor/unknown captures.</summary>
    public int? MonitorIndex { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Value for {counter}; callers increment a persisted counter when the pattern uses it.</summary>
    public long Counter { get; init; } = 1;
    public string? ComputerName { get; init; }
    public string? UserName { get; init; }

    public static NamingContext Sample(CaptureKind kind = CaptureKind.Region) => new()
    {
        Timestamp = DateTime.Now,
        Kind = kind,
        AppName = "msedge",
        WindowTitle = "Hotshot - Microsoft Edge",
        MonitorIndex = 1,
        Width = 1280,
        Height = 720,
        Counter = 42,
    };
}

public sealed record TokenInfo(string Token, string Description, string Example);
