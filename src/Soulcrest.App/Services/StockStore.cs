using System.Collections.Immutable;
using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>Only profile-bound aggregates and explicit corrections; no stack keys, names or event replay.</summary>
public sealed class CraftingStockStore : IDisposable
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private ImmutableDictionary<string, StockStoredProfile> _profiles = ImmutableDictionary<string, StockStoredProfile>.Empty;
    private Task _work = Task.CompletedTask;
    private bool _saving, _readOnly, _disposed;
    private long _revision;
    public string? StorageError { get; private set; }
    public event Action? Changed;
    public CraftingStockStore() : this(Path.Combine(AppPaths.DataDirectory, "crafting-stock.json")) { }
    public CraftingStockStore(string path)
    {
        _path = Path.GetFullPath(path);
        if (!File.Exists(_path)) return;
        try
        {
            if (new FileInfo(_path).Length > MaximumBytes) throw new InvalidDataException();
            using var document = JsonDocument.Parse(File.ReadAllText(_path), new() { MaxDepth = 20 });
            RejectDuplicates(document.RootElement);
            var file = document.Deserialize<StoredFile>(new JsonSerializerOptions
                { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 20 });
            if (file is not { SchemaVersion: 1, Profiles: not null } || file.Profiles.Length > 512) throw new InvalidDataException();
            var values = ImmutableDictionary.CreateBuilder<string, StockStoredProfile>(StringComparer.Ordinal);
            foreach (var profile in file.Profiles)
                if (!Valid(profile) || !values.TryAdd(profile.ProfileId, profile)) throw new InvalidDataException();
            _profiles = values.ToImmutable();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { _readOnly = true; StorageError = "Der gespeicherte Itembestand konnte nicht geladen werden. Die vorhandene Datei bleibt unverändert."; }
    }

    public StockStoredProfile? Read(string? profileId)
    { lock (_gate) return profileId is null ? null : _profiles.GetValueOrDefault(profileId); }

    public void Save(StockStoredProfile profile)
    {
        if (!Valid(profile)) throw new ArgumentException("Invalid stock profile.", nameof(profile));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_profiles.ContainsKey(profile.ProfileId) && _profiles.Count >= 512) throw new InvalidDataException("Stock profile capacity reached.");
            _profiles = _profiles.SetItem(profile.ProfileId, profile); _revision++;
            ScheduleLocked();
        }
    }

    public void DeleteProfile(string profileId)
    {
        lock (_gate)
        {
            if (_disposed || !_profiles.ContainsKey(profileId)) return;
            _profiles = _profiles.Remove(profileId); _revision++; ScheduleLocked();
        }
    }

    private void ScheduleLocked()
    {
        if (_saving || _readOnly) return;
        _saving = true;
        _work = Task.Run(WriteLoop);
    }

    private void WriteLoop()
    {
        while (true)
        {
            StoredFile file; long revision;
            lock (_gate) { file = new(1, _profiles.Values.OrderBy(profile => profile.ProfileId, StringComparer.Ordinal).ToArray()); revision = _revision; }
            string? error = null;
            try
            {
                if (JsonSerializer.SerializeToUtf8Bytes(file).Length > MaximumBytes) throw new InvalidDataException();
                JsonFile.Save(_path, file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            { error = "Der Itembestand konnte nicht gespeichert werden."; }
            var notify = false;
            var finished = false;
            lock (_gate)
            {
                notify = StorageError != error; StorageError = error;
                finished = error is not null || _revision == revision;
                if (finished) _saving = false;
            }
            if (notify) SafeEvent.Raise(Changed, "Itembestand-Speicherung");
            if (finished) return;
        }
    }

    public Task FlushAsync() { lock (_gate) return _work; }
    public void Flush() => FlushAsync().GetAwaiter().GetResult();
    public void Dispose() { lock (_gate) _disposed = true; Flush(); }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate stock property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) RejectDuplicates(value);
    }

    private static bool Valid(StockStoredProfile? profile) => profile is not null
        && Guid.TryParse(profile.ProfileId, out var id) && id != Guid.Empty
        && profile.AutomaticItems is not null && profile.ItemObservedAt is not null && profile.Overrides is not null && profile.IgnoredLegacyItems is not null
        && profile.AutomaticItems.Count <= 50000 && profile.ItemObservedAt.Count <= 50000 && profile.Overrides.Count <= 10000 && profile.IgnoredLegacyItems.Count <= 10000
        && profile.AutomaticItems.All(pair => pair.Key > 0 && pair.Value >= 0)
        && profile.ItemObservedAt.All(pair => pair.Key > 0 && profile.AutomaticItems.ContainsKey(pair.Key))
        && profile.Overrides.All(pair => pair.Key > 0 && pair.Value is >= 0 and <= 1_000_000_000)
        && profile.IgnoredLegacyItems.All(item => item > 0)
        && (profile.OwnerFingerprint is null || profile.OwnerFingerprint.Length == 64 && profile.OwnerFingerprint.All(Uri.IsHexDigit));
    private sealed record StoredFile(int SchemaVersion, StockStoredProfile[] Profiles);
}
