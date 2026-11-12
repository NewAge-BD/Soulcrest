using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public sealed record SavedCraftingProficiency(string ProfileId, DateTimeOffset ObservedAt,
    ImmutableDictionary<byte, CharacterCraftingProficiency> Proficiencies);

/// <summary>Separate atomic, local-profile-only history. Corrupt files are preserved rather than overwritten.</summary>
public sealed class CraftingProficiencyStore
{
    private const int MaximumBytes = 1024 * 1024;
    private const int MaximumProfiles = 512;
    private static readonly JsonSerializerOptions ReadOptions = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Action<string, object> _save;
    private ImmutableDictionary<string, SavedCraftingProficiency> _saved = ImmutableDictionary<string, SavedCraftingProficiency>.Empty;
    public bool IsWritable { get; private set; } = true;

    public CraftingProficiencyStore() : this(Path.Combine(AppPaths.DataDirectory, "crafting-proficiency.json")) { }
    public CraftingProficiencyStore(string path) : this(path, static (file, value) => JsonFile.Save(file, value)) { }
    internal CraftingProficiencyStore(string path, Action<string, object> save)
    {
        _path = Path.GetFullPath(path); _save = save;
        if (!File.Exists(_path)) return;
        try
        {
            using var stream = File.OpenRead(_path);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("Oversized crafting history.");
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            var file = document.Deserialize<Envelope>(ReadOptions);
            if (file is not { SchemaVersion: 1, Profiles: not null } || file.Profiles.Length > MaximumProfiles)
                throw new InvalidDataException("Invalid crafting history.");
            var values = _saved.ToBuilder();
            foreach (var row in file.Profiles)
            {
                if (row is null || !Valid(row) || values.ContainsKey(row.ProfileId))
                    throw new InvalidDataException("Invalid crafting profile history.");
                values.Add(row.ProfileId, row);
            }
            _saved = values.ToImmutable();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or ArgumentException or NotSupportedException)
        { IsWritable = false; }
    }

    public SavedCraftingProficiency? Read(string? profileId)
    {
        lock (_gate) return profileId is not null && _saved.TryGetValue(profileId, out var row) ? row : null;
    }

    internal void Save(SavedCraftingProficiency value)
    {
        if (!Valid(value)) throw new InvalidDataException("Invalid crafting profile history.");
        lock (_gate)
        {
            if (!IsWritable) throw new InvalidDataException("Existing crafting history could not be verified.");
            if (_saved.TryGetValue(value.ProfileId, out var previous) && (previous.ObservedAt > value.ObservedAt
                || previous.ObservedAt == value.ObservedAt && previous.Proficiencies.OrderBy(row => row.Key)
                    .SequenceEqual(value.Proficiencies.OrderBy(row => row.Key)))) return;
            var next = _saved.SetItem(value.ProfileId, value);
            if (next.Count > MaximumProfiles) throw new InvalidDataException("Too many crafting profiles.");
            _save(_path, new Envelope(1, next.Values.OrderBy(row => row.ProfileId, StringComparer.Ordinal).ToArray()));
            _saved = next;
        }
    }

    private static bool Valid(SavedCraftingProficiency row) => Guid.TryParse(row.ProfileId, out _)
        && row.ObservedAt != default && row.Proficiencies is { Count: <= 5 }
        && row.Proficiencies.All(pair => pair.Key is >= 1 and <= 5 && pair.Value is { Level: <= 100, Experience: >= 0 }
            && pair.Key == pair.Value.Category);
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate crafting history property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private sealed record Envelope(int SchemaVersion, SavedCraftingProficiency[] Profiles);
}
