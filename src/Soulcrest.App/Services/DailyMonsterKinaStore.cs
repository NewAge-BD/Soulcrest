using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>A cached server-observed absolute total, keyed only by a local profile GUID.</summary>
public sealed record SavedDailyMonsterKina(string ProfileId, long Total, DateTimeOffset ObservedAt, long Limit = 1_000_000);

/// <summary>
/// Small, atomic per-character cache. Reading never confirms a live source and no local clock
/// guesses the server's reset boundary. A newer qualified server observation may be lower.
/// </summary>
public sealed class DailyMonsterKinaStore
{
    private const int MaximumDocumentBytes = 1024 * 1024, MaximumProfiles = 4096;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Action<string, string> _replace;
    private readonly object _gate = new();

    public DailyMonsterKinaStore(string path) : this(path, (source, target) => File.Move(source, target, overwrite: true)) { }

    internal DailyMonsterKinaStore(string path, Action<string, string> replace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(replace);
        _path = Path.GetFullPath(path);
        _replace = replace;
        ValidateLocalPath(_path);
    }

    public SavedDailyMonsterKina? Read(string profileId)
    {
        var id = NormalizeProfileId(profileId);
        lock (_gate) return ReadAll().GetValueOrDefault(id);
    }

    /// <summary>Store the provided authoritative value, including lower values after a server reset.</summary>
    public void Save(SavedDailyMonsterKina record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = NormalizeProfileId(record.ProfileId);
        if (record.Total < 0) throw new ArgumentOutOfRangeException(nameof(record), "Daily total cannot be negative.");
        if (record.Limit <= 0) throw new ArgumentOutOfRangeException(nameof(record), "Daily limit must be positive.");
        lock (_gate)
        {
            var records = ReadAll();
            records[id] = record with { ProfileId = id };
            if (records.Count > MaximumProfiles) throw new InvalidDataException("Too many cached character records.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, records), Options);
            if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("Daily cache is too large.");
            JsonFile.Retry(() => WriteAtomically(bytes));
        }
    }

    private Dictionary<string, SavedDailyMonsterKina> ReadAll()
    {
        ValidateLocalPath(_path);
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumDocumentBytes) throw new InvalidDataException("Daily cache is too large.");
        try
        {
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            RequireProperties(root, "SchemaVersion", "Characters");
            if (!root.GetProperty("SchemaVersion").TryGetInt32(out var version) || version != 1)
                throw new InvalidDataException("Unsupported daily cache schema.");
            var characters = root.GetProperty("Characters");
            if (characters.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid character records.");
            var result = new Dictionary<string, SavedDailyMonsterKina>(StringComparer.Ordinal);
            foreach (var entry in characters.EnumerateObject())
            {
                if (result.Count >= MaximumProfiles) throw new InvalidDataException("Too many cached character records.");
                if (!TryProfileId(entry.Name, out var id)) throw new InvalidDataException("Invalid local profile GUID.");
                var value = entry.Value;
                RequireProperties(value, "ProfileId", "Total", "ObservedAt", "Limit");
                var savedId = value.GetProperty("ProfileId");
                if (savedId.ValueKind != JsonValueKind.String || !TryProfileId(savedId.GetString(), out var recordId) || id != recordId)
                    throw new InvalidDataException("Profile record does not match its local GUID key.");
                var total = value.GetProperty("Total"); var limit = value.GetProperty("Limit");
                var at = value.GetProperty("ObservedAt");
                if (total.ValueKind != JsonValueKind.Number || !total.TryGetInt64(out var amount) || amount < 0
                    || limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt64(out var maximum) || maximum <= 0
                    || at.ValueKind != JsonValueKind.String || !at.TryGetDateTimeOffset(out var observed))
                    throw new InvalidDataException("Invalid cached daily observation.");
                if (!result.TryAdd(id, new(id, amount, observed, maximum)))
                    throw new InvalidDataException("Duplicate local profile GUID.");
            }
            return result;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid daily cache JSON.", error); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Invalid daily cache value type.", error); }
    }

    private void WriteAtomically(byte[] bytes)
    {
        ValidateLocalPath(_path);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        ValidateLocalPath(_path);
        var temporary = Path.Combine(directory, ".daily-monster-kina-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            ValidateLocalPath(temporary);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            ValidateLocalPath(_path); ValidateLocalPath(temporary);
            _replace(temporary, _path);
        }
        finally
        {
            try { ValidateLocalPath(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    private static string NormalizeProfileId(string profileId) => TryProfileId(profileId, out var id)
        ? id : throw new ArgumentException("A non-empty local profile GUID is required.", nameof(profileId));

    private static bool TryProfileId(string? value, out string id)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty) { id = parsed.ToString("N"); return true; }
        id = ""; return false;
    }

    private static void RequireProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid daily cache object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate daily cache property.");
        if (seen.Count != names.Length) throw new InvalidDataException("Incomplete daily cache record.");
    }

    private static void ValidateLocalPath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path[root.Length..].Contains(':')
            || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException("Daily cache must use a local regular path.");
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are not valid daily cache storage.");
            if (current == path && (attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException("Daily cache target must be a regular file.");
        }
    }

    private sealed record Document(int SchemaVersion, Dictionary<string, SavedDailyMonsterKina> Characters);
}
