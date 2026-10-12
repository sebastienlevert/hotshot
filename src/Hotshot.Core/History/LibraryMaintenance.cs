using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace Hotshot.Core.History;

public sealed record StorageGroup(string Name, long Bytes, int Files);
public sealed record CleanupFile(string Path, long Bytes, string Category);
public sealed record LibraryBackupPreview(int Captures, int MissingFiles, long Bytes, bool IncludesOriginals);

public static class LibraryMaintenance
{
    public static StorageGroup[] Inspect(AppPaths paths, IReadOnlyList<HistoryItem> items)
    {
        var captures = items.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
        return [new("Captures", captures.Sum(path => new FileInfo(path).Length), captures.Length),
            Group("Thumbnails", paths.ThumbnailsDirectory), Group("Private originals and annotations", paths.OriginalsDirectory),
            Group("Library checkpoint/journal", paths.DataDirectory, path => Path.GetFileName(path).StartsWith("history.json", StringComparison.Ordinal))];
    }

    public static CleanupFile[] PreviewCleanup(AppPaths paths, IReadOnlyList<HistoryItem> items, bool allOriginals)
    {
        var ids = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var captures = items.Select(item => Path.GetFullPath(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<CleanupFile>();
        foreach (var path in Directory.EnumerateFiles(paths.ThumbnailsDirectory))
            if (!captures.Contains(Path.GetFullPath(path)) && !ids.Contains(Path.GetFileNameWithoutExtension(path))) files.Add(new(path, new FileInfo(path).Length, "Orphan thumbnail"));
        foreach (var path in Directory.EnumerateFiles(paths.OriginalsDirectory))
            if (!captures.Contains(Path.GetFullPath(path)) && (Path.GetExtension(path) is ".png" or ".json") &&
                (allOriginals || !ids.Contains(Path.GetFileNameWithoutExtension(path))))
                files.Add(new(path, new FileInfo(path).Length, allOriginals ? "Private editable original/sidecar" : "Orphan original/sidecar"));
        return files.ToArray();
    }

    public static void DeleteReviewed(AppPaths paths, IEnumerable<CleanupFile> reviewed, IEnumerable<string>? protectedCaptures = null)
    {
        var captures = (protectedCaptures ?? []).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in reviewed)
        {
            var path = Path.GetFullPath(file.Path);
            if (captures.Contains(path)) throw new InvalidOperationException("A reviewed file is now a saved capture. Preview cleanup again.");
            var parents = new[] { paths.ThumbnailsDirectory, paths.OriginalsDirectory }.Select(Path.GetFullPath).ToArray();
            if (!parents.Any(parent => string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Cleanup is limited to reviewed direct thumbnail/original files.");
            if ((File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cleanup does not follow linked original/thumbnail directories.");
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cleanup does not follow symbolic links.");
            if (new FileInfo(path).Length != file.Bytes) throw new IOException("A reviewed file changed. Preview cleanup again.");
            File.Delete(path);
        }
    }

    public static async Task BackupAsync(string destination, HistoryStore history, AppPaths paths, bool originals, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(destination) || !destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an absolute local ZIP backup path.");
        if (history.Items.Any(item => string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A backup must not replace a capture.");
        token.ThrowIfCancellationRequested();
        history.Checkpoint();
        var items = history.Items.Where(item => File.Exists(item.Path)).ToList();
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                await using (var stream = zip.CreateEntry("history.json").Open())
                    await JsonSerializer.SerializeAsync(stream, items, HistoryJsonContext.Default.ListHistoryItem, token);
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    var extension = Path.GetExtension(item.Path).ToLowerInvariant();
                    if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".mp4" or ".gif"))
                        throw new InvalidDataException("Unsupported capture file format in library backup.");
                    await AddAsync(zip, "captures/" + SafeId(item.Id) + extension, item.Path, token);
                    if (originals)
                        foreach (var suffix in new[] { ".png", ".json" })
                        {
                            var file = Path.Combine(paths.OriginalsDirectory, SafeId(item.Id) + suffix);
                            if (File.Exists(file)) await AddAsync(zip, "originals/" + SafeId(item.Id) + suffix, file, token);
                        }
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static LibraryBackupPreview PreviewRestore(string source)
    {
        using var zip = ZipFile.OpenRead(source);
        var items = ReadManifest(zip);
        ValidateEntries(zip, items);
        return new(items.Count, 0, zip.Entries.Sum(entry => entry.Length),
            zip.Entries.Any(entry => entry.FullName.StartsWith("originals/", StringComparison.Ordinal)));
    }

    public static async Task<int> RestoreAsync(string source, string destinationFolder, HistoryStore history, AppPaths paths,
        bool originals, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var zip = ZipFile.OpenRead(source);
        var items = ReadManifest(zip);
        ValidateEntries(zip, items);
        var existing = history.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var import = Path.Combine(Path.GetFullPath(destinationFolder), "Restored-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(import);
        var created = new List<string>();
        var restored = new List<HistoryItem>();
        try
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var oldId = SafeId(item.Id);
                var extension = Path.GetExtension(item.Path).ToLowerInvariant();
                var id = existing.Contains(oldId) ? Guid.NewGuid().ToString("N") : oldId;
                existing.Add(id);
                var output = Path.Combine(import, id + extension);
                await ExtractAsync(zip.GetEntry("captures/" + oldId + extension)!, output, created, token);
                item.Id = id; item.Path = output; item.ThumbnailPath = null; item.IsTemporary = false;
                item.DescriptionPending = false; item.DescriptionNeedsReview = false;
                if (originals)
                    foreach (var suffix in new[] { ".png", ".json" })
                        if (zip.GetEntry("originals/" + oldId + suffix) is { } entry)
                            await ExtractAsync(entry, Path.Combine(paths.OriginalsDirectory, id + suffix), created, token);
                restored.Add(item);
            }
            token.ThrowIfCancellationRequested();
            history.AddRange(restored);
            if (history.LastError is not null) throw new IOException("Restored files were preserved, but the library could not be persisted: " + history.LastError);
            return restored.Count;
        }
        catch
        {
            // If library records exist, preserve imported files instead of leaving broken links.
            var recorded = history.Items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!restored.Any(item => recorded.Contains(item.Path)))
            {
                foreach (var file in created) if (File.Exists(file)) File.Delete(file);
                if (!Directory.EnumerateFileSystemEntries(import).Any()) Directory.Delete(import);
            }
            throw;
        }
    }

    private static List<HistoryItem> ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("history.json") ?? throw new InvalidDataException("This is not a Hotshot library backup.");
        if (entry.Length > 64 * 1024 * 1024) throw new InvalidDataException("Backup manifest exceeds 64 MB.");
        using var stream = entry.Open();
        var items = JsonSerializer.Deserialize(stream, HistoryJsonContext.Default.ListHistoryItem)
            ?? throw new InvalidDataException("Backup manifest is empty.");
        if (items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Path) ||
            item.Width < 1 || item.Height < 1 || (long)item.Width * item.Height > 64_000_000 || item.Tags?.Length > 20 ||
            item.Summary?.Length > 4000 || item.Description?.Length > 16000 || item.Project?.Length > 80) ||
            items.Count > 50000 || items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new InvalidDataException("Backup has duplicate IDs or too many captures.");
        foreach (var item in items) { item.Tags ??= []; item.Project ??= ""; }
        return items;
    }

    private static void ValidateEntries(ZipArchive zip, List<HistoryItem> items)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "history.json" };
        foreach (var item in items)
        {
            var id = SafeId(item.Id);
            var extension = Path.GetExtension(item.Path).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".mp4" or ".gif"))
                throw new InvalidDataException("Backup contains an unsupported capture type.");
            allowed.Add("captures/" + id + extension);
            allowed.Add("originals/" + id + ".png"); allowed.Add("originals/" + id + ".json");
            if (zip.GetEntry("captures/" + id + extension) is null) throw new InvalidDataException("Backup capture is missing.");
        }
        if (zip.Entries.Count > 150001 || zip.Entries.Any(entry => !allowed.Contains(entry.FullName) || entry.Length > 10L * 1024 * 1024 * 1024) ||
            zip.Entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal).Any(group => group.Count() != 1) ||
            zip.Entries.Sum(entry => entry.Length) > 100L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Backup paths/size/count are outside safe restore limits.");
    }
    private static string SafeId(string id)
    {
        if (id.Length is < 1 or > 64 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new InvalidDataException("Invalid capture ID in backup.");
        return id;
    }
    private static async Task AddAsync(ZipArchive zip, string name, string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        await input.CopyToAsync(output, token);
    }
    private static async Task ExtractAsync(ZipArchiveEntry entry, string destination, List<string> created, CancellationToken token)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        created.Add(destination);
        await using var input = entry.Open();
        await input.CopyToAsync(output, token);
    }
    private static StorageGroup Group(string name, string directory, Func<string, bool>? filter = null)
    {
        var files = Directory.EnumerateFiles(directory).Where(path => filter?.Invoke(path) ?? true).ToArray();
        return new(name, files.Sum(path => new FileInfo(path).Length), files.Length);
    }
}
