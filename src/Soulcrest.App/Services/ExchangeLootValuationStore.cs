using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Services;

/// <summary>Atomic sidecars with immutable receipt identity/models and permanent deletion tombstones.</summary>
public sealed class ExchangeLootValuationStore
{
    private const long MaximumBytes = 16L * 1024 * 1024;
    private const int MaximumEntries = 32_768;
    private readonly string _root;
    private readonly object _gate = new();
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileModel> _files = new(StringComparer.Ordinal);
    private bool _rootIssue;
    private static readonly JsonSerializerOptions Options = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    private sealed record FileModel(int SchemaVersion, string SessionId, bool Deleted,
        ImmutableArray<ExchangeLootValuationEntry> Entries);

    public ExchangeLootValuationStore(string? root = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(AppPaths.DataDirectory, "loot-tracker", "market-valuations"));
        ValidatePath(_root);
    }

    public bool HasIssues { get { lock (_gate) return _rootIssue || _blocked.Count > 0; } }
    public bool IsBlocked(string sessionId) { lock (_gate) return _rootIssue || _blocked.Contains(sessionId); }

    public ImmutableArray<ExchangeLootValuationEntry> ReadAll()
    {
        lock (_gate)
        {
            try
            {
                ValidatePath(_root);
                if (Directory.Exists(_root))
                    foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
                        if (ValidId(Path.GetFileNameWithoutExtension(path))) Load(Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { _rootIssue = true; }
            if (_rootIssue) return [];
            return _files.Values.Where(f => !f.Deleted && !_blocked.Contains(f.SessionId))
                .SelectMany(f => f.Entries).ToImmutableArray();
        }
    }

    public bool IsDeleted(string sessionId)
    { lock (_gate) return Load(sessionId)?.Deleted == true; }

    public bool Put(ExchangeLootValuationEntry entry)
    {
        ValidateEntry(entry);
        lock (_gate)
        {
            var prior = Load(entry.SessionId);
            if (_rootIssue || _blocked.Contains(entry.SessionId) || prior?.Deleted == true) return false;
            var rows = prior?.Entries ?? [];
            var index = -1;
            for (var i = 0; i < rows.Length; i++) if (rows[i].Key == entry.Key) { index = i; break; }
            if (index >= 0)
            {
                var existing = rows[index];
                if (existing == entry) return true;
                if (existing.Status != ExchangeLootValueStatus.Pending || !SameReceipt(existing, entry))
                    throw new InvalidDataException("A frozen external market receipt cannot be rewritten.");
                rows = rows.SetItem(index, entry);
            }
            else
            {
                if (entry.Status is not (ExchangeLootValueStatus.Pending or ExchangeLootValueStatus.Unknown))
                    throw new InvalidDataException("An external valuation requires its persisted receipt first.");
                if (rows.Length >= MaximumEntries) { _blocked.Add(entry.SessionId); return false; }
                rows = rows.Add(entry);
            }
            return Save(new(1, entry.SessionId, false, rows));
        }
    }

    public void Tombstone(string sessionId)
    {
        if (!ValidId(sessionId)) throw new InvalidDataException("Invalid market session id.");
        lock (_gate)
        {
            Load(sessionId);
            if (_rootIssue || _blocked.Contains(sessionId)) return; // Preserve corrupt evidence, still write-protected.
            Save(new(1, sessionId, true, []));
        }
    }

    private FileModel? Load(string id)
    {
        if (!ValidId(id)) throw new InvalidDataException("Invalid market session id.");
        if (_files.TryGetValue(id, out var known)) return known;
        if (_blocked.Contains(id)) return null;
        var path = Path.Combine(_root, id + ".json");
        try
        {
            ValidatePath(path);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Oversized market sidecar.");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16 });
            CheckDuplicateProperties(doc.RootElement);
            var model = doc.RootElement.Deserialize<FileModel>(Options) ?? throw new InvalidDataException("Empty market sidecar.");
            if (model.SchemaVersion != 1 || model.SessionId != id || model.Entries.IsDefault
                || model.Entries.Length > MaximumEntries || model.Deleted && !model.Entries.IsEmpty)
                throw new InvalidDataException("Invalid market sidecar header.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in model.Entries)
            { ValidateEntry(row); if (row.SessionId != id || !keys.Add(row.Key)) throw new InvalidDataException("Duplicate market receipt."); }
            _files[id] = model; return model;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or OverflowException)
        { _blocked.Add(id); return null; }
    }

    private bool Save(FileModel model)
    {
        try
        {
            ValidatePath(_root); Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, model.SessionId + ".json"); ValidatePath(path);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(model, Options);
            if (bytes.LongLength > MaximumBytes) throw new InvalidDataException("Oversized market sidecar.");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ValidatePath(temp);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                ValidatePath(path); File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            _files[model.SessionId] = model; return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _blocked.Add(model.SessionId); _files.Remove(model.SessionId); return false; }
    }

    private static bool SameReceipt(ExchangeLootValuationEntry a, ExchangeLootValuationEntry b) =>
        a.SessionId == b.SessionId && a.ProfileId == b.ProfileId && a.FactId == b.FactId
        && a.ItemId == b.ItemId && a.Quantity == b.Quantity && a.DroppedAt == b.DroppedAt
        && a.Model == b.Model && a.Source == b.Source;

    internal static void ValidateEntry(ExchangeLootValuationEntry row)
    {
        if (row is null || !ValidId(row.SessionId) || !ValidId(row.ProfileId) || !ValidId(row.FactId)
            || row.ItemId <= 0 || row.Quantity <= 0 || row.DroppedAt == default || row.Model is null
            || !row.Model.HasValidFees || row.Source != ExchangeLootValuationEntry.Provider || !Enum.IsDefined(row.Status)
            || row.Model.ServerId <= 0 || !Text(row.Model.Region, 80) || !Text(row.Model.Faction, 80)
            || !Text(row.ServerName, 200) || !Text(row.UnknownReason, 100))
            throw new InvalidDataException("Invalid external market receipt.");
        if (row.Status == ExchangeLootValueStatus.Pending && !row.Model.HasServerSelection)
            throw new InvalidDataException("A pending market receipt needs its captured server context.");
        if (row.Status is ExchangeLootValueStatus.Estimated or ExchangeLootValueStatus.ReferenceOnly)
        {
            if (!row.Model.HasServerSelection || row.UnitPrice is not > 0 || row.GrossKina is null or < 0
                || row.Listings is not > 0 || row.SourceCapturedAt is null || row.SourceCapturedAt == default
                || row.FetchedAt is null || row.ValuedAt is null || row.ValuedAt < row.DroppedAt
                || row.UnknownReason is not null || row.NextAttemptAt is not null)
                throw new InvalidDataException("Invalid external market price proof.");
            var gross = checked(row.UnitPrice.Value * row.Quantity);
            var historicalQuote = new ExchangeMarketQuote(row.Model.ServerId!.Value, row.ItemId,
                row.UnitPrice.Value, row.Listings.Value, row.SourceCapturedAt.Value, row.FetchedAt.Value);
            if (!ExchangeMarketCache.IsUsable(historicalQuote, row.ValuedAt.Value)
                || row.ValuedAt.Value - row.DroppedAt > TimeSpan.FromMinutes(10))
                throw new InvalidDataException("External price proof was not usable at its recorded valuation time.");
            if (row.GrossKina != checked((long)decimal.Floor(gross))
                || row.NetKina != checked((long)decimal.Floor(gross
                    * (1m - row.Model.SalesTaxPercent / 100m - row.Model.RegistrationFeePercent / 100m))))
                throw new InvalidDataException("Invalid external market estimate arithmetic.");
        }
        else if (row.UnitPrice is not null || row.GrossKina is not null || row.NetKina is not null
            || row.SourceCapturedAt is not null || row.FetchedAt is not null || row.ValuedAt is not null
            || row.Listings is not null || row.ServerName is not null)
            throw new InvalidDataException("An unknown market receipt cannot contain a price.");
    }

    private static bool ValidId(string id) => Guid.TryParseExact(id, "N", out var guid) && guid != Guid.Empty;
    private static bool Text(string? value, int maximum) => value is null || value.Length <= maximum && !value.Any(char.IsControl);
    private static void ValidatePath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("Market sidecars require local paths.");
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Market sidecars cannot follow reparse points.");
    }
    private static void CheckDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate market sidecar property."); CheckDuplicateProperties(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) CheckDuplicateProperties(child);
    }
}
