using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hotshot.Core.Updates;

public enum DistributionEdition { Full, Core }

public sealed record DistributionMetadata
{
    public const string FileName = "Hotshot.Distribution.json";
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    [JsonRequired]
    public string Edition { get; init; } = "full";
    public required string Runtime { get; init; }
    public required string AppVersion { get; init; }
    public required string CopilotSdkVersion { get; init; }

    [JsonIgnore]
    public DistributionEdition DistributionEdition => Edition switch
    {
        "full" => DistributionEdition.Full,
        "core" => DistributionEdition.Core,
        _ => throw new InvalidDataException("Unknown Hotshot distribution edition."),
    };

    public void Validate(Architecture processArchitecture)
    {
        if (SchemaVersion != 1 || !ReleaseVersion.IsValid(AppVersion) || !ReleaseVersion.IsValid(CopilotSdkVersion))
            throw new InvalidDataException("Invalid Hotshot distribution metadata.");
        _ = DistributionEdition;
        if (Runtime != UpdateChannel.ForArchitecture(processArchitecture))
            throw new InvalidDataException("Hotshot distribution metadata does not match the process architecture.");
    }

    public static DistributionMetadata? Read(string directory, Architecture processArchitecture)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null; // Releases before editions shipped on the Full channels.
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Distribution metadata is too large.");
        var metadata = JsonSerializer.Deserialize<DistributionMetadata>(File.ReadAllText(path), ComponentJson.Options)
            ?? throw new InvalidDataException("Distribution metadata is empty.");
        metadata.Validate(processArchitecture);
        return metadata;
    }

    public static string Channel(Architecture processArchitecture, DistributionMetadata? metadata)
    {
        metadata?.Validate(processArchitecture);
        return UpdateChannel.ForArchitecture(processArchitecture, metadata?.DistributionEdition ?? DistributionEdition.Full);
    }
}

public static partial class ReleaseVersion
{
    public static bool IsValid(string? value) => value is not null && value.Length <= 80 && VersionPattern().IsMatch(value);
    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

public static class ComponentJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        WriteIndented = true,
    };
}
