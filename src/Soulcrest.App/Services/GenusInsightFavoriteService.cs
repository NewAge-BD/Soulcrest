using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.Core.GenusInsight;

namespace Soulcrest.App.Services;

public sealed record GenusInsightFavoriteSnapshot(ImmutableHashSet<GenusInsightFavoriteKey> Favorites)
{
    public ImmutableHashSet<string> GlobalStats { get; init; } = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    public ImmutableDictionary<GenusInsightFavoriteKey, bool> SlotOverrides { get; init; }
        = ImmutableDictionary<GenusInsightFavoriteKey, bool>.Empty;
    public bool Contains(GenusInsightFavoriteKey key) => Favorites.Contains(key);
}

/// <summary>Global desired stats with explicit slot exceptions (user request 2026-11-12), independent of observed rolls.</summary>
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
    private readonly ImmutableHashSet<GenusInsightFavoriteKey> _selectableKeys;
    private GenusInsightFavoriteSnapshot _snapshot = new(ImmutableHashSet<GenusInsightFavoriteKey>.Empty);
    private readonly bool _writable;
    private bool _saveFailed;
    public event Action? Changed;

    public GenusInsightFavoriteService() : this(Path.Combine(AppPaths.DataDirectory, "genus-insight-favorites.json")) { }
    public GenusInsightFavoriteService(string path) : this(path, static (file, value) => JsonFile.Save(file, value)) { }

    internal GenusInsightFavoriteService(string path, Action<string, object> save, GenusInsightCatalog? catalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(save);
        _path = Path.GetFullPath(path); _save = save; _catalog = catalog ?? GenusInsightCatalog.Default;
        _selectableKeys = _catalog.Slots.SelectMany(slot => slot.Options.Select(option =>
            new GenusInsightFavoriteKey(slot.Genus, slot.Slot, option.StatId))).ToImmutableHashSet();
        (_snapshot, _writable) = Read();
    }

    public GenusInsightFavoriteSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool CanSave => _writable;
    public bool SaveFailed { get { lock (_gate) return _saveFailed; } }
    public bool Contains(GenusInsightFavoriteKey key) => Snapshot.Contains(key);

    /// <summary>Records the opposite effective value as an explicit local exception.</summary>
    public void Toggle(GenusInsightFavoriteKey key)
    {
        if (!Selectable(key)) throw new ArgumentOutOfRangeException(nameof(key), "Stat is not available in this genus slot.");
        lock (_gate)
        {
            RequireWritable();
            Save(_snapshot.GlobalStats, _snapshot.SlotOverrides.SetItem(key, !_snapshot.Contains(key)));
        }
        SafeEvent.Raise(Changed, "Gattungs-Favoriten");
    }

    /// <summary>Changes inheritance in every valid slot pool while preserving explicit local exceptions.</summary>
    public void ToggleGlobal(string statId)
    {
        if (_catalog.FindStat(statId) is null) throw new ArgumentOutOfRangeException(nameof(statId), "Unknown Genus Insight stat.");
        lock (_gate)
        {
            RequireWritable();
            var next = _snapshot.GlobalStats.Contains(statId) ? _snapshot.GlobalStats.Remove(statId) : _snapshot.GlobalStats.Add(statId);
            Save(next, _snapshot.SlotOverrides);
        }
        SafeEvent.Raise(Changed, "Gattungs-Favoriten");
    }

    /// <summary>Removes a local exception so this pair inherits the global desired-stat choice again.</summary>
    public void ResetOverride(GenusInsightFavoriteKey key)
    {
        if (!Selectable(key)) throw new ArgumentOutOfRangeException(nameof(key), "Stat is not available in this genus slot.");
        lock (_gate)
        {
            RequireWritable();
            if (!_snapshot.SlotOverrides.ContainsKey(key)) return;
            Save(_snapshot.GlobalStats, _snapshot.SlotOverrides.Remove(key));
        }
        SafeEvent.Raise(Changed, "Gattungs-Favoriten");
    }

    private bool Selectable(GenusInsightFavoriteKey key) => _selectableKeys.Contains(key);

    private void RequireWritable()
    {
        if (!_writable) throw new InvalidDataException("Existing Genus Insight favorites could not be verified and were preserved.");
    }

    private GenusInsightFavoriteSnapshot Materialize(ImmutableHashSet<string> globalStats,
        ImmutableDictionary<GenusInsightFavoriteKey, bool> slotOverrides)
    {
        var favorites = _selectableKeys.Where(key => slotOverrides.TryGetValue(key, out var local)
            ? local : globalStats.Contains(key.StatId)).ToImmutableHashSet();
        if (favorites.Count > MaximumFavorites || slotOverrides.Count > MaximumFavorites)
            throw new InvalidDataException("Too many Genus Insight favorites.");
        return new(favorites) { GlobalStats = globalStats, SlotOverrides = slotOverrides };
    }

    /// <summary>Failed persistence preserves the last accepted snapshot and emits no success event.</summary>
    private void Save(ImmutableHashSet<string> globalStats, ImmutableDictionary<GenusInsightFavoriteKey, bool> slotOverrides)
    {
        var next = Materialize(globalStats, slotOverrides);
        var file = new Envelope
        {
            SchemaVersion = 2,
            GlobalStats = globalStats.Order(StringComparer.Ordinal).ToArray(),
            SlotOverrides = slotOverrides.OrderBy(row => row.Key.Genus, StringComparer.Ordinal).ThenBy(row => row.Key.Slot)
                .ThenBy(row => row.Key.StatId, StringComparer.Ordinal)
                .Select(row => new StoredOverride
                {
                    Genus = row.Key.Genus, Slot = row.Key.Slot, StatId = row.Key.StatId, IsFavorite = row.Value
                }).ToArray()
        };
        try { _save(_path, file); }
        catch { _saveFailed = true; throw; }
        _saveFailed = false;
        _snapshot = next;
    }

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
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version))
                throw new InvalidDataException("Invalid Genus Insight favorites.");
            var overrides = ImmutableDictionary.CreateBuilder<GenusInsightFavoriteKey, bool>();
            var globalStats = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            if (version == 1)
            {
                var file = document.Deserialize<LegacyEnvelope>(Options);
                if (file is not { Favorites: not null } || file.Favorites.Length > MaximumFavorites)
                    throw new InvalidDataException("Invalid Genus Insight favorites.");
                // Schema 1 is migrated only in RAM. The original file survives until a successful save.
                foreach (var row in file.Favorites)
                {
                    if (row is null) throw new InvalidDataException("Invalid Genus Insight favorite.");
                    var key = new GenusInsightFavoriteKey(row.Genus, row.Slot, row.StatId);
                    if (!Selectable(key) || !overrides.TryAdd(key, true))
                        throw new InvalidDataException("Invalid or duplicate Genus Insight favorite.");
                }
            }
            else if (version == 2)
            {
                var file = document.Deserialize<Envelope>(Options);
                if (file is not { GlobalStats: not null, SlotOverrides: not null }
                    || file.GlobalStats.Length > _catalog.Stats.Count || file.SlotOverrides.Length > MaximumFavorites)
                    throw new InvalidDataException("Invalid Genus Insight favorites.");
                foreach (var stat in file.GlobalStats)
                    if (_catalog.FindStat(stat) is null || !globalStats.Add(stat))
                        throw new InvalidDataException("Invalid or duplicate global Genus Insight stat.");
                foreach (var row in file.SlotOverrides)
                {
                    if (row is null) throw new InvalidDataException("Invalid Genus Insight favorite override.");
                    var key = new GenusInsightFavoriteKey(row.Genus, row.Slot, row.StatId);
                    if (!Selectable(key) || !overrides.TryAdd(key, row.IsFavorite))
                        throw new InvalidDataException("Invalid or duplicate Genus Insight favorite override.");
                }
            }
            else throw new InvalidDataException("Unsupported Genus Insight favorites schema.");
            return (Materialize(globalStats.ToImmutable(), overrides.ToImmutable()), true);
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
        [JsonPropertyName("globalStats")] public required string[] GlobalStats { get; init; }
        [JsonPropertyName("slotOverrides")] public required StoredOverride[] SlotOverrides { get; init; }
    }
    private sealed record LegacyEnvelope
    {
        [JsonPropertyName("schemaVersion")] public required int SchemaVersion { get; init; }
        [JsonPropertyName("favorites")] public required StoredFavorite[] Favorites { get; init; }
    }
    private sealed record StoredOverride
    {
        [JsonPropertyName("genus")] public required string Genus { get; init; }
        [JsonPropertyName("slot")] public required byte Slot { get; init; }
        [JsonPropertyName("statId")] public required string StatId { get; init; }
        [JsonPropertyName("isFavorite")] public required bool IsFavorite { get; init; }
    }
    private sealed record StoredFavorite(
        [property: JsonPropertyName("genus")] string Genus,
        [property: JsonPropertyName("slot")] byte Slot,
        [property: JsonPropertyName("statId")] string StatId);
}
