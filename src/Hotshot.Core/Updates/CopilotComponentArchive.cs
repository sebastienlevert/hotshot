using System.IO.Compression;
using System.Security.Cryptography;

namespace Hotshot.Core.Updates;

public static class CopilotComponentArchive
{
    public static async Task ExtractAsync(string archivePath, string destination, CopilotComponentManifest manifest,
        IProgress<ComponentProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        manifest.Validate(manifest.Runtime, manifest.CopilotSdkVersion);
        SafeComponentPaths.EnsureNotLinked(destination);
        if (Directory.Exists(destination)) throw new IOException("Component extraction destination already exists.");
        Directory.CreateDirectory(destination);
        await using var archiveFile = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (archiveFile.Length != manifest.ArchiveBytes ||
            !CopilotComponentManifest.HashMatches(await SHA256.HashDataAsync(archiveFile, cancellationToken), manifest.Sha256))
            throw new InvalidDataException("Component archive integrity verification failed.");
        archiveFile.Position = 0;
        using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read);
        if (archive.Entries.Count > CopilotComponentManifest.MaximumFiles * 2)
            throw new InvalidDataException("Component archive contains too many entries.");
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long unpacked = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/');
            var relative = isDirectory ? entry.FullName[..^1] : entry.FullName;
            CopilotComponentManifest.ValidateRelativePath(relative);
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !seen.Add(relative))
                throw new InvalidDataException("Component archive contains a link or duplicate path.");
            var path = SafeComponentPaths.Child(destination, relative);
            if (isDirectory)
            {
                if (entry.Length != 0 || !expected.Keys.Any(file => file.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Unexpected component directory.");
                Directory.CreateDirectory(path);
                continue;
            }
            if (!expected.TryGetValue(relative, out var file) || entry.Length != file.Bytes)
                throw new InvalidDataException("Component archive differs from its file manifest.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SafeComponentPaths.EnsureNotLinked(path);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long bytes = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                bytes = checked(bytes + read);
                unpacked = checked(unpacked + read);
                if (bytes > file.Bytes || unpacked > manifest.UnpackedBytes)
                    throw new InvalidDataException("Component extraction exceeded its byte limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report(new("Extracting and verifying", unpacked, manifest.UnpackedBytes));
            }
            output.Position = 0;
            if (bytes != file.Bytes ||
                !CopilotComponentManifest.HashMatches(await SHA256.HashDataAsync(output, cancellationToken), file.Sha256))
                throw new InvalidDataException("Component file SHA256 verification failed.");
        }
        if (unpacked != manifest.UnpackedBytes || expected.Keys.Any(path => !seen.Contains(path)))
            throw new InvalidDataException("Component archive is missing required files.");
    }
}

public static class SafeComponentPaths
{
    public static string Child(string root, string relative)
    {
        CopilotComponentManifest.ValidateRelativePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var child = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!child.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Component path escapes the managed directory.");
        EnsureNotLinked(child);
        return child;
    }

    public static void EnsureNotLinked(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Managed component paths cannot contain symbolic links or junctions.");
        }
    }

    public static void DeleteTree(string managedRoot, string directory)
    {
        var root = Path.GetFullPath(managedRoot).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Refusing to delete outside the managed component directory.");
        EnsureNotLinked(full);
        if (!Directory.Exists(full)) return;
        EnsureTreeNotLinked(full);
        Directory.Delete(full, recursive: true);
    }

    public static void EnsureTreeNotLinked(string directory)
    {
        EnsureNotLinked(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to delete a linked component directory.");
            if ((attributes & FileAttributes.Directory) != 0) EnsureTreeNotLinked(path);
        }
    }
}
