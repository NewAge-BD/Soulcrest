using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Services;

/// <summary>Atomic, serial persistence of explicit goals and accepted receipt identities. Reads never rewrite damage.</summary>
public sealed class CraftingProgressStore : IDisposable
{
    public const string LoadError = "Das gespeicherte Crafting-Ziel konnte nicht geprüft werden. Die Datei bleibt unverändert.";
    public const string SaveError = "Das Crafting-Ziel konnte nicht gespeichert werden.";
    internal const int MaximumReceipts = 4096;
    private const int MaximumBytes = 8 * 1024 * 1024, MaximumProfiles = 512;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 24,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Action<string, object> _save;
    private readonly ITimer _timer;
    private ImmutableDictionary<string, CraftingProgressPin> _pins = ImmutableDictionary<string, CraftingProgressPin>.Empty;
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private bool _readOnly, _disposed;
    private string? _error;
    private long _revision, _savedRevision;
    public string? Error { get { lock (_gate) return _error; } }
    public bool IsReadOnly { get { lock (_gate) return _readOnly; } }
    public event Action? Changed;
    public CraftingProgressStore() : this(Path.Combine(AppPaths.DataDirectory, "crafting-progress.json")) { }
    public CraftingProgressStore(string path, TimeProvider? time = null)
        : this(path, time, static (file, value) => JsonFile.Save(file, value)) { }
    internal CraftingProgressStore(string path, TimeProvider? time, Action<string, object> save)
    {
        _path = Path.GetFullPath(path); _time = time ?? TimeProvider.System; _save = save;
        _timer = _time.CreateTimer(_ => { _ = FlushAsync(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (!File.Exists(_path)) return;
        try
        {
            using var stream = File.OpenRead(_path);
            if (stream.Length > MaximumBytes) throw new InvalidDataException();
            using var document = JsonDocument.Parse(stream, new() { MaxDepth = 24 });
            RejectDuplicates(document.RootElement);
            var data = document.Deserialize<Envelope>(Options);
            if (data is not { SchemaVersion: 1, Pins: not null, DeletedProfiles: not null }
                || data.Pins.Length > MaximumProfiles || data.DeletedProfiles.Length > MaximumProfiles) throw new InvalidDataException();
            foreach (var id in data.DeletedProfiles)
                if (NormalizeProfile(id) is not { } normalized || normalized != id || !_deleted.Add(id)) throw new InvalidDataException();
            foreach (var pin in data.Pins)
            {
                if (!Valid(pin) || _deleted.Contains(pin.ProfileId) || _pins.ContainsKey(pin.ProfileId)) throw new InvalidDataException();
                _pins = _pins.Add(pin.ProfileId, pin);
            }
        }
        catch (Exception error) when (StorageError(error)) { _readOnly = true; _error = LoadError; }
    }
    public CraftingProgressPin? Read(string? profileId)
    {
        var id = NormalizeProfile(profileId);
        lock (_gate) return id is not null && !_readOnly && !_deleted.Contains(id) ? _pins.GetValueOrDefault(id) : null;
    }
    public bool Stage(CraftingProgressPin pin)
    {
        if (!Valid(pin)) return false;
        lock (_gate)
        {
            if (_disposed || _readOnly || _deleted.Contains(pin.ProfileId)) return false;
            if (!_pins.ContainsKey(pin.ProfileId) && _pins.Count >= MaximumProfiles) { _error = SaveError; return false; }
            _pins = _pins.SetItem(pin.ProfileId, pin); _revision++;
            _timer.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
        return true;
    }
    public void Clear(string profileId) => Remove(profileId, false);
    public void DeleteProfile(string profileId) => Remove(profileId, true);
    private void Remove(string profileId, bool deleted)
    {
        if (NormalizeProfile(profileId) is not { } id) return;
        lock (_gate)
        {
            if (_disposed || _readOnly) return;
            var changed = _pins.ContainsKey(id);
            _pins = _pins.Remove(id);
            if (deleted) changed |= _deleted.Add(id);
            if (!changed) return;
            _revision++; _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
    public async Task FlushAsync()
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                Envelope envelope; long revision;
                lock (_gate)
                {
                    if (_readOnly || _revision == _savedRevision) return;
                    revision = _revision;
                    envelope = new(1, _pins.Values.OrderBy(pin => pin.ProfileId, StringComparer.Ordinal).ToArray(), _deleted.Order(StringComparer.Ordinal).ToArray());
                }
                try
                {
                    if (JsonSerializer.SerializeToUtf8Bytes(envelope).Length > MaximumBytes) throw new InvalidDataException();
                    await Task.Run(() => _save(_path, envelope)).ConfigureAwait(false);
                    lock (_gate) { _savedRevision = revision; _error = null; }
                }
                catch (Exception error) when (StorageError(error))
                { lock (_gate) _error = SaveError; SafeEvent.Raise(Changed, "Crafting-Zielspeicher"); return; }
                SafeEvent.Raise(Changed, "Crafting-Zielspeicher");
            }
        }
        finally { _writer.Release(); }
    }
    internal static string? NormalizeProfile(string? id) => Guid.TryParse(id, out var value) && value != Guid.Empty ? value.ToString("N") : null;
    private static bool Valid(CraftingProgressPin? pin) => pin is not null && NormalizeProfile(pin.ProfileId) == pin.ProfileId
        && Guid.TryParseExact(pin.PinId, "N", out var pinId) && pinId != Guid.Empty
        && pin.TargetItemId > 0 && pin.TargetQuantity is >= 1 and <= 100000 && pin.ConfirmedQuantity >= 0
        && pin.PinnedAt > DateTimeOffset.UnixEpoch && ValidScenario(pin.Scenario)
        && pin.Scenario.SelectedRecipeId is > 0 && pin.Scenario.TargetItemId == pin.TargetItemId
        && !pin.PlanItems.IsDefaultOrEmpty && pin.PlanItems.Length <= 128
        && pin.PlanItems.All(row => row is not null && row.ItemId > 0 && row.RequiredQuantity is null or >= 0
            && row.RemainingQuantity is null or >= 0)
        && pin.PlanItems.Select(row => row.ItemId).Distinct().Count() == pin.PlanItems.Length
        && pin.PlanItems.Any(row => row.ItemId == pin.TargetItemId)
        && !pin.SeenReceipts.IsDefault && pin.SeenReceipts.Length <= MaximumReceipts
        && pin.SeenReceipts.All(key => !string.IsNullOrWhiteSpace(key) && key.Length <= 160)
        && pin.SeenReceipts.Distinct(StringComparer.Ordinal).Count() == pin.SeenReceipts.Length;
    private static bool ValidScenario(CraftingScenarioDraft? draft) => draft is not null
        && draft.Profession is { Length: > 0 and <= 80 } && draft.Subcategory is { Length: > 0 and <= 80 }
        && draft.Query is { Length: <= 160 } && draft.Quantity is >= 1 and <= 100000
        && draft.SimulationSamples is >= 1 and <= 100000
        && draft.SimulatedLevels is { Count: <= 1024 } && draft.Acquisition is { Count: <= 1024 }
        && draft.RecipeChoices is { Count: <= 1024 } && draft.OwnedItems is { Count: <= 1024 }
        && !draft.ReplacementChoices.IsDefault && draft.ReplacementChoices.Length <= 1024
        && draft.ReplacementChoices.All(row => row is not null)
        && draft.ReplacementChoices.Select(row => (row.RecipeId, row.IngredientId)).Distinct().Count() == draft.ReplacementChoices.Length
        && draft.OwnedItems.All(row => row.Key > 0 && row.Value is >= 0 and <= 1_000_000_000);
    private static bool StorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException
        or InvalidDataException or ArgumentException or NotSupportedException;
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException(); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var row in element.EnumerateArray()) RejectDuplicates(row);
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _timer.Dispose(); }
        FlushAsync().GetAwaiter().GetResult();
    }
    private sealed record Envelope(int SchemaVersion, CraftingProgressPin[] Pins, string[] DeletedProfiles);
}
