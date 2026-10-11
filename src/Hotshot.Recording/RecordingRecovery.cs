using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Storage;

namespace Hotshot.Recording;

public sealed record PendingRecording(string Path, int Width, int Height);

[JsonSerializable(typeof(PendingRecording))]
internal sealed partial class RecoveryJsonContext : JsonSerializerContext;

public static class RecordingRecovery
{
    public static async Task BeginAsync(string journal, PendingRecording recording)
    {
        if (File.Exists(journal)) throw new InvalidOperationException("An interrupted recording needs review. Recover it or dismiss its notice in the workspace before starting another recording.");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        var temp = journal + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(recording, RecoveryJsonContext.Default.PendingRecording));
        File.Move(temp, journal, overwrite: true);
    }

    public static async Task<RecordingResult?> InspectAsync(string journal)
    {
        if (!File.Exists(journal)) return null;
        var pending = JsonSerializer.Deserialize(await File.ReadAllTextAsync(journal), RecoveryJsonContext.Default.PendingRecording)
            ?? throw new InvalidDataException("The recording recovery journal is empty.");
        if (!Path.IsPathFullyQualified(pending.Path) || !pending.Path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The recovery journal does not contain a valid MP4 path.");
        var file = await StorageFile.GetFileFromPathAsync(pending.Path);
        var properties = await file.Properties.GetVideoPropertiesAsync();
        if (properties.Duration <= TimeSpan.Zero || properties.Width == 0 || properties.Height == 0)
            throw new InvalidDataException("The interrupted MP4 is not finalized/playable. It was preserved unchanged; it cannot be safely recovered by Hotshot.");
        return new RecordingResult(pending.Path, properties.Duration, (int)properties.Width, (int)properties.Height,
            new FileInfo(pending.Path).Length, false);
    }

    public static void Complete(string journal) { if (File.Exists(journal)) File.Delete(journal); }
    public static void Dismiss(string journal)
    {
        if (!File.Exists(journal)) throw new FileNotFoundException("No recovery notice exists.");
        File.Move(journal, journal + ".archived-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
    }
}
