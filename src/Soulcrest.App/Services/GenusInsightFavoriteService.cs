using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.Core.GenusInsight;

namespace Soulcrest.App.Services;

public sealed record GenusInsightFavoriteSnapshot(ImmutableHashSet<GenusInsightFavoriteKey> Favorites)
{
    public bool Contains(GenusInsightFavoriteKey key) => Favorites.Contains(key);
}

/// <summary>Global display preferences requested 2026-11-09, independent of character data and observed rolls.</summary>
public sealed class GenusInsightFavoriteService
{
    private const int MaximumFileBytes = 512 * 1024;
    private const int MaximumFavorites = 4096;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Action<string, object> _save;
    private readonly GenusInsightCatalog _catalog;
    private GenusInsightFavoriteSnapshot _snapshot = new(ImmutableHashSet<GenusInsightFavoriteKey>.Empty);
    private readonly bool _writable;
    public event Action? Changed;

    public GenusInsightFavoriteService() : this(Path.Combine(AppPaths.DataDirectory, "genus-insight-favorites.json")) { }
    public GenusInsightFavoriteService(string path) : this(path, static (file, value) => JsonFile.Save(file, value)) { }

    internal GenusInsightFavoriteService(string path, Action<string, object> save, GenusInsightCatalog? catalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(save);
        _path = Path.GetFullPath(path); _save = save; _catalog = catalog ?? GenusInsightCatalog.Default;
        (_snapshot, _writable) = Read();
    }

    public GenusInsightFavoriteSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool Contains(GenusInsightFavoriteKey key) => Snapshot.Contains(key);

    /// <summary>Failed persistence preserves the last accepted snapshot and emits no success event.</summary>
    public void Toggle(GenusInsightFavoriteKey key)
    {
        if (!Selectable(key)) throw new ArgumentOutOfRangeException(nameof(key), "Stat is not available in this genus slot.");
        lock (_gate)
        {
            if (!_writable) throw new InvalidDataException("Existing Genus Insight favorites could not be verified and were preserved.");
            var next = _snapshot.Favorites.Contains(key) ? _snapshot.Favorites.Remove(key) : _snapshot.Favorites.Add(key);
            if (next.Count > MaximumFavorites) throw new InvalidDataException("Too many Genus Insight favorites.");
            var file = new Envelope
            {
                SchemaVersion = 1,
                Favorites = next.OrderBy(row => row.Genus, StringComparer.Ordinal).ThenBy(row => row.Slot)
                    .ThenBy(row => row.StatId, StringComparer.Ordinal)
                    .Select(row => new StoredFavorite(row.Genus, row.Slot, row.StatId)).ToArray()
            };
            _save(_path, file);
            _snapshot = new(next);
        }
        SafeEvent.Raise(Changed, "Gattungs-Favoriten");
    }

    private bool Selectable(GenusInsightFavoriteKey key) => key.Slot is >= 1 and <= 9
        && key.StatId is not null && _catalog.Options(key.Genus, key.Slot).Any(option => option.StatId == key.StatId);

    private (GenusInsightFavoriteSnapshot Snapshot, bool Writable) Read()
    {
        if (!File.Exists(_path)) return (_snapshot, true);
        try
        {
            using var stream = File.OpenRead(_path);
            var buffer = new byte[MaximumFileBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumFileBytes) throw new InvalidDataException("Oversized Genus Insight favorites.");
            var content = buffer.AsMemory(0, count);
            if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) content = content[3..];
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 12 });
            RejectDuplicates(document.RootElement);
            var file = document.Deserialize<Envelope>(Options);
            if (file is not { SchemaVersion: 1, Favorites: not null } || file.Favorites.Length > MaximumFavorites)
                throw new InvalidDataException("Invalid Genus Insight favorites.");
            var favorites = ImmutableHashSet.CreateBuilder<GenusInsightFavoriteKey>();
            foreach (var row in file.Favorites)
            {
                if (row is null) throw new InvalidDataException("Invalid Genus Insight favorite.");
                var key = new GenusInsightFavoriteKey(row.Genus, row.Slot, row.StatId);
                if (!Selectable(key) || !favorites.Add(key)) throw new InvalidDataException("Invalid or duplicate Genus Insight favorite.");
            }
            return (new(favorites.ToImmutable()), true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or ArgumentException or NotSupportedException)
        {
            return (_snapshot, false);
        }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate Genus Insight favorite property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private sealed record Envelope
    {
        [JsonPropertyName("schemaVersion")] public required int SchemaVersion { get; init; }
        [JsonPropertyName("favorites")] public required StoredFavorite[] Favorites { get; init; }
    }
    private sealed record StoredFavorite(
        [property: JsonPropertyName("genus")] string Genus,
        [property: JsonPropertyName("slot")] byte Slot,
        [property: JsonPropertyName("statId")] string StatId);
}
