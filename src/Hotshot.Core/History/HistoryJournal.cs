using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hotshot.Core.History;

internal sealed record HistoryMutation(List<HistoryItem> Upserts, string[] Removed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HistoryMutation))]
internal sealed partial class HistoryMutationJson : JsonSerializerContext;

internal static class HistoryJournal
{
    public static void Append(string path, HistoryMutation mutation)
    {
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mutation, HistoryMutationJson.Default.HistoryMutation) + "\n");
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        file.Write(data);
        file.Flush(flushToDisk: true);
    }

    public static (List<HistoryItem> Items, string? Warning, int Count, bool BlockWrites) Replay(string path, List<HistoryItem> checkpoint)
    {
        if (!File.Exists(path)) return (checkpoint, null, 0, false);
        var items = checkpoint.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var bytes = File.ReadAllBytes(path);
        var start = 0; var count = 0;
        for (var end = 0; end < bytes.Length; end++)
        {
            if (bytes[end] != (byte)'\n') continue;
            HistoryMutation mutation;
            try { mutation = JsonSerializer.Deserialize(bytes.AsSpan(start, end - start), HistoryMutationJson.Default.HistoryMutation)
                ?? throw new JsonException("Empty journal transaction."); }
            catch (JsonException ex)
            {
                return (items.Values.OrderByDescending(item => item.CreatedAt).ToList(),
                    "Capture journal contains an invalid completed transaction; completed earlier changes are visible, original data is retained and writes are paused. " + ex.Message, count, true);
            }
            if (mutation.Removed is null || mutation.Upserts is null || mutation.Upserts.Any(item =>
                item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Path)))
                return (items.Values.OrderByDescending(item => item.CreatedAt).ToList(),
                    "Capture journal has an invalid transaction record; completed earlier changes are visible, original data is retained and writes are paused.", count, true);
            foreach (var id in mutation.Removed) items.Remove(id);
            foreach (var item in mutation.Upserts)
            {
                item.Tags ??= []; item.Project ??= "";
                items[item.Id] = item;
            }
            count++; start = end + 1;
        }
        return (items.Values.OrderByDescending(item => item.CreatedAt).ToList(),
            start < bytes.Length ? "An interrupted final library transaction was ignored. Completed transactions were recovered." : null, count, false);
    }
}
