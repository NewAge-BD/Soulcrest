using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Services;

internal sealed record FarmLootStoreRead(IReadOnlyList<LootTrackerSession> Sessions, IReadOnlyList<string> Issues)
{
    public bool CleanupPending { get; init; }
}

/// <summary>
/// Absolute, immutable farm-session states in an append-only journal. The last complete journal
/// record owns the totals; the small atomic checkpoint is never added to them during recovery.
/// Only local profile GUIDs and presentation values are persisted, never network identities.
/// </summary>
public sealed class FarmLootSessionStore
{
    internal const int MaximumRecordBytes = 1024 * 1024;
    internal const long MaximumSessionBytes = 128L * 1024 * 1024;
    internal const long MaximumSessionRecords = 1_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Action<string, string> _append;
    private readonly Action<string, object> _save;
    private readonly Action<string, string> _move;
    private readonly Action<string> _delete;
    private readonly Dictionary<string, Cursor> _cursors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedAppends = new(StringComparer.Ordinal);

    public FarmLootSessionStore(string root) : this(root, AppendDurably, SaveAtomically) { }

    internal FarmLootSessionStore(string root, Action<string, string> append, Action<string, object> save,
        Action<string, string>? move = null, Action<string>? delete = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(append); ArgumentNullException.ThrowIfNull(save);
        _root = Path.GetFullPath(root); _append = append; _save = save;
        _move = move ?? Directory.Move; _delete = delete ?? (path => Directory.Delete(path, recursive: true));
        ValidateLocalPath(_root);
    }

    internal void Append(LootTrackerSession snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        lock (_gate)
        {
            var directory = SessionDirectory(snapshot.Id);
            if (_deleted.Contains(snapshot.Id)) throw new InvalidOperationException("Session was deleted.");
            if (_failedAppends.Contains(snapshot.Id)) throw new IOException("Session journal needs recovery after an uncertain append.");
            var previous = GetCursor(snapshot.Id, directory);
            if (previous is not null) ValidateTransition(previous.Snapshot, snapshot);
            var sequence = (previous?.Sequence ?? 0) + 1;
            if (sequence > MaximumSessionRecords) throw new InvalidDataException("Session journal record limit reached.");
            var route = RouteChange(previous?.Snapshot, snapshot);
            var line = JsonSerializer.Serialize(new JournalEntry(2, sequence, snapshot with { RouteSegments = [] })
                { RouteDelta = route }, Options) + "\n";
            var bytes = Utf8.GetByteCount(line);
            if (bytes > MaximumRecordBytes) throw new InvalidDataException("Session journal record is too large.");
            var totalBytes = checked((previous?.Bytes ?? 0) + bytes);
            if (totalBytes > MaximumSessionBytes) throw new InvalidDataException("Session journal size limit reached.");
            Directory.CreateDirectory(directory); ValidateLocalPath(directory);
            var journal = Path.Combine(directory, "events-0001.jsonl"); ValidateLocalPath(journal);
            try { _append(journal, line); }
            catch { _failedAppends.Add(snapshot.Id); throw; }
            // The journal commit happened even if the following checkpoint write fails.
            _cursors[snapshot.Id] = new(sequence, totalBytes, snapshot);
            _save(Path.Combine(directory, "summary-v1.json"), Checkpoint.From(sequence, snapshot));
        }
    }

    internal FarmLootStoreRead Read()
    {
        lock (_gate)
        {
            ValidateLocalPath(_root);
            if (!Directory.Exists(_root)) return new([], []);
            var cleanupPending = CleanupDeleted();
            var sessions = new List<LootTrackerSession>(); var issues = new List<string>();
            foreach (var directory in Directory.EnumerateDirectories(_root).Order(StringComparer.Ordinal))
            {
                var id = Path.GetFileName(directory);
                if (!ValidId(id)) continue;
                Cursor? cursor = null; var damaged = false;
                try
                {
                    ValidateLocalPath(directory);
                    cursor = ReadJournal(id, directory, issues, out damaged);
                    if (cursor is not null) damaged |= CheckCheckpoint(id, directory, cursor.Sequence, issues);
                }
                catch (Exception error) when (IsStorageError(error))
                { damaged = true; issues.Add("unreadable-farm-session"); }
                if (cursor is not null)
                {
                    var session = Recover(cursor.Snapshot);
                    sessions.Add(session with { CoverageLimited = session.CoverageLimited || damaged });
                }
            }
            return new(sessions.OrderByDescending(session => session.StartedAt).ThenBy(session => session.Id, StringComparer.Ordinal).ToArray(),
                issues.Distinct(StringComparer.Ordinal).ToArray()) { CleanupPending = cleanupPending };
        }
    }

    /// <summary>Rename commits deletion; cleanup failures leave an ignored tombstone, never a visible session.</summary>
    internal bool Delete(string id)
    {
        lock (_gate)
        {
            var directory = SessionDirectory(id);
            if (!Directory.Exists(directory)) { _deleted.Add(id); _cursors.Remove(id); return false; }
            ValidateTree(directory);
            var tombstone = Path.Combine(_root, ".deleted-" + Guid.NewGuid().ToString("N"));
            ValidateLocalPath(tombstone);
            _move(directory, tombstone);
            _deleted.Add(id); _cursors.Remove(id); _failedAppends.Remove(id);
            try { ValidateTree(tombstone); _delete(tombstone); return false; }
            catch (Exception error) when (IsStorageError(error)) { return true; }
        }
    }

    private Cursor? GetCursor(string id, string directory)
    {
        if (_cursors.TryGetValue(id, out var cached)) return cached;
        if (!Directory.Exists(directory)) return null;
        var issues = new List<string>();
        var cursor = ReadJournal(id, directory, issues, out var damaged);
        if (damaged || cursor is null) throw new InvalidDataException("Existing session journal is incomplete or invalid.");
        _cursors[id] = cursor;
        return cursor;
    }

    private static Cursor? ReadJournal(string id, string directory, ICollection<string> issues, out bool damaged)
    {
        damaged = false;
        var path = Path.Combine(directory, "events-0001.jsonl"); ValidateLocalPath(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.SequentialScan);
        var buffer = new byte[8192]; var line = new ArrayBufferWriter<byte>();
        Cursor? result = null; long consumed = 0; int count;
        try
        {
            while ((count = stream.Read(buffer)) > 0)
            {
                var offset = 0;
                while (offset < count)
                {
                    var rest = buffer.AsSpan(offset, count - offset); var newline = rest.IndexOf((byte)'\n');
                    var length = newline < 0 ? rest.Length : newline;
                    if (line.WrittenCount + length + 1 > MaximumRecordBytes)
                    { issues.Add("farm-journal-record-size-limit"); damaged = true; return result; }
                    line.Write(rest[..length]); offset += length;
                    if (newline < 0) continue;
                    offset++; consumed += line.WrittenCount + 1L;
                    if (consumed > MaximumSessionBytes || (result?.Sequence ?? 0) >= MaximumSessionRecords)
                    { issues.Add("farm-journal-session-limit"); damaged = true; return result; }
                    using var json = JsonDocument.Parse(Utf8.GetString(line.WrittenSpan), new JsonDocumentOptions { MaxDepth = 32 });
                    RejectDuplicateProperties(json.RootElement);
                    var entry = json.RootElement.Deserialize<JournalEntry>(Options);
                    if (entry is null || entry.SchemaVersion is not (1 or 2) || entry.Sequence != (result?.Sequence ?? 0) + 1
                        || entry.Snapshot is null || entry.Snapshot.Id != id)
                        throw new InvalidDataException("Invalid journal record identity or sequence.");
                    var snapshot = entry.Snapshot;
                    if (entry.SchemaVersion == 1)
                    { if (entry.RouteDelta is not null) throw new InvalidDataException("Unexpected legacy route delta."); }
                    else
                    {
                        if (snapshot.RouteSegments.IsDefault || !snapshot.RouteSegments.IsEmpty || entry.RouteDelta is not { } delta)
                            throw new InvalidDataException("Missing or duplicated farm route data.");
                        snapshot = snapshot with { RouteSegments = ApplyRouteChange(result?.Snapshot, snapshot, delta) };
                    }
                    ValidateSnapshot(snapshot);
                    if (result is not null) ValidateTransition(result.Snapshot, snapshot);
                    result = new(entry.Sequence, consumed, snapshot); line.Clear();
                }
            }
            if (line.WrittenCount > 0) { issues.Add("incomplete-farm-journal-tail"); damaged = true; }
        }
        catch (Exception error) when (IsStorageError(error))
        { issues.Add("invalid-farm-journal-record"); damaged = true; }
        return result;
    }

    private static bool CheckCheckpoint(string id, string directory, long sequence, ICollection<string> issues)
    {
        var path = Path.Combine(directory, "summary-v1.json"); ValidateLocalPath(path);
        if (!File.Exists(path)) return false;
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Checkpoint is too large.");
            using var json = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(json.RootElement);
            var checkpoint = json.RootElement.Deserialize<Checkpoint>(Options);
            if (checkpoint is null || checkpoint.SchemaVersion != 1 || checkpoint.SessionId != id
                || checkpoint.ThroughSequence < 1 || checkpoint.ThroughSequence > sequence)
                throw new InvalidDataException("Invalid checkpoint.");
            return false;
        }
        catch (Exception error) when (IsStorageError(error)) { issues.Add("invalid-farm-session-checkpoint"); return true; }
    }

    private bool CleanupDeleted()
    {
        var pending = false;
        foreach (var directory in Directory.EnumerateDirectories(_root, ".deleted-*"))
        {
            if (!ValidId(Path.GetFileName(directory)[9..])) continue;
            try { ValidateTree(directory); _delete(directory); }
            catch (Exception error) when (IsStorageError(error)) { pending = true; }
        }
        return pending;
    }

    private static LootTrackerSession Recover(LootTrackerSession session)
    {
        if (session.Status != LootTrackerSessionStatus.Live) return session;
        var end = session.LastLootAt ?? session.StartedAt;
        var elapsed = session.Elapsed <= end - session.StartedAt ? session.Elapsed : end - session.StartedAt;
        var routes = TrimRoute(session, end, elapsed);
        return session with { Status = LootTrackerSessionStatus.Interrupted, EndedAt = end, Elapsed = elapsed, RouteSegments = routes };
    }

    internal static ImmutableArray<LootTrackerRouteSegment> TrimRoute(LootTrackerSession session, DateTimeOffset end, TimeSpan elapsed) =>
        session.RouteSegments.Select(segment => segment with
        { Points = segment.Points.Where(point => (point.At is null || point.At <= end)
            && (point.Elapsed is null || point.Elapsed <= elapsed)).ToImmutableArray() })
        .Where(segment => !segment.Points.IsEmpty).ToImmutableArray();

    private static RouteDelta RouteChange(LootTrackerSession? previous, LootTrackerSession next)
    {
        var before = previous?.RouteSegments ?? [];
        var after = next.RouteSegments;
        var append = before.Length <= after.Length;
        for (var i = 0; append && i < before.Length; i++)
        {
            var old = before[i]; var current = after[i];
            append = old.MapId == current.MapId && old.MapName == current.MapName
                && (i == before.Length - 1 ? old.Points.Length <= current.Points.Length : old.Points.Length == current.Points.Length)
                && old.Points.SequenceEqual(current.Points.Take(old.Points.Length));
        }
        if (append)
            return new(false, before.Length,
                before.IsEmpty ? [] : after[before.Length - 1].Points.Skip(before[^1].Points.Length).ToImmutableArray(),
                after.Skip(before.Length).ToImmutableArray());
        if (previous is null || next.Status == LootTrackerSessionStatus.Live || next.EndedAt is not { } end
            || !SameRoute(after, TrimRoute(previous, end, next.Elapsed)))
            throw new InvalidDataException("A recorded route cannot be rewritten.");
        return new(true, 0, [], after);
    }

    private static ImmutableArray<LootTrackerRouteSegment> ApplyRouteChange(LootTrackerSession? previous,
        LootTrackerSession next, RouteDelta delta)
    {
        if (delta.LastSegmentPoints.IsDefault || delta.Segments.IsDefault || delta.ExistingSegments < 0)
            throw new InvalidDataException("Invalid route delta.");
        var before = previous?.RouteSegments ?? [];
        if (delta.Replace)
        {
            if (delta.ExistingSegments != 0 || !delta.LastSegmentPoints.IsEmpty || previous is null
                || next.Status == LootTrackerSessionStatus.Live || next.EndedAt is not { } end
                || !SameRoute(delta.Segments, TrimRoute(previous, end, next.Elapsed)))
                throw new InvalidDataException("Invalid route truncation.");
            return delta.Segments;
        }
        if (delta.ExistingSegments != before.Length || before.IsEmpty && !delta.LastSegmentPoints.IsEmpty)
            throw new InvalidDataException("Route delta does not match its prefix.");
        var result = before.ToBuilder();
        if (!delta.LastSegmentPoints.IsEmpty)
            result[^1] = result[^1] with { Points = result[^1].Points.AddRange(delta.LastSegmentPoints) };
        result.AddRange(delta.Segments);
        return result.ToImmutable();
    }

    private static bool SameRoute(ImmutableArray<LootTrackerRouteSegment> left, ImmutableArray<LootTrackerRouteSegment> right) =>
        left.Length == right.Length && left.Zip(right).All(pair => pair.First is not null && pair.Second is not null
            && pair.First.MapId == pair.Second.MapId && pair.First.MapName == pair.Second.MapName
            && !pair.First.Points.IsDefault && !pair.Second.Points.IsDefault && pair.First.Points.SequenceEqual(pair.Second.Points));

    private static void ValidateSnapshot(LootTrackerSession session)
    {
        if (!ValidId(session.Id) || session.Kind is not (LootTrackerSessionKind.Grind or LootTrackerSessionKind.Dungeon)
            || !Enum.IsDefined(session.Status) || !Enum.IsDefined(session.Mode)
            || (session.Mode != LootTrackerSessionMode.Normal && session.Kind != LootTrackerSessionKind.Grind)
            || session.StartedAt == default || session.Elapsed < TimeSpan.Zero
            || (session.FarmDay is { } farmDay && farmDay != LootFarmDay.Get(session.StartedAt))
            || session.EndedAt < session.StartedAt || session.LastLootAt < session.StartedAt
            || (session.EndedAt is { } end && session.LastLootAt > end)
            || (session.Status == LootTrackerSessionStatus.Live ? session.EndedAt is not null : session.EndedAt is null))
            throw new InvalidDataException("Invalid farm session metadata.");
        if (session.Character is not { } character || !Guid.TryParse(character.ProfileId, out var profile) || profile == Guid.Empty
            || character.CombatScore < 0 || character.GearScore < 0 || !Text(character.Name, 256, optional: true)
            || !Text(session.Location, 1024, optional: true) || !Text(session.DungeonName, 1024, optional: true)
            || session.CombatScore < 0 || session.KinaBound < 0 || session.KinaUnbound < 0 || session.MonsterCount < 0
            || session.ItemCount < 0 || session.SoulCount < 0 || session.HarvestCount < 0 || session.FailedHarvestCount < 0
            || session.Loot.IsDefault || session.Mobs.IsDefault || session.RouteSegments.IsDefault || session.MinuteIncome.IsDefault
            || session.ResourceNodes.IsDefault || session.GatherVisits.IsDefault || session.FarmRoutePoints.IsDefault
            || !session.ResourceNodes.IsEmpty || !session.GatherVisits.IsEmpty || !session.FarmRoutePoints.IsEmpty || session.FarmRouteVersion != 0)
            throw new InvalidDataException("Invalid farm session values or local profile.");
        foreach (var item in session.Loot)
            if (item is null || !Text(item.Id, 256) || !Text(item.Name, 1024) || !Text(item.MobId, 256, true)
                || !Enum.IsDefined(item.Kind) || item.Quantity < 0)
                throw new InvalidDataException("Invalid session loot.");
        foreach (var mob in session.Mobs)
            if (mob is null || !Text(mob.Id, 256) || !Text(mob.Name, 1024) || mob.Count < 0) throw new InvalidDataException("Invalid session monster.");
        foreach (var segment in session.RouteSegments)
        {
            if (segment is null || !Text(segment.MapId, 256) || !Text(segment.MapName, 1024, true) || segment.Points.IsDefault)
                throw new InvalidDataException("Invalid session route.");
            foreach (var point in segment.Points)
                if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0
                    || point.At < session.StartedAt || point.Elapsed < TimeSpan.Zero
                    || session.Status != LootTrackerSessionStatus.Live && (point.At > session.EndedAt || point.Elapsed > session.Elapsed))
                    throw new InvalidDataException("Invalid route point.");
        }
        foreach (var bin in session.MinuteIncome)
            if (bin is null || bin.Minute < 0 || bin.KinaBound < 0 || bin.KinaUnbound < 0) throw new InvalidDataException("Invalid income bin.");
    }

    private static void ValidateTransition(LootTrackerSession previous, LootTrackerSession next)
    {
        if (previous.Status != LootTrackerSessionStatus.Live || previous.Kind != next.Kind || previous.StartedAt != next.StartedAt
            || previous.Character != next.Character || previous.Mode != next.Mode || previous.FarmDay != next.FarmDay
            || next.Elapsed < previous.Elapsed
            || (previous.LastLootAt is { } last && (next.LastLootAt is null || next.LastLootAt < last)))
            throw new InvalidDataException("Farm session history cannot be rewritten or resumed after completion.");
    }

    private string SessionDirectory(string id)
    {
        if (!ValidId(id)) throw new InvalidDataException("Invalid local session GUID.");
        var path = Path.GetFullPath(Path.Combine(_root, id));
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Session escaped its storage root.");
        ValidateLocalPath(path); return path;
    }

    private static bool ValidId(string value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;
    private static bool Text(string? value, int maximum, bool optional = false) =>
        value is null ? optional : value.Length <= maximum && (optional || !string.IsNullOrWhiteSpace(value)) && !value.Any(char.IsControl);
    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException
        or DecoderFallbackException or InvalidDataException or ArgumentException or OverflowException;

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property."); RejectDuplicateProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void ValidateLocalPath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path[root.Length..].Contains(':') || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException("Farm sessions must use local regular paths.");
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not valid farm session storage.");
        }
    }

    private static void ValidateTree(string directory)
    {
        ValidateLocalPath(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        { ValidateLocalPath(path); if (Directory.Exists(path)) ValidateTree(path); }
    }

    private static void AppendDurably(string path, string line)
    {
        ValidateLocalPath(path);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Utf8.GetBytes(line)); stream.Flush(flushToDisk: true);
    }

    private static void SaveAtomically(string path, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        JsonFile.Retry(() =>
        {
            ValidateLocalPath(path);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ValidateLocalPath(temp);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                ValidateLocalPath(path); ValidateLocalPath(temp); File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { ValidateLocalPath(temp); if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception error) when (IsStorageError(error)) { }
            }
        });
    }

    private sealed record JournalEntry(int SchemaVersion, long Sequence, LootTrackerSession Snapshot)
    {
        public RouteDelta? RouteDelta { get; init; }
    }
    private sealed record RouteDelta(bool Replace, int ExistingSegments,
        ImmutableArray<LootTrackerRoutePoint> LastSegmentPoints, ImmutableArray<LootTrackerRouteSegment> Segments);
    private sealed record Cursor(long Sequence, long Bytes, LootTrackerSession Snapshot);
    private sealed record Checkpoint(int SchemaVersion, string SessionId, long ThroughSequence,
        LootTrackerSessionStatus Status, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, DateTimeOffset? LastLootAt,
        TimeSpan Elapsed, LootTrackerCharacterSnapshot? Character, string Location, long? KinaBound, long? KinaUnbound,
        long? MonsterCount, long? ItemCount, long? SoulCount, bool CoverageLimited)
    {
        public LootTrackerSessionMode Mode { get; init; }
        public DateOnly? FarmDay { get; init; }
        public bool LocationApproximate { get; init; }
        public static Checkpoint From(long sequence, LootTrackerSession session) => new(1, session.Id, sequence,
            session.Status, session.StartedAt, session.EndedAt, session.LastLootAt, session.Elapsed, session.Character,
            session.Location, session.KinaBound, session.KinaUnbound, session.MonsterCount, session.ItemCount,
            session.SoulCount, session.CoverageLimited) { Mode = session.Mode, FarmDay = session.FarmDay,
                LocationApproximate = session.LocationApproximate };
    }
}
