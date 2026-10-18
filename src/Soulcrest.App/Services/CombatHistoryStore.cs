using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Services;

/// <summary>
/// Local-only primary storage: run metadata, append-only typed event journals and a rebuildable index.
/// The caller supplies a private data directory; tests always supply a fresh temporary directory.
/// No settings, player database, network addresses, raw packets or upload copies are created here.
/// </summary>
public sealed class CombatHistoryStore
{
    public const int DefaultRetainedRuns = 500;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 64
    };
    private static readonly JsonSerializerOptions DetailOptions = new(JsonOptions) { WriteIndented = true };
    private readonly object _gate = new();

    public CombatHistoryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        DirectoryPath = Path.GetFullPath(directory);
    }

    public string DirectoryPath { get; }
    private string RunsDirectory => Path.Combine(DirectoryPath, "runs");
    private string IndexPath => Path.Combine(DirectoryPath, "index.json");

    /// <summary>Events are appended first; metadata contains no second, divergent copy of the timeline.</summary>
    public void SaveRun(CombatRunDocument run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ValidateRun(run);
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            EnsureExistingIndexSupported();
            var runPath = RunPath(run.Id);
            if (!File.Exists(runPath))
            {
                SaveNewRun(run);
                RebuildIndexCore();
                return;
            }
            var old = ReadRunMetadata(run.Id);
            ValidateCheckpoint(old, run);
            if (old.Attempts.Any(a => !run.Attempts.Any(next => next.Id == a.Id)))
                throw new InvalidOperationException("Existing attempts cannot be removed by SaveRun; delete the whole run explicitly.");
            // Favorite is edited independently through SetFavorite; an older live checkpoint cannot undo it.
            run = run with { Favorite = old.Favorite };

            // Validate every append before mutating any journal.
            foreach (var attempt in run.Attempts)
            {
                var journal = ReadJournalCore(JournalPath(run.Id, attempt.Id));
                ValidateAttemptJournal(attempt, journal);
                var added = ValidateAppend(journal, attempt.Events);
                var existingAttempt = old.Attempts.FirstOrDefault(a => a.Id == attempt.Id);
                if (existingAttempt?.EndReason is not null && added.Length != 0)
                    throw new InvalidOperationException("A sealed attempt cannot receive new events.");
            }
            foreach (var attempt in run.Attempts)
                AppendCore(JournalPath(run.Id, attempt.Id), attempt.Events);

            WriteAtomic(runPath, run with
            {
                Attempts = run.Attempts.Select(a => a with { Events = [] }).ToArray()
            });
            RebuildIndexCore();
        }
    }

    public CombatHistoryRunLoadResult LoadRun(Guid runId)
    {
        ValidateId(runId);
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            return ReadRunCore(runId);
        }
    }

    /// <summary>A local browser snapshot. Only the shared writer lock is touched; primary records stay unchanged.</summary>
    public CombatHistoryRunLoadResult LoadRunReadOnly(Guid runId)
    {
        ValidateId(runId);
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            return ReadRunCore(runId);
        }
    }

    /// <summary>Validates the existing index but derives the list from primary metadata without writing a repair.</summary>
    public CombatHistoryReadOnlyOverview LoadOverviewReadOnly()
    {
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            EnsureSafePath(DirectoryPath);
            EnsureExistingIndexSupported();
            var staging = Path.Combine(DirectoryPath, "staging");
            EnsureSafePath(staging);
            var unpublished = 0;
            if (Directory.Exists(staging))
                foreach (var directory in Directory.EnumerateDirectories(staging))
                {
                    EnsureSafePath(directory);
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                        throw new CombatHistoryFormatException(directory, "Invalid staged run identity.");
                    unpublished++;
                }
            return new(BuildIndex(), unpublished);
        }
    }

    private CombatHistoryRunLoadResult ReadRunCore(Guid runId)
    {
        var run = ReadRunMetadata(runId);
        List<Guid> incomplete = [];
        var attempts = run.Attempts.Select(attempt =>
        {
            var journal = ReadJournalCore(JournalPath(runId, attempt.Id));
            ValidateAttemptJournal(attempt, journal);
            if (journal.HasIncompleteTail) incomplete.Add(attempt.Id);
            return attempt with
            {
                Events = journal.Events.ToArray(),
                HasIncompleteJournal = attempt.HasIncompleteJournal || journal.HasIncompleteTail
            };
        }).ToArray();
        return new(run with { Attempts = attempts }, incomplete);
    }

    public CombatJournalLoadResult LoadJournal(Guid runId, Guid attemptId)
    {
        ValidateId(runId);
        ValidateId(attemptId);
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            var attempt = RequireAttempt(ReadRunMetadata(runId), attemptId);
            var journal = ReadJournalCore(JournalPath(runId, attemptId));
            ValidateAttemptJournal(attempt, journal);
            return journal;
        }
    }

    /// <summary>Retransmitted sequence + identical typed content is idempotent; a different sequence is a real event.</summary>
    public void AppendEvents(Guid runId, Guid attemptId, IEnumerable<CombatEventEnvelope> events)
    {
        ValidateId(runId);
        ValidateId(attemptId);
        ArgumentNullException.ThrowIfNull(events);
        var batch = events.ToArray();
        foreach (var item in batch) ValidateEvent(item);
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            EnsureExistingIndexSupported();
            var attempt = RequireAttempt(ReadRunMetadata(runId), attemptId);
            ValidateAttemptJournal(attempt, new(batch, false));
            if (attempt.EndReason is not null && ValidateAppend(ReadJournalCore(JournalPath(runId, attemptId)), batch).Length != 0)
                throw new InvalidOperationException("A sealed attempt cannot receive new events.");
            AppendCore(JournalPath(runId, attemptId), batch);
        }
    }

    /// <summary>Only an absent index is rebuilt automatically. Corruption or unsupported versions are explicit errors.</summary>
    public CombatHistoryIndex LoadIndex()
    {
        lock (_gate)
        {
            if (!File.Exists(IndexPath))
            {
                using var writerLock = AcquireWriterLock();
                return RebuildIndexCore();
            }
            return ReadIndex();
        }
    }

    /// <summary>Explicit recovery of a derived index. Invalid primary metadata leaves the previous index untouched.</summary>
    public CombatHistoryIndex RebuildIndex()
    {
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            // Do not destroy a future index using an older application, even during explicit recovery.
            if (File.Exists(IndexPath)) EnsureIndexNotFromFuture();
            return RebuildIndexCore();
        }
    }

    public void SetFavorite(Guid runId, bool favorite)
    {
        ValidateId(runId);
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            EnsureExistingIndexSupported();
            var run = ReadRunMetadata(runId);
            WriteAtomic(RunPath(runId), run with { Favorite = favorite });
            RebuildIndexCore();
        }
    }

    /// <summary>Unpublished first-save directories are discoverable, never silently discarded or included in the index.</summary>
    public IReadOnlyList<Guid> GetStagedRunIds()
    {
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            var staging = Path.Combine(DirectoryPath, "staging");
            EnsureSafePath(staging);
            if (!Directory.Exists(staging)) return [];
            return Directory.EnumerateDirectories(staging).Select(directory =>
            {
                EnsureSafePath(directory);
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id))
                    throw new CombatHistoryFormatException(directory, "Invalid staged run identity.");
                return id;
            }).Order().ToArray();
        }
    }

    /// <summary>Publishes a fully written interrupted first save after validating its metadata and every journal.</summary>
    public bool RecoverStagedRun(Guid runId)
    {
        ValidateId(runId);
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            EnsureExistingIndexSupported();
            var staging = StagedRunDirectory(runId);
            if (!Directory.Exists(staging)) return false;
            if (Directory.Exists(RunDirectory(runId))) throw new InvalidOperationException("The run is already published.");
            var run = ReadRunMetadata(runId, staging);
            foreach (var attempt in run.Attempts)
            {
                var journal = ReadJournalCore(Path.Combine(staging, "journals", attempt.Id.ToString("N") + ".jsonl"));
                ValidateAttemptJournal(attempt, journal);
                if (journal.HasIncompleteTail) throw new CombatHistoryFormatException(staging, "Staged journal is incomplete.");
            }
            PublishStagedRun(runId);
            RebuildIndexCore();
            return true;
        }
    }

    /// <summary>Removes the owned run directory, including journals and any derived projections. No separate character ledger exists.</summary>
    public bool DeleteRun(Guid runId)
    {
        ValidateId(runId);
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            EnsureExistingIndexSupported();
            var directory = RunDirectory(runId);
            var staging = StagedRunDirectory(runId);
            var targets = new[] { directory, staging }.Where(Directory.Exists).ToArray();
            if (targets.Length == 0) return false;
            foreach (var target in targets) EnsureNoLinks(target);
            foreach (var target in targets) Directory.Delete(target, recursive: true);
            RebuildIndexCore();
            return true;
        }
    }

    /// <summary>Oldest completed nonfavorites go first. Favorites and active runs can keep the total above the limit.</summary>
    public IReadOnlyList<Guid> Prune(int retainedRuns = DefaultRetainedRuns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retainedRuns);
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            EnsureExistingIndexSupported();
            var index = BuildIndex();
            var removeCount = Math.Max(0, index.Runs.Length - retainedRuns);
            var removed = index.Runs.Where(r => !r.Favorite && r.EndedAtUtc is not null)
                .OrderBy(r => r.StartedAtUtc).ThenBy(r => r.Id).Take(removeCount).Select(r => r.Id).ToArray();
            // Check every target before any recursive deletion.
            foreach (var id in removed) EnsureNoLinks(RunDirectory(id));
            foreach (var id in removed) Directory.Delete(RunDirectory(id), recursive: true);
            WriteAtomic(IndexPath, BuildIndex());
            return removed;
        }
    }

    private FileStream AcquireWriterLock()
    {
        EnsureSafePath(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        // A second process/instance cannot interleave journal validation, append and metadata replacement.
        return new FileStream(Path.Combine(DirectoryPath, "writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private void SaveNewRun(CombatRunDocument run)
    {
        var staging = StagedRunDirectory(run.Id);
        if (Directory.Exists(staging) || Directory.Exists(RunDirectory(run.Id)))
            throw new InvalidOperationException("An unpublished run already exists; recover or delete it explicitly.");
        Directory.CreateDirectory(staging);
        foreach (var attempt in run.Attempts)
            AppendCore(Path.Combine(staging, "journals", attempt.Id.ToString("N") + ".jsonl"), attempt.Events);
        WriteAtomic(Path.Combine(staging, "run.json"), run with
        {
            Attempts = run.Attempts.Select(a => a with { Events = [] }).ToArray()
        });
        PublishStagedRun(run.Id);
    }

    private void PublishStagedRun(Guid runId)
    {
        var staging = StagedRunDirectory(runId);
        EnsureNoLinks(staging);
        EnsureSafePath(RunDirectory(runId));
        Directory.CreateDirectory(RunsDirectory);
        // Both paths are under the injected data root on the same volume: readers see the full first save or no run.
        Directory.Move(staging, RunDirectory(runId));
    }

    private CombatHistoryIndex RebuildIndexCore()
    {
        var index = BuildIndex();
        WriteAtomic(IndexPath, index);
        return index;
    }

    private CombatHistoryIndex BuildIndex()
    {
        if (!Directory.Exists(RunsDirectory)) return new();
        EnsureSafePath(RunsDirectory);
        List<CombatHistoryIndexEntry> entries = [];
        foreach (var directory in Directory.EnumerateDirectories(RunsDirectory).Order(StringComparer.Ordinal))
        {
            EnsureSafePath(directory);
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id))
                throw new CombatHistoryFormatException(directory, "Invalid run directory name.");
            var run = ReadRunMetadata(id);
            entries.Add(new(run.Id, run.DungeonKey, run.StartedAtUtc, run.EndedAtUtc, run.Favorite, run.Attempts.Length));
        }
        return new() { Runs = entries.OrderByDescending(r => r.StartedAtUtc).ThenBy(r => r.Id).ToArray() };
    }

    private CombatHistoryIndex ReadIndex()
    {
        var index = ReadJson<CombatHistoryIndex>(IndexPath);
        if (index.Runs is null || index.Runs.Any(r => r is null || r.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(r.DungeonKey) || r.AttemptCount < 0) ||
            index.Runs.Select(r => r.Id).Distinct().Count() != index.Runs.Length)
            throw new CombatHistoryFormatException(IndexPath, "Invalid or duplicate index entries.");
        return index;
    }

    private void EnsureExistingIndexSupported()
    {
        if (File.Exists(IndexPath)) _ = ReadIndex();
    }

    private void EnsureIndexNotFromFuture()
    {
        EnsureSafePath(IndexPath);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(IndexPath));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("SchemaVersion", out var schema) &&
                schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version != CombatHistoryIndex.CurrentSchemaVersion)
                throw new CombatHistoryFormatException(IndexPath, "Unsupported index schema version.");
        }
        catch (JsonException) { /* Explicit rebuild can recover an unreadable derived index. */ }
    }

    private CombatRunDocument ReadRunMetadata(Guid id, string? directory = null)
    {
        var runDirectory = directory ?? RunDirectory(id);
        var path = Path.Combine(runDirectory, "run.json");
        var run = ReadJson<CombatRunDocument>(path);
        try
        {
            ValidateRun(run);
            if (run.Id != id || run.Attempts.Any(a => a.Events.Length > 0))
                throw new ArgumentException("Run identity mismatch or events outside the primary journal.");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { throw new CombatHistoryFormatException(path, e.Message, e); }
        foreach (var attempt in run.Attempts)
            if (!File.Exists(Path.Combine(runDirectory, "journals", attempt.Id.ToString("N") + ".jsonl")))
                throw new CombatHistoryFormatException(path, "A referenced event journal is missing.");
        return run;
    }

    private static T ReadJson<T>(string path)
    {
        EnsureSafePath(path);
        var bytes = File.ReadAllBytes(path);
        RequireSchema(bytes, path, 1);
        RequireDocumentFields<T>(bytes, path);
        try { return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new JsonException("Null document."); }
        catch (JsonException e) { throw new CombatHistoryFormatException(path, "Invalid JSON or fields.", e); }
    }

    private static void RequireDocumentFields<T>(ReadOnlyMemory<byte> bytes, string path)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        static void Require(JsonElement element, string file, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object || names.Any(n => !element.TryGetProperty(n, out _)))
                throw new CombatHistoryFormatException(file, "Required persisted fields are missing.");
        }
        if (typeof(T) == typeof(CombatHistoryIndex)) Require(root, path, "Runs");
        if (typeof(T) != typeof(CombatRunDocument)) return;
        Require(root, path, "Id", "DungeonKey", "StartedAtUtc", "EndedAtUtc", "Favorite", "Attempts");
        var attempts = root.GetProperty("Attempts");
        if (attempts.ValueKind != JsonValueKind.Array)
            throw new CombatHistoryFormatException(path, "Attempts must be an array.");
        foreach (var attempt in attempts.EnumerateArray())
        {
            Require(attempt, path, "Id", "BossKey", "BossTargets", "StartedAtUtc", "DurationTicks", "EndReason",
                "Kind", "Capabilities", "MeasurementProfile", "HasIncompleteJournal", "Participants", "Events");
            Require(attempt.GetProperty("MeasurementProfile"), path, "RulesetVersion");
        }
    }

    private static void RequireSchema(ReadOnlyMemory<byte> bytes, string path, int version)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("SchemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var actual) || actual != version)
                throw new CombatHistoryFormatException(path, $"Unsupported or missing schema version; expected {version}.");
        }
        catch (JsonException e) { throw new CombatHistoryFormatException(path, "Invalid JSON.", e); }
    }

    private static CombatJournalLoadResult ReadJournalCore(string path)
    {
        EnsureSafePath(path);
        if (!File.Exists(path)) return new([], false);
        var bytes = File.ReadAllBytes(path);
        List<CombatEventEnvelope> events = [];
        long previous = 0;
        long previousOffset = 0;
        var offset = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            var line = bytes.AsMemory(offset, i - offset);
            RequireSchema(line, path, 1);
            CombatEventEnvelope item;
            try
            {
                item = JsonSerializer.Deserialize<CombatEventEnvelope>(line.Span, JsonOptions)
                    ?? throw new JsonException("Null event.");
                ValidateEvent(item);
                if (item.Sequence <= previous) throw new ArgumentException("Journal sequences are not strictly increasing.");
                if (item.OffsetTicks < previousOffset) throw new ArgumentException("Journal offsets are not monotonic.");
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            { throw new CombatHistoryFormatException(path, $"Invalid journal record at byte {offset}.", e); }
            events.Add(item);
            previous = item.Sequence;
            previousOffset = item.OffsetTicks;
            offset = i + 1;
        }
        return new(events, offset < bytes.Length);
    }

    private static CombatEventEnvelope[] ValidateAppend(CombatJournalLoadResult existing,
        IReadOnlyList<CombatEventEnvelope> batch)
    {
        if (existing.HasIncompleteTail)
            throw new InvalidOperationException("An incomplete journal tail requires explicit recovery before appending.");
        var known = existing.Events.ToDictionary(e => e.Sequence);
        var lastSequence = existing.Events.LastOrDefault()?.Sequence ?? 0;
        var lastOffset = existing.Events.LastOrDefault()?.OffsetTicks ?? 0;
        List<CombatEventEnvelope> added = [];
        foreach (var item in batch)
        {
            ValidateEvent(item);
            if (known.TryGetValue(item.Sequence, out var previous))
            {
                if (JsonSerializer.Serialize(previous, JsonOptions) != JsonSerializer.Serialize(item, JsonOptions))
                    throw new InvalidOperationException($"Sequence {item.Sequence} has conflicting content.");
                continue;
            }
            if (item.Sequence <= lastSequence)
                throw new InvalidOperationException("New journal sequences must be strictly increasing.");
            if (item.OffsetTicks < lastOffset)
                throw new InvalidOperationException("Journal offsets must be monotonic; UTC capture times may regress.");
            known.Add(item.Sequence, item);
            lastSequence = item.Sequence;
            lastOffset = item.OffsetTicks;
            added.Add(item);
        }
        return added.ToArray();
    }

    private static void AppendCore(string path, IReadOnlyList<CombatEventEnvelope> batch)
    {
        var added = ValidateAppend(ReadJournalCore(path), batch);
        if (added.Length == 0 && File.Exists(path)) return;
        var bytes = Utf8.GetBytes(string.Concat(added.Select(e => JsonSerializer.Serialize(e, JsonOptions) + "\n")));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Never retry an append: a failed write/flush may already have persisted a prefix or partial line.
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        EnsureSafePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, DetailOptions);
        JsonFile.Retry(() =>
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        });
    }

    private string RunDirectory(Guid id) => Path.Combine(RunsDirectory, id.ToString("N"));
    private string StagedRunDirectory(Guid id) => Path.Combine(DirectoryPath, "staging", id.ToString("N"));
    private string RunPath(Guid id) => Path.Combine(RunDirectory(id), "run.json");
    private string JournalPath(Guid runId, Guid attemptId) => Path.Combine(RunDirectory(runId), "journals", attemptId.ToString("N") + ".jsonl");
    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A nonempty identity is required.", nameof(id));
    }

    private static CombatAttemptDocument RequireAttempt(CombatRunDocument run, Guid attemptId)
    {
        return run.Attempts.FirstOrDefault(a => a.Id == attemptId)
            ?? throw new ArgumentException("Attempt does not belong to this run.", nameof(attemptId));
    }

    private static void ValidateAttemptJournal(CombatAttemptDocument attempt, CombatJournalLoadResult journal)
    {
        if (attempt.DurationTicks is { } duration && journal.Events.Any(e => e.OffsetTicks > duration))
            throw new CombatHistoryFormatException(attempt.Id.ToString("N"), "An event lies outside its attempt.");
    }

    private static void ValidateCheckpoint(CombatRunDocument old, CombatRunDocument next)
    {
        if (old.DungeonKey != next.DungeonKey || old.InstanceKey != next.InstanceKey || old.StartedAtUtc != next.StartedAtUtc ||
            old.EndedAtUtc is not null && old.EndedAtUtc != next.EndedAtUtc)
            throw new InvalidOperationException("Historical run identity and confirmed end cannot be rewritten.");
        if (old.EndedAtUtc is not null && next.Attempts.Any(after => !old.Attempts.Any(before => before.Id == after.Id)))
            throw new InvalidOperationException("A closed run cannot receive new attempts.");
        foreach (var before in old.Attempts)
        {
            var after = next.Attempts.FirstOrDefault(a => a.Id == before.Id)
                ?? throw new InvalidOperationException("Historical attempts cannot be removed.");
            if (before.BossKey != after.BossKey || before.Kind != after.Kind || before.StartedAtUtc != after.StartedAtUtc ||
                before.EncounterVariantKey != after.EncounterVariantKey || before.MeasurementProfile != after.MeasurementProfile ||
                before.Capabilities != after.Capabilities ||
                !before.Participants.SequenceEqual(after.Participants) ||
                before.BossTargets.Any(target => !after.BossTargets.Contains(target)) ||
                before.EndReason is not null && after.BossTargets.Any(target => !before.BossTargets.Contains(target)) ||
                before.EndReason is not null && (before.EndReason != after.EndReason || before.DurationTicks != after.DurationTicks) ||
                before.HasIncompleteJournal && !after.HasIncompleteJournal)
                throw new InvalidOperationException("Historical attempt identity, roster, targets or confirmed outcome cannot be rewritten.");
        }
    }

    private static void ValidateRun(CombatRunDocument run)
    {
        if (run.SchemaVersion != CombatRunDocument.CurrentSchemaVersion)
            throw new ArgumentException("Unsupported run schema version.", nameof(run));
        ValidateId(run.Id);
        if (string.IsNullOrWhiteSpace(run.DungeonKey) || run.StartedAtUtc == default || run.StartedAtUtc.Offset != TimeSpan.Zero ||
            run.EndedAtUtc is { } end && (end < run.StartedAtUtc || end.Offset != TimeSpan.Zero) || run.Attempts is null)
            throw new ArgumentException("Invalid run metadata.", nameof(run));
        HashSet<Guid> ids = [];
        foreach (var attempt in run.Attempts)
        {
            if (attempt is null || attempt.Id == Guid.Empty || !ids.Add(attempt.Id) || string.IsNullOrWhiteSpace(attempt.BossKey) ||
                attempt.StartedAtUtc < run.StartedAtUtc || attempt.StartedAtUtc.Offset != TimeSpan.Zero ||
                attempt.DurationTicks is < 0 || (attempt.EndReason is not null) != (attempt.DurationTicks is not null) ||
                !Enum.IsDefined(attempt.Kind) || attempt.MeasurementProfile is null ||
                string.IsNullOrWhiteSpace(attempt.MeasurementProfile.RulesetVersion) ||
                attempt.EndReason is { } reason && !Enum.IsDefined(reason) ||
                (attempt.Capabilities & ~(CombatCapabilities.Damage | CombatCapabilities.SkillCasts | CombatCapabilities.Healing |
                    CombatCapabilities.Deaths | CombatCapabilities.Buffs)) != 0 ||
                attempt.BossTargets is null || attempt.Participants is null || attempt.Participants.Length > 5 || attempt.Events is null)
                throw new ArgumentException("Invalid or duplicate attempt metadata.", nameof(run));
            foreach (var target in attempt.BossTargets) ValidateEntity(target);
            if (run.EndedAtUtc is { } runEnd && (attempt.StartedAtUtc > runEnd ||
                attempt.DurationTicks is { } duration && duration > (runEnd - attempt.StartedAtUtc).Ticks))
                throw new ArgumentException("Attempt lies outside its run.", nameof(run));
            foreach (var participant in attempt.Participants) ValidateParticipant(participant);
            if (attempt.Participants.Select(p => p.ParticipantId).Distinct(StringComparer.Ordinal).Count() != attempt.Participants.Length)
                throw new ArgumentException("Duplicate participant identities.", nameof(run));
            foreach (var item in attempt.Events)
            {
                ValidateEvent(item);
                if (attempt.DurationTicks is { } eventDuration && item.OffsetTicks > eventDuration)
                    throw new ArgumentException("Event lies outside its attempt.", nameof(run));
            }
            _ = ValidateAppend(new([], false), attempt.Events);
        }
        if (run.EndedAtUtc is not null && run.Attempts.Any(a => a.EndReason is null))
            throw new ArgumentException("A completed run cannot contain an active attempt.", nameof(run));
    }

    private static void ValidateEvent(CombatEventEnvelope item)
    {
        if (item is null || item.SchemaVersion != 1 || item.Sequence <= 0 || item.OffsetTicks < 0 || item.Source is null ||
            string.IsNullOrWhiteSpace(item.Source.StreamId) || item.Source.Generation < 0 || item.CapturedAtUtc == default ||
            item.CapturedAtUtc.Offset != TimeSpan.Zero || item.Event is null)
            throw new ArgumentException("Invalid event envelope.");
        switch (item.Event)
        {
            case CombatRosterEvent roster:
                if (roster.Members is null || roster.Members.Length > 5) throw new ArgumentException("Invalid roster.");
                foreach (var participant in roster.Members) ValidateParticipant(participant);
                if (roster.Members.Select(p => p.ParticipantId).Distinct(StringComparer.Ordinal).Count() != roster.Members.Length)
                    throw new ArgumentException("Duplicate roster identities.");
                break;
            case CombatDamageEvent damage:
                ValidateEntity(damage.Target);
                ValidateScope(item, damage.Target);
                RequireText(damage.SkillId);
                if (damage.Amount < 0) throw new ArgumentException("Negative damage.");
                break;
            case CombatCastEvent cast:
                RequireText(cast.SourceParticipantId); RequireText(cast.SkillId); break;
            case CombatHealEvent heal:
                RequireText(heal.SourceParticipantId); RequireText(heal.TargetParticipantId); RequireText(heal.SkillId);
                if (heal.Amount < 0) throw new ArgumentException("Negative healing.");
                break;
            case CombatBuffEvent buff:
                RequireText(buff.BuffId);
                if (!Enum.IsDefined(buff.Action) || (buff.TargetParticipantId is null) == (buff.TargetEntity is null) ||
                    buff.StackCount is < 0 || buff.Level is < 0) throw new ArgumentException("Invalid buff state.");
                if (buff.TargetEntity is not null) ValidateEntity(buff.TargetEntity);
                if (buff.TargetEntity is not null) ValidateScope(item, buff.TargetEntity);
                if (buff.TargetParticipantId is not null) RequireText(buff.TargetParticipantId);
                break;
            case CombatHpEvent hp:
                ValidateEntity(hp.Target);
                ValidateScope(item, hp.Target);
                if (hp.Current < 0 || hp.Maximum is <= 0 || hp.Maximum is { } maximum && hp.Current > maximum)
                    throw new ArgumentException("Invalid boss HP.");
                break;
            case CombatUnclassifiedAmountEvent unknown:
                RequireText(unknown.SourceParticipantId); RequireText(unknown.EffectId);
                if ((unknown.TargetParticipantId is null) == (unknown.TargetEntity is null) || unknown.Amount is < 0)
                    throw new ArgumentException("Invalid unclassified amount.");
                if (unknown.TargetParticipantId is not null) RequireText(unknown.TargetParticipantId);
                if (unknown.TargetEntity is not null) { ValidateEntity(unknown.TargetEntity); ValidateScope(item, unknown.TargetEntity); }
                if (unknown.SkillId is not null) RequireText(unknown.SkillId);
                break;
            case CombatDeathEvent death: RequireText(death.ParticipantId); break;
            case CombatGapEvent gap:
                if (!Enum.IsDefined(gap.Reason)) throw new ArgumentException("Invalid gap reason.");
                break;
            default: throw new ArgumentException("Unknown event kind.");
        }
    }

    private static void ValidateEntity(CombatEntityReference entity)
    {
        if (entity is null || entity.Generation < 0) throw new ArgumentException("Invalid entity.");
        RequireText(entity.EntityId);
        RequireText(entity.StreamId);
    }

    private static void ValidateScope(CombatEventEnvelope item, CombatEntityReference entity)
    {
        if (item.Source.StreamId != entity.StreamId || item.Source.Generation != entity.Generation)
            throw new ArgumentException("An entity cannot cross source stream/generation boundaries.");
    }

    private static void ValidateParticipant(CombatParticipant participant)
    {
        if (participant is null || participant.CombatPower is < 0) throw new ArgumentException("Invalid participant.");
        RequireText(participant.ParticipantId);
    }

    private static void RequireText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A nonempty identifier is required.");
    }

    private static void EnsureSafePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Combat storage paths cannot traverse symbolic links or junctions.");
    }

    private static void EnsureNoLinks(string directory)
    {
        EnsureSafePath(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureSafePath(entry);
            if (Directory.Exists(entry)) EnsureNoLinks(entry);
        }
    }
}
