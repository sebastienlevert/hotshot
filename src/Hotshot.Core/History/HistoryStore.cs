using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hotshot.Core.History;

public sealed class HistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Path { get; set; }
    public CaptureKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public int Width { get; set; }
    public int Height { get; set; }
    public double? DurationSeconds { get; set; }
    public long FileSize { get; set; }
    public string? AppName { get; set; }
    public string? WindowTitle { get; set; }
    public int? MonitorIndex { get; set; }
    public string? ThumbnailPath { get; set; }
    /// <summary>True when the file lives in the temp folder because "save to file" is disabled.</summary>
    public bool IsTemporary { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? DescriptionError { get; set; }
    public bool DescriptionPending { get; set; }

    public bool MatchesSearch(string query) => string.IsNullOrWhiteSpace(query) ||
        new[] { FileName, Kind.DisplayName(), Summary, Description }.Any(value =>
            value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    [JsonIgnore]
    public string FileName => System.IO.Path.GetFileName(Path);

    [JsonIgnore]
    public bool IsVideo => Kind.IsVideo();
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<HistoryItem>))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>Thread-safe, persisted list of recent captures (newest first).</summary>
public sealed class HistoryStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private List<HistoryItem> _items = [];

    public HistoryStore(string path, int maxItems)
    {
        _path = path;
        MaxItems = maxItems;
    }

    public event EventHandler? Changed;

    public int MaxItems { get; set; }

    public IReadOnlyList<HistoryItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    public HistoryItem? Latest(Func<HistoryItem, bool>? predicate = null)
    {
        lock (_gate)
        {
            return predicate is null ? _items.FirstOrDefault() : _items.FirstOrDefault(predicate);
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                _items = File.Exists(_path)
                    ? JsonSerializer.Deserialize(File.ReadAllText(_path), HistoryJsonContext.Default.ListHistoryItem) ?? []
                    : [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _items = [];
            }
        }

        Prune();
    }

    public void Add(HistoryItem item)
    {
        List<HistoryItem> removed;
        lock (_gate)
        {
            _items.RemoveAll(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, item);
            removed = Trim();
            SaveLocked();
        }

        DeleteArtifacts(removed);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(HistoryItem item)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == item.Id);
            if (index < 0)
            {
                return;
            }

            _items[index] = item;
            SaveLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool UpdateDescription(string id, string? summary, string? description, string? error, long? fileSize = null)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(item => item.Id == id);
            if (item is null) return false;
            if (error is null)
            {
                item.Summary = summary;
                item.Description = description;
            }
            item.DescriptionError = error;
            item.DescriptionPending = false;
            if (fileSize is { } size) item.FileSize = size;
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SetDescriptionPending(string id, bool pending)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(item => item.Id == id);
            if (item is null) return;
            item.DescriptionPending = pending;
            SaveLocked();
        }
    }

    public void Remove(string id)
    {
        HistoryItem? removed;
        lock (_gate)
        {
            removed = _items.FirstOrDefault(i => i.Id == id);
            if (removed is null)
            {
                return;
            }

            _items.Remove(removed);
            SaveLocked();
        }

        DeleteArtifacts([removed]);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops entries whose files were deleted or moved, and enforces <see cref="MaxItems"/>.</summary>
    public void Prune()
    {
        List<HistoryItem> removed;
        lock (_gate)
        {
            removed = _items.Where(i => !File.Exists(i.Path)).ToList();
            _items.RemoveAll(i => removed.Contains(i));
            removed.AddRange(Trim());
            if (removed.Count > 0)
            {
                SaveLocked();
            }
        }

        if (removed.Count > 0)
        {
            DeleteArtifacts(removed);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<HistoryItem> Trim()
    {
        if (_items.Count <= MaxItems)
        {
            return [];
        }

        var extra = _items.GetRange(MaxItems, _items.Count - MaxItems);
        _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        return extra;
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, HistoryJsonContext.Default.ListHistoryItem));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException)
        {
            // History is best-effort; never fail a capture because of it.
        }
    }

    private static void DeleteArtifacts(IEnumerable<HistoryItem> items)
    {
        foreach (var item in items)
        {
            TryDelete(item.ThumbnailPath);
            if (item.IsTemporary)
            {
                TryDelete(item.Path);
            }
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
