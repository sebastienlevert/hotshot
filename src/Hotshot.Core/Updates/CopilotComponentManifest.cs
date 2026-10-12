using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Hotshot.Core.Updates;

public sealed record CopilotComponentFile
{
    public required string Path { get; init; }
    [JsonRequired]
    public long Bytes { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record CopilotComponentManifest
{
    public const string SdkVersion = "1.0.19";
    public const long MaximumArchiveBytes = 256L * 1024 * 1024;
    public const long MaximumUnpackedBytes = 512L * 1024 * 1024;
    public const int MaximumFiles = 256;
    public const int MaximumManifestBytes = 128 * 1024;
    public const string ReceiptFileName = "Hotshot.Component.json";
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public required string AppVersion { get; init; }
    public required string Runtime { get; init; }
    public required string CopilotSdkVersion { get; init; }
    public required string FileName { get; init; }
    public long ArchiveBytes { get; init; }
    public long UnpackedBytes { get; init; }
    public required string Sha256 { get; init; }
    public required CopilotComponentFile[] Files { get; init; }

    public static string AssetName(string version, string runtime, string extension)
    {
        if (!ReleaseVersion.IsValid(version) || runtime is not ("win-x64" or "win-arm64") ||
            extension is not ("zip" or "json")) throw new ArgumentException("Invalid Copilot component asset.");
        return $"Hotshot-Copilot-{version}-{runtime}.{extension}";
    }

    public void Validate(string runtime, string sdkVersion, string? requiredAppVersion = null)
    {
        if (SchemaVersion != 1 || !ReleaseVersion.IsValid(AppVersion) ||
            Runtime != runtime || runtime is not ("win-x64" or "win-arm64") ||
            CopilotSdkVersion != sdkVersion || !ReleaseVersion.IsValid(CopilotSdkVersion) ||
            (requiredAppVersion is not null && AppVersion != requiredAppVersion) ||
            FileName != AssetName(AppVersion, Runtime, "zip") ||
            ArchiveBytes is <= 0 or > MaximumArchiveBytes || UnpackedBytes is <= 0 or > MaximumUnpackedBytes ||
            !ValidHash(Sha256) || Files is null || Files.Length is < 2 or > MaximumFiles)
            throw new InvalidDataException("The Copilot component manifest is invalid or incompatible with this app/SDK/architecture.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in Files)
        {
            if (file is null || file.Bytes < 0 || file.Bytes > MaximumUnpackedBytes || !ValidHash(file.Sha256))
                throw new InvalidDataException("Invalid Copilot component file.");
            ValidateRelativePath(file.Path);
            if (!paths.Add(file.Path) || file.Path.Equals(ReceiptFileName, StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetFileName(file.Path).Equals("copilot_runtime.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Duplicate or forbidden Copilot component file.");
            total = checked(total + file.Bytes);
        }
        if (total != UnpackedBytes || !paths.Contains("copilot-runtime.exe") || !paths.Contains("runtime.node"))
            throw new InvalidDataException("The component does not contain the complete stdio runtime.");
        foreach (var path in paths)
            if (paths.Any(other => other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A component file conflicts with a directory.");
    }

    public static void ValidateRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith('/') ||
            path.Split('/').Length > 16) throw new InvalidDataException("Unsafe component archive path.");
        foreach (var segment in path.Split('/'))
        {
            var stem = segment.Split('.')[0];
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9'))
                throw new InvalidDataException("Unsafe component archive path.");
        }
    }

    public static bool ValidHash(string? hash) =>
        hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    public static bool HashMatches(ReadOnlySpan<byte> bytes, string hash) =>
        ValidHash(hash) && CryptographicOperations.FixedTimeEquals(bytes, Convert.FromHexString(hash));
}
