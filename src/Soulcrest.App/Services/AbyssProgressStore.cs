using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Services;

public sealed record SavedAbyssValue(long Value, DateTimeOffset ObservedAt);
public sealed record SavedAbyssProgress(string ProfileId, SavedAbyssValue? AbyssPointBalance,
    SavedAbyssValue? FarmedAbyssPoints, SavedAbyssValue? RankPoints);

/// <summary>Independent atomic cache of authoritative absolute values; deleting a session never changes it.</summary>
public sealed class AbyssProgressStore
{
    private const int MaximumBytes = 1024 * 1024, MaximumProfiles = 4096;
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string _path;
    private readonly Action<string, string> _replace;
    private readonly object _gate = new();

    public AbyssProgressStore(string path) : this(path, (source, target) => File.Move(source, target, true)) { }
    internal AbyssProgressStore(string path, Action<string, string> replace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path); _replace = replace;
        ValidateLocalPath(_path);
    }

    public SavedAbyssProgress? Read(string profileId)
    {
        var id = ProfileId(profileId);
        lock (_gate) return ReadAll().GetValueOrDefault(id);
    }

    public void Save(SavedAbyssProgress record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = ProfileId(record.ProfileId);
        record = record with { ProfileId = id }; Validate(record);
        lock (_gate)
        {
            var records = ReadAll(); records[id] = record;
            if (records.Count > MaximumProfiles) throw new InvalidDataException("Too many Abyss character records.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, records), Options);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Abyss cache is too large.");
            JsonFile.Retry(() => Write(bytes));
        }
    }

    private Dictionary<string, SavedAbyssProgress> ReadAll()
    {
        ValidateLocalPath(_path);
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Abyss cache is too large.");
        try
        {
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicates(json.RootElement);
            var document = json.RootElement.Deserialize<Document>(Options);
            if (document?.SchemaVersion != 1 || document.Characters is null || document.Characters.Count > MaximumProfiles)
                throw new InvalidDataException("Unsupported Abyss cache.");
            var result = new Dictionary<string, SavedAbyssProgress>(StringComparer.Ordinal);
            foreach (var (id, record) in document.Characters)
            {
                if (record is null || ProfileId(id) != id || record.ProfileId != id)
                    throw new InvalidDataException("Invalid local Abyss profile identity.");
                Validate(record);
                if (!result.TryAdd(id, record)) throw new InvalidDataException("Duplicate Abyss profile.");
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidDataException("Invalid Abyss cache JSON.", error); }
    }

    private void Write(byte[] bytes)
    {
        ValidateLocalPath(_path);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory); ValidateLocalPath(_path);
        var temporary = Path.Combine(directory, ".abyss-progress-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            ValidateLocalPath(temporary);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            ValidateLocalPath(_path); ValidateLocalPath(temporary); _replace(temporary, _path);
        }
        finally
        {
            try { ValidateLocalPath(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    private static void Validate(SavedAbyssProgress record)
    {
        _ = ProfileId(record.ProfileId);
        foreach (var value in new[] { record.AbyssPointBalance, record.FarmedAbyssPoints, record.RankPoints })
            if (value is not null && (value.Value < 0 || value.ObservedAt == default))
                throw new InvalidDataException("Invalid absolute Abyss value.");
    }

    private static string ProfileId(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id.ToString("N") : throw new ArgumentException("A non-empty local profile GUID is required.", nameof(value));

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate Abyss cache property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private static void ValidateLocalPath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path[root.Length..].Contains(':')
            || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException("Abyss cache requires a local regular path.");
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || current == path && (attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException("Abyss cache cannot use directories or reparse points.");
        }
    }

    private sealed record Document(int SchemaVersion, Dictionary<string, SavedAbyssProgress>? Characters);
}
