using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.App.Components;

namespace Soulcrest.App.Services;

public sealed record AchievementFavoriteSnapshot(AchievementProfileBinding? Binding, ImmutableArray<int> FamilyIds)
{
    public ImmutableArray<int> TitleIds { get; init; } = [];
    public bool Contains(int familyId) => FamilyIds.Contains(familyId);
    public bool ContainsTitle(int titleId) => !TitleIds.IsDefaultOrEmpty && TitleIds.Contains(titleId);
}

/// <summary>Explicit user preferences bound to a profile assignment, independently of live progress and selected stage.</summary>
public sealed class AchievementFavoriteService : IDisposable
{
    private const int MaximumFileBytes = 512 * 1024;
    private const int MaximumFavorites = 10000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private readonly string _root;
    private readonly ExplorationService? _profiles;
    private readonly AchievementDefinitionCatalog _definitions;
    private readonly CombatTitleDisplayCatalog _titles;
    private readonly Action<string, object> _save;
    private readonly object _gate = new();
    private readonly Dictionary<AchievementProfileBinding, ReadState> _cache = [];
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private bool _disposed;
    public event Action? Changed;

    public AchievementFavoriteService(string rootPath, ExplorationService? profiles = null)
        : this(rootPath, profiles, AchievementDefinitionCatalog.Default, SaveAtomically) { }

    internal AchievementFavoriteService(string rootPath, ExplorationService? profiles,
        AchievementDefinitionCatalog definitions, Action<string, object> save, CombatTitleDisplayCatalog? titles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        ValidateLocalPath(_root);
        _profiles = profiles; _definitions = definitions; _save = save; _titles = titles ?? CombatTitleDisplayCatalog.Default;
        if (profiles is not null) profiles.CharacterRemoved += ProfileRemoved;
    }

    /// <summary>Reading creates no files. Inactive and unresolved favorites remain stored but are not effective.</summary>
    public AchievementFavoriteSnapshot Get(AchievementProfileBinding? binding)
    {
        if (binding is null) return new(null, []);
        AchievementStateStore.ValidateBinding(binding);
        if (!AssignmentCurrent(binding)) return new(null, []);
        AchievementFavoriteSnapshot result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsDeleted(binding.ProfileId)) return new(null, []);
            var raw = Read(binding);
            result = new(binding, raw.FamilyIds.Where(Selectable).ToImmutableArray())
                { TitleIds = raw.TitleIds.Where(TitleSelectable).ToImmutableArray() };
        }
        return AssignmentCurrent(binding) ? result : new(null, []);
    }

    public void Toggle(AchievementProfileBinding binding, int familyId)
    {
        AchievementStateStore.ValidateBinding(binding);
        if (!Selectable(familyId)) throw new ArgumentOutOfRangeException(nameof(familyId), "Achievement is not eligible for favorites.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsDeleted(binding.ProfileId) || !AssignmentCurrent(binding))
                throw new InvalidOperationException("Achievement favorite assignment is no longer current.");
            var raw = Read(binding);
            if (!raw.Writable) throw new InvalidDataException("Existing achievement favorites could not be verified and were preserved.");
            var next = raw.FamilyIds.Contains(familyId) ? raw.FamilyIds.Remove(familyId) : raw.FamilyIds.Add(familyId);
            if (next.Length > MaximumFavorites) throw new InvalidDataException("Too many achievement favorites.");
            Persist(binding, next, raw.TitleIds);
        }
        SafeEvent.Raise(Changed, "Achievement-Favoriten");
    }

    public void ToggleTitle(AchievementProfileBinding binding, int titleId)
    {
        AchievementStateStore.ValidateBinding(binding);
        if (!TitleSelectable(titleId)) throw new ArgumentOutOfRangeException(nameof(titleId), "Title is not known in the catalog.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsDeleted(binding.ProfileId) || !AssignmentCurrent(binding))
                throw new InvalidOperationException("Achievement favorite assignment is no longer current.");
            var raw = Read(binding);
            if (!raw.Writable) throw new InvalidDataException("Existing achievement favorites could not be verified and were preserved.");
            var next = raw.TitleIds.Contains(titleId) ? raw.TitleIds.Remove(titleId) : raw.TitleIds.Add(titleId);
            if (next.Length > MaximumFavorites) throw new InvalidDataException("Too many title favorites.");
            Persist(binding, raw.FamilyIds, next);
        }
        SafeEvent.Raise(Changed, "Achievement-Favoriten");
    }

    // A title preference is never silently converted into direct achievement-family preferences.
    private void Persist(AchievementProfileBinding binding, ImmutableArray<int> families, ImmutableArray<int> titles)
    {
        var path = BindingPath(binding);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ValidateLocalPath(path);
        _save(path, new Envelope { SchemaVersion = 2, Binding = binding, FamilyIds = families.ToArray(), TitleIds = titles.ToArray() });
        _cache[binding] = new(families, titles, true);
    }

    private bool Selectable(int familyId) => _definitions.Find(familyId) is { Role: not AchievementRole.Reference }
        && !AchievementContentPolicy.IsRetired(familyId, _definitions.MetadataBuild);
    private bool TitleSelectable(int titleId) => _titles.Find(titleId) is not null;
    private bool AssignmentCurrent(AchievementProfileBinding binding) => _profiles is null
        || _profiles.TryGetAchievementBinding(binding.ProfileId) == binding;

    private ReadState Read(AchievementProfileBinding binding)
    {
        if (_cache.TryGetValue(binding, out var cached)) return cached;
        var path = BindingPath(binding);
        if (!File.Exists(path)) return _cache[binding] = new([], [], true);
        try
        {
            ValidateLocalPath(path);
            if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("Oversized achievement favorites.");
            using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8), new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out var versionValue)
                || versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out var version))
                throw new InvalidDataException("Invalid achievement favorites.");
            AchievementProfileBinding storedBinding;
            int[] families, titles;
            if (version == 1)
            {
                var legacy = document.Deserialize<LegacyEnvelope>(Options)
                    ?? throw new InvalidDataException("Invalid achievement favorites.");
                storedBinding = legacy.Binding; families = legacy.FamilyIds; titles = [];
            }
            else if (version == 2)
            {
                var current = document.Deserialize<Envelope>(Options)
                    ?? throw new InvalidDataException("Invalid achievement favorites.");
                storedBinding = current.Binding; families = current.FamilyIds; titles = current.TitleIds;
            }
            else throw new InvalidDataException("Unsupported achievement favorite version.");
            if (storedBinding != binding || !ValidIds(families) || !ValidIds(titles))
                throw new InvalidDataException("Invalid achievement favorites.");
            return _cache[binding] = new(families.ToImmutableArray(), titles.ToImmutableArray(), true);
        }
        catch (Exception error) when (StorageError(error)) { return _cache[binding] = new([], [], false); }
    }

    private static bool ValidIds(int[]? ids) => ids is { Length: <= MaximumFavorites }
        && ids.All(id => id > 0) && ids.Distinct().Count() == ids.Length;

    private void ProfileRemoved(string profileId)
    {
        AchievementStateStore.ValidateBinding(new(profileId, profileId));
        lock (_gate)
        {
            if (_disposed) return;
            _deleted.Add(profileId);
            foreach (var binding in _cache.Keys.Where(binding => binding.ProfileId == profileId).ToArray()) _cache.Remove(binding);
            try
            {
                Directory.CreateDirectory(_root); ValidateLocalPath(_root);
                _save(TombstonePath(profileId), new { SchemaVersion = 1 });
                var directory = Path.GetFullPath(Path.Combine(_root, profileId));
                if (!directory.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Achievement favorite deletion escaped its root.");
                ValidateLocalPath(directory);
                if (Directory.Exists(directory))
                {
                    ValidateTree(directory);
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception error) when (StorageError(error)) { LogFile.Error("Achievement-Favoriten entfernen", error); }
        }
        SafeEvent.Raise(Changed, "Achievement-Favoriten entfernt");
    }

    private bool IsDeleted(string profileId)
    {
        if (_deleted.Contains(profileId)) return true;
        var path = TombstonePath(profileId); ValidateLocalPath(path);
        return File.Exists(path);
    }
    private string TombstonePath(string profileId) => Path.Combine(_root, ".deleted-" + profileId + ".json");
    private string BindingPath(AchievementProfileBinding binding)
    {
        var path = Path.GetFullPath(Path.Combine(_root, binding.ProfileId, binding.Revision + ".json"));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Achievement favorite path escaped its root.");
        ValidateLocalPath(path); return path;
    }

    private static void ValidateLocalPath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path[root.Length..].Contains(':')
            || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException("Achievement favorites require local regular paths.");
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not valid achievement favorite paths.");
        }
    }
    private static void ValidateTree(string directory)
    {
        ValidateLocalPath(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        { ValidateLocalPath(path); if (Directory.Exists(path)) ValidateTree(path); }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate achievement favorite property."); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    private static bool StorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or ArgumentException or NotSupportedException;
    private static void SaveAtomically(string path, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        JsonFile.Retry(() =>
        {
            ValidateLocalPath(path);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ValidateLocalPath(temporary);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                ValidateLocalPath(path); ValidateLocalPath(temporary);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { ValidateLocalPath(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (StorageError(error)) { }
            }
        });
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        if (_profiles is not null) _profiles.CharacterRemoved -= ProfileRemoved;
    }
    private sealed record ReadState(ImmutableArray<int> FamilyIds, ImmutableArray<int> TitleIds, bool Writable);
    private sealed record LegacyEnvelope
    {
        public required int SchemaVersion { get; init; }
        public required AchievementProfileBinding Binding { get; init; }
        public required int[] FamilyIds { get; init; }
    }
    private sealed record Envelope
    {
        public required int SchemaVersion { get; init; }
        public required AchievementProfileBinding Binding { get; init; }
        public required int[] FamilyIds { get; init; }
        public required int[] TitleIds { get; init; }
    }
}
