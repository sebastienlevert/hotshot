using System.Globalization;
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
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private int _journalRecords;

    public HistoryStore(string path, int maxItems, bool keepSavedCaptures = false)
    {
        _path = path;
        MaxItems = maxItems;
        KeepSavedCaptures = keepSavedCaptures;
    }

    public event EventHandler? Changed;
    public event Action<string>? Failed;
    public string? LastError { get; private set; }
    public string CheckpointPath => _path;
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
            _search = null;
            _dirty.Clear();
            _removed.Clear();
            _journalRecords = 0;
            _recoveredCorruptFile = false;
            LastError = null;
            try
            {
                _writeBlocked = false;
                _items = File.Exists(_path)
                    ? ReadHistory(_path)
                    : [];
            }
            catch (JsonException ex)
            {
                var corrupt = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
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
            if (!_writeBlocked)
            {
                try
                {
                    var replay = HistoryJournal.Replay(_path + ".journal", _items);
                    _items = replay.Items;
                    _journalRecords = replay.Count;
                    _writeBlocked = replay.BlockWrites;
                    if (replay.Warning is { } warning)
                    {
                        Report(warning);
                        if (replay.BlockWrites) return;
                        File.Copy(_path + ".journal", _path + ".journal.interrupted-" + Guid.NewGuid().ToString("N"));
                        WriteCheckpointLocked();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _writeBlocked = true;
                    Report("Capture journal recovery failed; original data was retained and writes are paused: " + ex.Message);
                }
            }
        }

        Prune();
    }

    public void Add(HistoryItem item)
    {
        ValidateItem(item);
        List<HistoryItem> removed;
        bool persisted;
        lock (_gate)
        {
            foreach (var existing in _items.Where(i => i.Id == item.Id ||
                string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            {
                _removed.Add(existing.Id);
                _dirty.Remove(existing.Id);
            }
            _items.RemoveAll(i => i.Id == item.Id || string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, item);
            _removed.Remove(item.Id);
            _dirty.Add(item.Id);
            removed = Trim();
            persisted = SaveLocked();
        }

        if (persisted) DeleteArtifacts(removed);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(HistoryItem item)
    {
        ValidateItem(item);
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == item.Id);
            if (index < 0)
            {
                return;
            }

            _items[index] = item;
            _dirty.Add(item.Id);
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
            _dirty.Add(item.Id);
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
            _dirty.Add(item.Id);
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
            _dirty.Add(item.Id);
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
            _dirty.Add(item.Id);
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
            foreach (var update in updates) { update.Item.Project = collection; update.Item.Tags = update.Tags; _dirty.Add(update.Item.Id); }
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
            _dirty.Add(item.Id);
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
            _dirty.Add(item.Id);
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        HistoryItem? removed;
        bool persisted;
        lock (_gate)
        {
            removed = _items.FirstOrDefault(i => i.Id == id);
            if (removed is null)
            {
                return;
            }

            _items.Remove(removed);
            _removed.Add(removed.Id);
            _dirty.Remove(removed.Id);
            persisted = SaveLocked();
        }

        if (persisted) DeleteArtifacts([removed]);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops entries whose files were deleted or moved, and enforces <see cref="MaxItems"/>.</summary>
    public void Prune()
    {
        List<HistoryItem> removed;
        var persisted = false;
        lock (_gate)
        {
            removed = _items.Where(i => (!KeepSavedCaptures || i.IsTemporary) && !File.Exists(i.Path)).ToList();
            _items.RemoveAll(i => removed.Contains(i));
            foreach (var item in removed) { _removed.Add(item.Id); _dirty.Remove(item.Id); }
            removed.AddRange(Trim());
            if (removed.Count > 0)
            {
                persisted = SaveLocked();
            }
        }

        if (removed.Count > 0)
        {
            if (persisted) DeleteArtifacts(removed);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<HistoryItem> Trim()
    {
        var extra = _items.Where(item => !item.IsFavorite && (!KeepSavedCaptures || item.IsTemporary)).Skip(MaxItems).ToList();
        _items.RemoveAll(extra.Contains);
        foreach (var item in extra) { _removed.Add(item.Id); _dirty.Remove(item.Id); }
        return extra;
    }

    private bool SaveLocked()
    {
        _search = null;
        if (_writeBlocked)
        {
            Report("History changes are in memory only. Fix the read/recovery failure and restart Hotshot before saving the library.");
            return false;
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path) || _recoveredCorruptFile) WriteCheckpointLocked();
            else
            {
                HistoryJournal.Append(_path + ".journal", new HistoryMutation(_items.Where(item => _dirty.Contains(item.Id)).ToList(), _removed.ToArray()));
                _journalRecords++;
                if (_journalRecords >= 256 || new FileInfo(_path + ".journal").Length > 8 * 1024 * 1024)
                    WriteCheckpointLocked();
            }
            _dirty.Clear(); _removed.Clear();
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _writeBlocked = true;
            Report($"The capture file is safe, but library changes could not be saved: {ex.Message}");
            return false;
        }
    }

    public void Checkpoint()
    {
        lock (_gate)
        {
            if (_writeBlocked) throw new IOException("Library writes are paused until the recovery error is resolved.");
            WriteCheckpointLocked();
        }
    }

    private void WriteCheckpointLocked()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, HistoryJsonContext.Default.ListHistoryItem));
            if (File.Exists(_path) && !_recoveredCorruptFile)
            {
                _ = ReadHistory(_path);
                File.Copy(_path, _path + ".backup", overwrite: true);
            }
            File.Move(temp, _path, overwrite: true);
            if (File.Exists(_path + ".journal")) File.Delete(_path + ".journal");
            _journalRecords = 0; _recoveredCorruptFile = false;
            _dirty.Clear(); _removed.Clear();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void AddRange(IEnumerable<HistoryItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var added = items.ToArray();
        foreach (var item in added) ValidateItem(item);
        if (added.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != added.Length)
            throw new ArgumentException("Invalid or duplicate capture IDs.");
        lock (_gate)
        {
            var ids = added.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            _items.RemoveAll(item => ids.Contains(item.Id));
            _items.InsertRange(0, added.OrderByDescending(item => item.CreatedAt));
            foreach (var item in added) _dirty.Add(item.Id);
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<HistoryItem> ReadPage(CaptureLibraryFilter filter, string? project, int limit, int offset, out int total)
    {
        lock (_gate)
        {
            _search ??= new CaptureSearchIndex(_items.ToArray());
            var matches = _search.Search(filter.Query).Where(item => filter.Matches(item, DateTimeOffset.Now) &&
                (string.IsNullOrEmpty(project) || item.Project == project)).ToArray();
            total = matches.Length;
            return matches.Skip(offset).Take(limit).ToArray();
        }
    }

    public void Relink(string id, string newPath, long fileSize)
    {
        if (!Path.IsPathFullyQualified(newPath) || !File.Exists(newPath)) throw new ArgumentException("Choose an existing capture file.");
        lock (_gate)
        {
            if (_items.Any(item => item.Id != id && string.Equals(item.Path, newPath, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The selected file is already in the library.");
            var item = _items.FirstOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException("Capture no longer exists.");
            if (!string.Equals(Path.GetExtension(item.Path), Path.GetExtension(newPath), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose the same file format to preserve the capture record.");
            item.Path = newPath; item.FileSize = fileSize;
            _dirty.Add(id); SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
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
        if (items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(item.Id)) ||
            items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new JsonException("History contains an invalid capture record.");
        foreach (var item in items) { item.Tags ??= []; item.Project ??= ""; }
        return items;
    }

    private static void ValidateItem(HistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Path))
            throw new ArgumentException("Capture ID and path must not be empty.", nameof(item));
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

        lock (_gate)
        {
            if (_items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.ThumbnailPath, path, StringComparison.OrdinalIgnoreCase))) return;
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
}
