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
    public bool IsFavorite { get; set; }
    public string[] Tags { get; set; } = [];
    public string Project { get; set; } = "";
    public bool DescriptionNeedsReview { get; set; }
    public string? RecognizedText { get; set; }
    public string? TextRecognitionError { get; set; }
    public int ImageRevision { get; set; }

    public bool MatchesSearch(string query) => CaptureQuery.Matches(this, query);

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
    private bool _writeBlocked;
    private bool _recoveredCorruptFile;
    private CaptureSearchIndex? _search;

    public HistoryStore(string path, int maxItems, bool keepSavedCaptures = false)
    {
        _path = path;
        MaxItems = maxItems;
        KeepSavedCaptures = keepSavedCaptures;
    }

    public event EventHandler? Changed;
    public event Action<string>? Failed;
    public string? LastError { get; private set; }
    public bool KeepSavedCaptures { get; set; }

    public IReadOnlyList<HistoryItem> Search(string query, int limit = 500, int offset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        lock (_gate)
        {
            _search ??= new CaptureSearchIndex(_items.ToArray());
            return _search.Search(query).Skip(offset).Take(limit).ToArray();
        }
    }

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
                    ? ReadHistory(_path)
                    : [];
            }
            catch (JsonException ex)
            {
                var corrupt = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
                try
                {
                    File.Copy(_path, corrupt);
                    _items = File.Exists(_path + ".backup")
                        ? ReadHistory(_path + ".backup")
                        : [];
                    _recoveredCorruptFile = true;
                    Report($"Capture history was corrupt. The original was preserved at {corrupt}. " +
                        (File.Exists(_path + ".backup") ? "The previous backup was recovered." : "No backup was available; saved image files are unchanged."));
                }
                catch (Exception recovery) when (recovery is JsonException or IOException or UnauthorizedAccessException)
                {
                    _writeBlocked = true;
                    Report($"History recovery failed; writes are paused to preserve the original. {ex.Message} {recovery.Message}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writeBlocked = true;
                Report($"History could not be read. Writes are paused to avoid overwriting existing data: {ex.Message}");
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

    public void HoldDescriptionForReview(string id)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(item => item.Id == id);
            if (item is null) return;
            item.DescriptionPending = false;
            item.DescriptionNeedsReview = true;
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool UpdateOrganization(string id, bool favorite, IEnumerable<string> tags, string? project = null)
    {
        var normalized = CaptureLibrary.NormalizeTags(tags);
        var normalizedProject = project is null ? null : CaptureLibrary.NormalizeProject(project);
        lock (_gate)
        {
            var item = _items.FirstOrDefault(item => item.Id == id);
            if (item is null) return false;
            item.IsFavorite = favorite;
            item.Tags = normalized;
            if (normalizedProject is not null) item.Project = normalizedProject;
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Organize(IEnumerable<string> ids, string project, IEnumerable<string> tags)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var collection = CaptureLibrary.NormalizeProject(project);
        var added = CaptureLibrary.NormalizeTags(tags);
        lock (_gate)
        {
            var items = _items.Where(item => selected.Contains(item.Id)).ToArray();
            if (items.Length != selected.Count) throw new KeyNotFoundException("Some selected captures are no longer in the library.");
            var updates = items.Select(item => (Item: item, Tags: CaptureLibrary.NormalizeTags((item.Tags ?? []).Concat(added)))).ToArray();
            foreach (var update in updates) { update.Item.Project = collection; update.Item.Tags = update.Tags; }
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool UpdateRecognizedText(string id, string? text, string? error, int? expectedRevision = null)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(item => item.Id == id);
            if (item is null || expectedRevision is { } revision && item.ImageRevision != revision) return false;
            if (error is null) item.RecognizedText = text;
            item.TextRecognitionError = error;
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RecordImageChange(string id, bool clearDescription)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(item => item.Id == id);
            if (item is null) return;
            item.ImageRevision++;
            item.RecognizedText = item.TextRecognitionError = null;
            if (clearDescription)
            {
                item.Summary = item.Description = item.DescriptionError = null;
                item.DescriptionPending = false;
            }
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
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
            removed = _items.Where(i => (!KeepSavedCaptures || i.IsTemporary) && !File.Exists(i.Path)).ToList();
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
        var extra = _items.Where(item => !item.IsFavorite && (!KeepSavedCaptures || item.IsTemporary)).Skip(MaxItems).ToList();
        _items.RemoveAll(extra.Contains);
        return extra;
    }

    private void SaveLocked()
    {
        _search = null;
        if (_writeBlocked)
        {
            Report("History changes are in memory only. Fix the read/recovery failure and restart Hotshot before saving the library.");
            return;
        }
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, HistoryJsonContext.Default.ListHistoryItem));
            if (File.Exists(_path) && !_recoveredCorruptFile)
            {
                _ = ReadHistory(_path);
                File.Copy(_path, _path + ".backup", overwrite: true);
            }
            File.Move(temp, _path, overwrite: true);
            _recoveredCorruptFile = false;
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Report($"The capture file is safe, but library changes could not be saved: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report($"History temporary-file cleanup failed: {ex.Message}"); }
        }
    }

    private void Report(string message)
    {
        LastError = message;
        Failed?.Invoke(message);
    }

    private static List<HistoryItem> ReadHistory(string path)
    {
        var items = JsonSerializer.Deserialize(File.ReadAllText(path), HistoryJsonContext.Default.ListHistoryItem)
            ?? throw new JsonException("History must be a capture list, not null.");
        if (items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(item.Id)))
            throw new JsonException("History contains an invalid capture record.");
        foreach (var item in items) { item.Tags ??= []; item.Project ??= ""; }
        return items;
    }

    private void DeleteArtifacts(IEnumerable<HistoryItem> items)
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

    private void TryDelete(string? path)
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
            Report($"Could not remove a capture artifact: {ex.Message}");
        }
    }
}
