using System.Collections.Immutable;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Soulcrest.App.Services;

internal enum LifeskillEventKind { Started, Harvested, Position, Gap, Completed, Interrupted, Failed }

/// <summary>Local session journal, deliberately without network identities, socket keys or targets.</summary>
internal sealed record LifeskillSessionEvent
{
    public int SchemaVersion { get; init; } = 1;
    public long Sequence { get; init; }
    public LifeskillEventKind Kind { get; init; }
    public DateTimeOffset At { get; init; }
    public DateOnly? FarmDay { get; init; }
    public TimeSpan Elapsed { get; init; }
    public LootTrackerCharacterSnapshot? Character { get; init; }
    public ImmutableArray<LootTrackerLoot> Items { get; init; } = [];
    public string? MapId { get; init; }
    public string? MapName { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public LootTrackerGatherVisit? GatherVisit { get; init; }
}

internal sealed record LifeskillCheckpoint(int SchemaVersion, string SessionId, long ThroughSequence,
    LootTrackerSessionStatus Status, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    DateTimeOffset? LastLootAt, TimeSpan Elapsed, LootTrackerCharacterSnapshot? Character,
    string Location, long HarvestCount, long ItemCount, bool CoverageLimited)
{
    public long? FailedHarvestCount { get; init; }
    public DateOnly? FarmDay { get; init; }
}

internal sealed record LifeskillStoreRead(IReadOnlyList<LootTrackerSession> Sessions,
    IReadOnlyList<string> Issues)
{
    public bool CleanupPending { get; init; }
}

/// <summary>
/// A private session directory owns one append-only journal and a small atomic checkpoint.
/// Journal records are authoritative: recovery rebuilds them once, never adds them to checkpoint totals.
/// </summary>
public sealed class LifeskillSessionStore
{
    internal const int MaximumRecordBytes = 256 * 1024;
    internal const long MaximumSessionRecords = 1_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _root;
    private readonly Action<string, string> _append;
    private readonly Action<string, object> _save;
    private readonly Action<string, string> _move;
    private readonly Action<string> _delete;

    public LifeskillSessionStore(string root) : this(root, AppendDurably,
        (path, value) => JsonFile.Save(path, value)) { }

    internal LifeskillSessionStore(string root, Action<string, string> append, Action<string, object> save,
        Action<string, string>? move = null, Action<string>? delete = null)
    {
        _root = Path.GetFullPath(root); _append = append; _save = save;
        _move = move ?? Directory.Move; _delete = delete ?? (path => Directory.Delete(path, recursive: true));
        ValidateLocalPath(_root);
    }

    /// <summary>The rename commits deletion atomically. A later cleanup failure cannot resurrect a session.</summary>
    internal bool Delete(string id)
    {
        var directory = SessionDirectory(id);
        ValidateLocalPath(directory);
        ValidateTree(directory);
        var tombstone = Path.Combine(_root, ".deleted-" + Guid.NewGuid().ToString("N"));
        ValidateLocalPath(tombstone);
        _move(directory, tombstone);
        try { ValidateTree(tombstone); _delete(tombstone); return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { return true; }
    }

    private bool CleanupDeleted()
    {
        var pending = false;
        foreach (var directory in Directory.EnumerateDirectories(_root, ".deleted-*"))
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name[9..], "N", out _)) continue;
            try { ValidateLocalPath(directory); ValidateTree(directory); _delete(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { pending = true; }
        }
        return pending;
    }

    private static void ValidateTree(string directory)
    {
        ValidateLocalPath(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are not valid session storage.");
            if (Directory.Exists(path)) ValidateTree(path);
        }
    }

    private static void ValidateLocalPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)
            || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new InvalidDataException("Session storage must be local.");
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are not valid session storage.");
    }

    internal void Append(string id, LifeskillSessionEvent entry)
    {
        if (entry.Sequence > MaximumSessionRecords) throw new InvalidDataException("Session journal record limit reached.");
        var directory = SessionDirectory(id);
        Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(entry) + "\n";
        if (Utf8.GetByteCount(line) > MaximumRecordBytes) throw new InvalidDataException("Session journal record is too large.");
        _append(Path.Combine(directory, "events-0001.jsonl"), line);
    }

    internal void Checkpoint(LifeskillSessionState state)
    {
        var session = state.Snapshot();
        _save(Path.Combine(SessionDirectory(state.Id), "summary-v1.json"), new LifeskillCheckpoint(state.SchemaVersion,
            state.Id, state.Sequence, session.Status, session.StartedAt, session.EndedAt,
            session.LastLootAt, session.Elapsed, session.Character, session.Location,
            session.HarvestCount ?? 0, session.ItemCount ?? 0, session.CoverageLimited)
            { FailedHarvestCount = session.FailedHarvestCount, FarmDay = session.FarmDay });
    }

    internal LifeskillStoreRead Read()
    {
        ValidateLocalPath(_root);
        if (!Directory.Exists(_root)) return new([], []);
        var cleanupPending = CleanupDeleted();
        var sessions = new List<LootTrackerSession>();
        var issues = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(_root).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            var state = new LifeskillSessionState(id);
            var damaged = false;
            try
            {
                ValidateLocalPath(directory);
                var journal = Path.Combine(directory, "events-0001.jsonl");
                if (File.Exists(journal) && (File.GetAttributes(journal) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Reparse journal rejected.");
                if (!File.Exists(journal)) continue;
                damaged |= ReadJournal(journal, state, issues);
                var checkpoint = Path.Combine(directory, "summary-v1.json");
                if (File.Exists(checkpoint))
                {
                    var saved = new FileInfo(checkpoint).Length > 64 * 1024 ? null
                        : JsonSerializer.Deserialize<LifeskillCheckpoint>(File.ReadAllText(checkpoint));
                    if (saved is null || saved.SchemaVersion is not (1 or 2 or 3) || saved.SchemaVersion != state.SchemaVersion || saved.SessionId != id || saved.ThroughSequence > state.Sequence)
                    { damaged = true; issues.Add("invalid-session-checkpoint"); }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or InvalidDataException)
            { damaged = true; issues.Add("unreadable-session-journal"); }
            if (state.Started && state.HasAttempts)
                sessions.Add(state.Snapshot(recovered: true) with { CoverageLimited = state.CoverageLimited || damaged });
        }
        return new(sessions.OrderByDescending(session => session.StartedAt).ToArray(), issues.Distinct(StringComparer.Ordinal).ToArray())
            { CleanupPending = cleanupPending };
    }

    private static bool ReadJournal(string path, LifeskillSessionState state, ICollection<string> issues)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 8192, FileOptions.SequentialScan);
        var buffer = new byte[8192];
        var line = new ArrayBufferWriter<byte>();
        long records = 0;
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            var offset = 0;
            while (offset < count)
            {
                var rest = buffer.AsSpan(offset, count - offset);
                var newline = rest.IndexOf((byte)'\n');
                var length = newline < 0 ? rest.Length : newline;
                if (line.WrittenCount + length > MaximumRecordBytes)
                { issues.Add("journal-record-size-limit"); return true; }
                line.Write(rest[..length]);
                offset += length;
                if (newline < 0) continue;
                offset++;
                if (++records > MaximumSessionRecords)
                { issues.Add("journal-record-count-limit"); return true; }
                // Decode only a completed line. Invalid UTF-8 in an unfinished tail cannot destroy
                // already processed lines, even when they share this read buffer.
                var entry = JsonSerializer.Deserialize<LifeskillSessionEvent>(Utf8.GetString(line.WrittenSpan));
                if (entry is null || !state.TryApply(entry))
                { issues.Add("invalid-journal-record"); return true; }
                line.Clear();
            }
        }
        if (line.WrittenCount == 0) return false;
        issues.Add("incomplete-journal-tail");
        return true;
    }

    private string SessionDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid local session ID.");
        var path = Path.GetFullPath(Path.Combine(_root, id));
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Session path escaped its root.");
        ValidateLocalPath(path);
        return path;
    }

    private static void AppendDurably(string path, string text)
    {
        var bytes = Utf8.GetBytes(text);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

/// <summary>Deterministic journal reducer shared by the writer and recovery; contains no source logic.</summary>
internal sealed class LifeskillSessionState(string id)
{
    private readonly Dictionary<string, LootTrackerLoot> _loot = new(StringComparer.Ordinal);
    private readonly List<(string MapId, string MapName, List<LootTrackerRoutePoint> Points)> _segments = [];
    private bool _break = true;
    private readonly List<LootTrackerGatherVisit> _gatherVisits = [];
    private readonly HashSet<string> _visitIds = new(StringComparer.Ordinal);
    private int _routeSection;
    private ImmutableArray<LootTrackerFarmRoutePoint> _farmRoute;
    private LootTrackerCharacterSnapshot? _character;
    private DateTimeOffset _startedAt;
    private DateOnly _farmDay;
    private DateTimeOffset? _lastLootAt, _lastAttemptAt;
    private TimeSpan _elapsed, _lastAttemptElapsed;
    private LootTrackerSessionStatus _status = LootTrackerSessionStatus.Live;
    private long _harvestCount, _itemCount;
    private long? _failedHarvestCount;
    private string _location = "";

    public string Id { get; } = id;
    public long Sequence { get; private set; }
    public bool Started { get; private set; }
    public bool CoverageLimited { get; private set; }
    public int SchemaVersion { get; private set; }
    public long HarvestCount => _harvestCount;
    public bool HasAttempts => _harvestCount > 0 || _failedHarvestCount > 0;
    public TimeSpan LastAttemptElapsed => _lastAttemptElapsed;
    public DateTimeOffset? LastAttemptAt => _lastAttemptAt;
    public DateTimeOffset? LastLootAt => _lastLootAt;
    public DateOnly FarmDay => _farmDay;
    public int RouteSection => _routeSection;

    public bool CanApply(LifeskillSessionEvent entry)
    {
        if (entry.SchemaVersion is not (1 or 2 or 3) || Started && entry.SchemaVersion != SchemaVersion
            || !Enum.IsDefined(entry.Kind) || entry.Sequence != Sequence + 1
            || entry.Elapsed < TimeSpan.Zero || (Started && _status != LootTrackerSessionStatus.Live)) return false;
        if (!Started)
            return entry.Kind == LifeskillEventKind.Started && entry.Character is { } character
                && entry.At != default && (entry.FarmDay is null || entry.FarmDay == LootFarmDay.Get(entry.At))
                && Guid.TryParse(character.ProfileId, out _) && !string.IsNullOrWhiteSpace(character.Name);
        if (entry.Kind == LifeskillEventKind.Started) return false;
        if (entry.FarmDay is not null) return false;
        if (entry.GatherVisit is { } visit && (SchemaVersion != 3
            || entry.Kind is not (LifeskillEventKind.Harvested or LifeskillEventKind.Failed)
            || !ValidVisit(visit, entry))) return false;
        switch (entry.Kind)
        {
            case LifeskillEventKind.Harvested:
                if (entry.Items.IsDefaultOrEmpty || entry.Elapsed < _lastAttemptElapsed
                    || entry.Items.Any(item => item is null || item.Kind != LootTrackerLootKind.Item || item.Quantity <= 0 || string.IsNullOrWhiteSpace(item.Id))) return false;
                long amount = 0;
                try
                {
                    foreach (var item in entry.Items) amount = checked(amount + item.Quantity);
                    _ = checked(_itemCount + amount);
                    _ = checked(_harvestCount + 1);
                    foreach (var group in entry.Items.GroupBy(item => item.Id))
                    {
                        long added = 0;
                        foreach (var item in group) added = checked(added + item.Quantity);
                        _ = checked((_loot.GetValueOrDefault(group.Key)?.Quantity ?? 0) + added);
                    }
                }
                catch (OverflowException) { return false; }
                return true;
            case LifeskillEventKind.Failed:
                return SchemaVersion >= 2 && entry.Items.IsDefaultOrEmpty
                    && entry.Elapsed >= _lastAttemptElapsed && _failedHarvestCount < long.MaxValue;
            case LifeskillEventKind.Position:
                return SchemaVersion < 3 && !string.IsNullOrWhiteSpace(entry.MapId) && entry.X is { } x && entry.Y is { } y
                    && double.IsFinite(x) && double.IsFinite(y) && x >= 0 && y >= 0;
            default:
                return true;
        }
    }

    public bool TryApply(LifeskillSessionEvent entry)
    {
        if (!CanApply(entry)) return false;
        if (!Started)
        {
            _character = entry.Character;
            _startedAt = entry.At;
            _farmDay = entry.FarmDay ?? LootFarmDay.Get(entry.At);
            SchemaVersion = entry.SchemaVersion;
            _failedHarvestCount = SchemaVersion >= 2 ? 0 : null;
            Started = true;
        }
        if (entry.GatherVisit is { } visit)
        {
            _gatherVisits.Add(visit); _visitIds.Add(visit.Id); _break = false;
            _farmRoute = default;
            if (_location.Length == 0) _location = visit.MapName;
        }
        switch (entry.Kind)
        {
            case LifeskillEventKind.Harvested:
                var amount = entry.Items.Sum(item => item.Quantity);
                foreach (var item in entry.Items)
                    _loot[item.Id] = item with { Quantity = (_loot.GetValueOrDefault(item.Id)?.Quantity ?? 0) + item.Quantity, MobId = null };
                _itemCount += amount;
                _harvestCount++;
                _lastLootAt = entry.At;
                _lastAttemptAt = entry.At;
                _lastAttemptElapsed = entry.Elapsed;
                _elapsed = entry.Elapsed;
                break;
            case LifeskillEventKind.Failed:
                _failedHarvestCount++;
                _lastAttemptAt = entry.At;
                _lastAttemptElapsed = entry.Elapsed;
                _elapsed = entry.Elapsed;
                break;
            case LifeskillEventKind.Position:
                if (_break || _segments.Count == 0 || _segments[^1].MapId != entry.MapId)
                    _segments.Add((entry.MapId!, entry.MapName ?? entry.MapId!, []));
                _break = false;
                _segments[^1].Points.Add(new(entry.X!.Value, entry.Y!.Value, entry.At, entry.Elapsed));
                if (_location.Length == 0) _location = entry.MapName ?? entry.MapId!;
                break;
            case LifeskillEventKind.Gap:
                if (!_break && _gatherVisits.Count > 0) _routeSection++;
                _break = true;
                CoverageLimited = true;
                break;
            case LifeskillEventKind.Completed:
            case LifeskillEventKind.Interrupted:
                _status = entry.Kind == LifeskillEventKind.Completed ? LootTrackerSessionStatus.Completed : LootTrackerSessionStatus.Interrupted;
                _elapsed = _lastAttemptElapsed;
                break;
        }
        Sequence = entry.Sequence;
        return true;
    }

    private bool ValidVisit(LootTrackerGatherVisit visit, LifeskillSessionEvent entry) =>
        Guid.TryParseExact(visit.Id, "N", out _) && !_visitIds.Contains(visit.Id)
        && visit.Section == _routeSection && !string.IsNullOrWhiteSpace(visit.MapId)
        && !string.IsNullOrWhiteSpace(visit.MapName) && !string.IsNullOrWhiteSpace(visit.Name)
        && visit.MapSize > 0 && visit.At == entry.At
        && visit.Succeeded == (entry.Kind == LifeskillEventKind.Harvested)
        && (visit.GatherableId is null or > 0)
        && (visit.CatalogNodeId is null || !string.IsNullOrWhiteSpace(visit.CatalogNodeId) && visit.GatherableId is > 0)
        && ValidCoordinate(visit.OriginalX, visit.MapSize) && ValidCoordinate(visit.OriginalY, visit.MapSize)
        && ValidCoordinate(visit.X, visit.MapSize) && ValidCoordinate(visit.Y, visit.MapSize);

    private static bool ValidCoordinate(double value, int size) => double.IsFinite(value) && value >= 0 && value <= size;

    public LootTrackerSession Snapshot(TimeSpan? liveElapsed = null, bool recovered = false)
    {
        var final = recovered || _status != LootTrackerSessionStatus.Live;
        if (_farmRoute.IsDefault) _farmRoute = LifeskillFarmRouteBuilder.Build(_gatherVisits);
        var elapsed = final ? _lastAttemptElapsed : liveElapsed ?? _elapsed;
        return new()
        {
            Id = Id, Kind = LootTrackerSessionKind.Lifeskill,
            Status = recovered && _status == LootTrackerSessionStatus.Live ? LootTrackerSessionStatus.Interrupted : _status,
            StartedAt = _startedAt, EndedAt = final ? _lastAttemptAt : null, LastLootAt = _lastLootAt,
            FarmDay = _farmDay,
            Elapsed = elapsed, Character = _character, Location = _location,
            HarvestCount = _harvestCount, FailedHarvestCount = _failedHarvestCount, ItemCount = _itemCount, CoverageLimited = CoverageLimited || recovered && _status == LootTrackerSessionStatus.Live,
            FarmRouteVersion = SchemaVersion >= 3 ? 1 : 0,
            GatherVisits = _gatherVisits.ToImmutableArray(),
            FarmRoutePoints = _farmRoute,
            Loot = _loot.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToImmutableArray(),
            RouteSegments = _segments.Select(segment => new LootTrackerRouteSegment(segment.MapId, segment.MapName,
                segment.Points.Where(point => !final || point.Elapsed is { } at && at <= _lastAttemptElapsed).ToImmutableArray()))
                .Where(segment => !segment.Points.IsDefaultOrEmpty).ToImmutableArray(),
        };
    }
}
