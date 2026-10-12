using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hotshot.Core.Settings;

public enum SettingsSyncMode { Automatic, Local, OneDrive }

public sealed class SettingsSyncConfiguration
{
    public SettingsSyncMode Mode { get; set; }
    public string? OneDriveRoot { get; set; }
    public string? BaselineJson { get; set; }
    public bool Initialized { get; set; }
    public bool PendingLocalChanges { get; set; }
}

public sealed class RoamingSettingsStore
{
    private const int MaximumSettingsBytes = 2 * 1024 * 1024;
    private readonly SettingsStore _local;
    private readonly string _configurationPath;
    private readonly Func<string?> _discoverRoot;
    private readonly Lock _gate = new();
    private SettingsSyncConfiguration _configuration = new();
    private PortableSettings? _baseline;
    private string? _lastSharedHash;
    private bool _configurationInvalid;
    private readonly bool _forceLocal;
    public string Status { get; private set; } = "Local settings";
    public string? SharedFilePath { get; private set; }
    public string LocalFilePath => _local.FilePath;
    public SettingsSyncMode Mode => _configuration.Mode;
    public bool HasConflict { get; private set; }
    public event Action? StatusChanged;
    public event Action<string>? Failed;

    public RoamingSettingsStore(string localPath, Func<string?>? discoverRoot = null, bool forceLocal = false)
    {
        _local = new SettingsStore(localPath);
        _configurationPath = Path.Combine(Path.GetDirectoryName(localPath)!, "settings-sync.json");
        _discoverRoot = discoverRoot ?? (() => null);
        _forceLocal = forceLocal;
        if (forceLocal) _configuration.Mode = SettingsSyncMode.Local;
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            var local = _local.Load();
            if (_forceLocal) return local;
            try
            {
                if (File.Exists(_configurationPath))
                {
                    _configuration = JsonSerializer.Deserialize(ReadBounded(_configurationPath), SharedSettingsJson.Default.SettingsSyncConfiguration)
                        ?? throw new InvalidDataException("Settings sync configuration is empty.");
                    if (!Enum.IsDefined(_configuration.Mode)) throw new InvalidDataException("Unknown settings sync mode.");
                    if (_configuration.BaselineJson is { } baseline)
                    {
                        _baseline = JsonSerializer.Deserialize(baseline, SharedSettingsJson.Default.PortableSettings)
                            ?? throw new InvalidDataException("Settings sync baseline is empty.");
                        _ = _baseline.ApplyTo(new AppSettings());
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _configurationInvalid = true;
                Error("Settings sync configuration could not be read; local preferences were retained. " + ex.Message);
                return local;
            }
            return LoadShared(local, seed: true);
        }
    }

    public AppSettings Save(AppSettings settings)
    {
        lock (_gate)
        {
            var candidate = settings.Clone();
            if (candidate.Validate().Count > 0) throw new ArgumentException("Settings are invalid.");
            if (_configurationInvalid || !ResolveSharedPath())
            {
                _local.Save(candidate);
                if (!_configurationInvalid && _configuration.Mode != SettingsSyncMode.Local)
                {
                    _configuration.PendingLocalChanges = true;
                    SaveConfiguration();
                    SetStatus("Changes saved locally; OneDrive is unavailable and will be retried.");
                }
                return candidate;
            }
            var mine = PortableSettings.From(candidate);
            try
            {
                if (_configuration.Initialized && !File.Exists(SharedFilePath))
                    throw new IOException("The shared file is currently missing. It was not recreated over another machine's changes.");
                var remote = ReadShared();
                var merged = MergeShared(mine, remote.Preferences);
                var result = merged.ApplyTo(candidate);
                var serialized = EncodeDocument(merged);
                if (Hash(serialized) != remote.Hash)
                    WriteShared(serialized, remote.Hash);
                _local.Save(result);
                SetBaseline(merged, Hash(serialized));
                SetStatus("Preferences saved in OneDrive. Upload/sync is handled by the OneDrive client.");
                return result;
            }
            catch (SettingsSyncConflictException ex)
            {
                HasConflict = true;
                var conflict = _local.FilePath + ".local-conflict-" + Guid.NewGuid().ToString("N") + ".json";
                AtomicWrite(conflict, SettingsStore.Serialize(candidate));
                Error(ex.Message + " Your proposed settings were preserved locally at " + conflict);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                if (ex is JsonException or InvalidDataException) HasConflict = true;
                _local.Save(candidate);
                _configuration.PendingLocalChanges = true;
                SaveConfiguration();
                Error("Changes saved in the local cache; OneDrive is unavailable or invalid and will be retried without overwriting it. " + ex.Message);
                return candidate;
            }
        }
    }

    public AppSettings? Refresh(AppSettings current)
    {
        lock (_gate)
        {
            if (_configurationInvalid || !ResolveSharedPath()) return null;
            if (!_configuration.Initialized)
            {
                var result = LoadShared(current, seed: true);
                return _configuration.Initialized ? result : null;
            }
            try
            {
                var remote = ReadShared();
                if (remote.Hash == _lastSharedHash && !_configuration.PendingLocalChanges) return null;
                var mine = PortableSettings.From(current);
                var merged = MergeShared(mine, remote.Preferences);
                var updated = merged.ApplyTo(current);
                var serialized = EncodeDocument(merged);
                if (_configuration.PendingLocalChanges && Hash(serialized) != remote.Hash) WriteShared(serialized, remote.Hash);
                _local.Save(updated);
                SetBaseline(merged, _configuration.PendingLocalChanges ? Hash(serialized) : remote.Hash);
                SetStatus("OneDrive preferences loaded; device paths and permissions were kept local.");
                return updated;
            }
            catch (SettingsSyncConflictException ex) { HasConflict = true; Error(ex.Message); return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                if (ex is JsonException or InvalidDataException) HasConflict = true;
                Error("Shared preferences could not be reloaded; current settings remain active. " + ex.Message); return null;
            }
        }
    }

    public AppSettings Configure(AppSettings current, SettingsSyncMode mode, string? oneDriveRoot)
    {
        lock (_gate)
        {
            if (_forceLocal) throw new InvalidOperationException("This isolated test instance uses local settings only.");
            if (mode == SettingsSyncMode.OneDrive && (string.IsNullOrWhiteSpace(oneDriveRoot) || !Path.IsPathFullyQualified(oneDriveRoot) || !Directory.Exists(oneDriveRoot)))
                throw new ArgumentException("Choose an existing local OneDrive folder.");
            _configuration = new SettingsSyncConfiguration { Mode = mode, OneDriveRoot = mode == SettingsSyncMode.OneDrive ? oneDriveRoot : null };
            _configurationInvalid = false; _baseline = null; _lastSharedHash = null; HasConflict = false;
            SaveConfiguration();
            if (mode == SettingsSyncMode.Local)
            {
                SharedFilePath = null; _local.Save(current); SetStatus("Preferences are stored locally. The existing OneDrive file is not deleted.");
                return current.Clone();
            }
            return LoadShared(current, seed: true);
        }
    }

    public AppSettings ResolveConflict(AppSettings current, bool useThisMachine)
    {
        lock (_gate)
        {
            if (!ResolveSharedPath()) throw new IOException("OneDrive is unavailable.");
            (PortableSettings Preferences, string Hash) remote;
            try { remote = ReadShared(); }
            catch (Exception ex) when (useThisMachine && (ex is JsonException or InvalidDataException))
            {
                remote = (PortableSettings.From(current), Hash(ReadBounded(SharedFilePath!)));
            }
            var portable = useThisMachine ? PortableSettings.From(current) : remote.Preferences;
            var result = portable.ApplyTo(current);
            var json = EncodeDocument(portable);
            if (useThisMachine && Hash(json) != remote.Hash) WriteShared(json, remote.Hash);
            _local.Save(result);
            SetBaseline(portable, useThisMachine ? Hash(json) : remote.Hash);
            SetStatus("The selected preferences were applied. OneDrive handles transport to other machines.");
            return result;
        }
    }

    private AppSettings LoadShared(AppSettings local, bool seed)
    {
        if (!ResolveSharedPath()) return local;
        try
        {
            if (!File.Exists(SharedFilePath))
            {
                if (!seed || _configuration.Initialized) throw new IOException("The shared settings file is not available yet.");
                var portable = PortableSettings.From(local);
                var json = EncodeDocument(portable);
                WriteShared(json, expectedHash: null);
                SetBaseline(portable, Hash(json));
                SetStatus("Portable preferences were copied to OneDrive. Device-specific settings remain in the local cache.");
                return local;
            }
            var remote = ReadShared();
            var merged = _configuration.PendingLocalChanges
                ? MergeShared(PortableSettings.From(local), remote.Preferences) : remote.Preferences;
            var result = merged.ApplyTo(local);
            var jsonResult = EncodeDocument(merged);
            if (_configuration.PendingLocalChanges && Hash(jsonResult) != remote.Hash) WriteShared(jsonResult, remote.Hash);
            _local.Save(result);
            SetBaseline(merged, _configuration.PendingLocalChanges ? Hash(jsonResult) : remote.Hash);
            SetStatus("Portable preferences loaded from OneDrive; local device settings were retained.");
            return result;
        }
        catch (SettingsSyncConflictException ex) { HasConflict = true; Error(ex.Message); return local; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            if (ex is JsonException or InvalidDataException) HasConflict = true;
            Error("OneDrive preferences are unavailable or invalid. The local cache is active; the shared file was not overwritten. " + ex.Message); return local;
        }
    }

    private bool ResolveSharedPath()
    {
        if (_configuration.Mode == SettingsSyncMode.Local) { SharedFilePath = null; SetStatus("Preferences are stored locally."); return false; }
        var root = _configuration.Mode == SettingsSyncMode.OneDrive ? _configuration.OneDriveRoot : _discoverRoot();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
        { SharedFilePath = null; SetStatus("OneDrive folder is unavailable. Preferences remain in the local cache."); return false; }
        SharedFilePath = Path.Combine(root, "Apps", "Hotshot", "settings.json");
        return true;
    }

    private (PortableSettings Preferences, string Hash) ReadShared()
    {
        var directory = Path.GetDirectoryName(SharedFilePath!)!;
        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "settings*.json")
            .Any(path => !Path.GetFileName(path).Equals("settings.json", StringComparison.OrdinalIgnoreCase)))
            throw new SettingsSyncConflictException(["OneDrive conflict copies exist; review and move the extra settings JSON files before resolving"]);
        var json = ReadBounded(SharedFilePath!);
        return (DecodeDocument(json), Hash(json));
    }

    private static PortableSettings DecodeDocument(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var schema) || schema != 1 ||
            !document.RootElement.TryGetProperty("preferences", out var preferences) || preferences.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The OneDrive file is not a supported portable Hotshot settings document.");
        foreach (var section in new[] { "general", "hotkeys", "naming", "capture", "recording", "gif", "library", "export" })
            if (!preferences.TryGetProperty(section, out var value) || value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Shared settings contain a missing section: " + section + ".");
        var shared = JsonSerializer.Deserialize(json, SharedSettingsJson.Default.SharedSettingsDocument)
            ?? throw new InvalidDataException("Shared settings are empty.");
        _ = shared.Preferences.ApplyTo(new AppSettings());
        return shared.Preferences;
    }

    private void WriteShared(string json, string? expectedHash)
    {
        var path = SharedFilePath ?? throw new IOException("OneDrive folder is unavailable.");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var current = File.Exists(path) ? Hash(ReadBounded(path)) : null;
        if (current != expectedHash) throw new SettingsSyncConflictException(["shared file changed during save"]);
        if (File.Exists(path))
        {
            var previous = ReadBounded(path);
            var backup = path + ".backup";
            try
            {
                _ = DecodeDocument(previous);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            { backup = path + ".invalid-" + Guid.NewGuid().ToString("N"); }
            AtomicWrite(backup, previous);
        }
        AtomicWrite(path, json);
    }

    private PortableSettings MergeShared(PortableSettings mine, PortableSettings remote)
    {
        if (_baseline is not null) return PortableSettingsMerge.Merge(_baseline, mine, remote);
        if (_configuration.PendingLocalChanges && EncodeDocument(mine) != EncodeDocument(remote))
            throw new SettingsSyncConflictException(["local preferences changed before the first shared baseline was available"]);
        return remote;
    }

    private void SetBaseline(PortableSettings portable, string hash)
    {
        _lastSharedHash = hash;
        _configuration.BaselineJson = JsonSerializer.Serialize(portable, SharedSettingsJson.Default.PortableSettings);
        _baseline = JsonSerializer.Deserialize(_configuration.BaselineJson, SharedSettingsJson.Default.PortableSettings)
            ?? throw new InvalidDataException("Settings synchronization baseline is empty.");
        _configuration.Initialized = true; _configuration.PendingLocalChanges = false; HasConflict = false;
        SaveConfiguration();
    }
    private void SaveConfiguration() => AtomicWrite(_configurationPath,
        JsonSerializer.Serialize(_configuration, SharedSettingsJson.Default.SettingsSyncConfiguration));
    private void SetStatus(string status) { if (Status != status) { Status = status; StatusChanged?.Invoke(); } }
    private void Error(string message)
    {
        var changed = Status != message;
        SetStatus(message);
        if (changed) Failed?.Invoke(message);
    }
    private static string EncodeDocument(PortableSettings portable) =>
        JsonSerializer.Serialize(new SharedSettingsDocument { Preferences = portable }, SharedSettingsJson.Default.SharedSettingsDocument);
    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    private static string ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumSettingsBytes) throw new InvalidDataException("Settings file exceeds the 2 MB limit.");
        using var bounded = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (bounded.Length + count > MaximumSettingsBytes) throw new InvalidDataException("Settings file exceeds the 2 MB limit.");
            bounded.Write(buffer, 0, count);
        }
        bounded.Position = 0;
        using var reader = new StreamReader(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
    private static void AtomicWrite(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, json, new UTF8Encoding(false)); File.Move(temp, path, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
