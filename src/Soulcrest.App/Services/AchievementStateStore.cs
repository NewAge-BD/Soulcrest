using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>Private, per-assignment last state and confirmed milestones. Never writes from a network lock.</summary>
public sealed class AchievementStateStore
{
    internal const int MaximumRecordBytes = 8 * 1024 * 1024;
    internal const long MaximumJournalBytes = 256L * 1024 * 1024;
    internal const int MaximumMilestones = 200_000;
    private const int SchemaVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Action<string, string> _append;
    private readonly Action<string, object> _save;
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);

    public AchievementStateStore(string rootPath) : this(rootPath, AppendDurably, SaveAtomically) { }

    internal AchievementStateStore(string rootPath, Action<string, string> append, Action<string, object> save)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _append = append;
        _save = save;
        ValidateLocalPath(_root);
    }

    public AchievementStoreRead Load(AchievementProfileBinding binding)
    {
        ValidateBinding(binding);
        lock (_gate) return Read(binding).Result;
    }

    /// <summary>Durably append new facts before replacing latest. Counter-only updates do not grow the journal.</summary>
    public AchievementSavedSnapshot Save(AchievementSavedSnapshot snapshot, IEnumerable<AchievementMilestone> newMilestones)
    {
        ValidateSnapshot(snapshot);
        ArgumentNullException.ThrowIfNull(newMilestones);
        var candidates = newMilestones.Take(MaximumMilestones + 1).ToArray();
        if (candidates.Length > MaximumMilestones) throw new InvalidDataException("Too many achievement milestones.");
        foreach (var candidate in candidates) ValidateMilestone(candidate);
        lock (_gate)
        {
            if (IsDeleted(snapshot.Binding.ProfileId)) throw new InvalidOperationException("Achievement profile was deleted.");
            var read = Read(snapshot.Binding);
            if (read.UnsupportedSchema) throw new InvalidDataException("Unsupported achievement history schema; existing data was preserved.");
            if (read.Result.Snapshot is { } previous && (previous.ObservedAt > snapshot.ObservedAt
                || previous.ObservedAt == snapshot.ObservedAt && previous.Generation == snapshot.Generation
                && previous.SourceSequence > snapshot.SourceSequence))
                throw new InvalidDataException("An older achievement observation cannot replace the last known state.");
            var known = read.Result.Milestones.ToDictionary(m => m.Id, StringComparer.Ordinal);
            var pending = ImmutableArray.CreateBuilder<AchievementMilestone>();
            foreach (var candidate in candidates)
            {
                if (known.TryGetValue(candidate.Id, out var old))
                {
                    if (!SameMilestoneFact(old, candidate))
                        throw new InvalidDataException("Conflicting achievement milestone identity.");
                    continue;
                }
                known.Add(candidate.Id, candidate);
                pending.Add(candidate);
            }
            if (known.Count > MaximumMilestones) throw new InvalidDataException("Achievement history limit reached.");
            var watermark = Math.Max(read.MaximumSequence, read.Result.Snapshot?.JournalWatermark ?? 0);
            var latest = snapshot with
            {
                HistoryGap = snapshot.HistoryGap || read.Result.Snapshot?.HistoryGap == true || !read.Result.Issues.IsEmpty,
                JournalWatermark = pending.Count == 0 ? watermark : checked(watermark + 1),
            };
            var directory = BindingDirectory(snapshot.Binding);
            Directory.CreateDirectory(directory);
            ValidateLocalPath(directory);
            if (pending.Count > 0)
            {
                // One durable frame contains all facts of this observation and its full recovery checkpoint.
                // This also recovers an initial import if the process stops before the first latest file exists.
                var entry = new JournalEntry(SchemaVersion, latest.JournalWatermark, latest, pending.ToImmutable());
                var line = JsonSerializer.Serialize(entry, Options);
                if (Utf8.GetByteCount(line) > MaximumRecordBytes) throw new InvalidDataException("Achievement journal frame is too large.");
                var journal = Path.Combine(directory, "milestones-v1.jsonl");
                ValidateLocalPath(journal);
                var prefix = read.UnterminatedTail ? "\n" : "";
                if ((File.Exists(journal) ? new FileInfo(journal).Length : 0) + Utf8.GetByteCount(prefix + line + "\n") > MaximumJournalBytes)
                    throw new InvalidDataException("Achievement journal size limit reached.");
                _append(journal, prefix + line + "\n");
            }
            var envelope = new SnapshotEnvelope(SchemaVersion, latest);
            if (JsonSerializer.SerializeToUtf8Bytes(envelope, Options).Length > MaximumRecordBytes)
                throw new InvalidDataException("Achievement snapshot is too large.");
            _save(Path.Combine(directory, "latest-v1.json"), envelope);
            return latest;
        }
    }

    /// <summary>The durable tombstone prevents delayed writer jobs, including a later store instance, recreating history.</summary>
    public void DeleteProfile(string profileId)
    {
        ValidateOpaqueId(profileId);
        lock (_gate)
        {
            _deleted.Add(profileId);
            Directory.CreateDirectory(_root);
            ValidateLocalPath(_root);
            var tombstone = TombstonePath(profileId);
            _save(tombstone, new DeletionEnvelope(SchemaVersion));
            var directory = ProfileDirectory(profileId);
            if (!Directory.Exists(directory)) return;
            ValidateTree(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    private StoreRead Read(AchievementProfileBinding binding)
    {
        var issues = new HashSet<string>(StringComparer.Ordinal);
        if (IsDeleted(binding.ProfileId))
            return new(new(null, [], ["profile-deleted"]), 0, false, false);
        var directory = BindingDirectory(binding);
        if (!Directory.Exists(directory)) return new(new(null, [], []), 0, false, false);
        ValidateLocalPath(directory);
        AchievementSavedSnapshot? snapshot = null;
        var milestones = new Dictionary<string, AchievementMilestone>(StringComparer.Ordinal);
        var sequences = new Dictionary<long, string>();
        var maximumSequence = 0L;
        var unsupported = false;
        var unterminated = false;
        var latestPath = Path.Combine(directory, "latest-v1.json");
        try
        {
            ValidateLocalPath(latestPath);
            if (File.Exists(latestPath))
            {
                if (new FileInfo(latestPath).Length > MaximumRecordBytes) throw new InvalidDataException("Oversized snapshot.");
                var envelope = Deserialize<SnapshotEnvelope>(File.ReadAllText(latestPath, Utf8));
                if (envelope.SchemaVersion != SchemaVersion) { unsupported = true; issues.Add("snapshot-schema-unsupported"); }
                else
                {
                    ValidateSnapshot(envelope.Snapshot);
                    if (envelope.Snapshot.Binding != binding) throw new InvalidDataException("Snapshot binding mismatch.");
                    snapshot = envelope.Snapshot;
                }
            }
        }
        catch (Exception error) when (IsStorageError(error))
        {
            unsupported |= error is UnsupportedAchievementSchemaException;
            issues.Add(error is UnsupportedAchievementSchemaException ? "snapshot-schema-unsupported" : "snapshot-unreadable");
        }
        var journalPath = Path.Combine(directory, "milestones-v1.jsonl");
        try
        {
            ValidateLocalPath(journalPath);
            if (File.Exists(journalPath))
            {
                if (new FileInfo(journalPath).Length > MaximumJournalBytes) throw new InvalidDataException("Oversized journal.");
                using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    unterminated = stream.ReadByte() != '\n';
                    stream.Position = 0;
                }
                using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false);
                while (ReadBoundedLine(reader, out var line, out var oversized))
                {
                    if (oversized) { issues.Add("journal-frame-too-large"); continue; }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = Deserialize<JournalEntry>(line);
                        if (entry.SchemaVersion != SchemaVersion)
                        { unsupported = true; issues.Add("journal-schema-unsupported"); continue; }
                        ValidateSnapshot(entry.Recovery);
                        if (entry.Sequence <= 0 || entry.Sequence != entry.Recovery.JournalWatermark || entry.Recovery.Binding != binding
                            || entry.Milestones.IsDefaultOrEmpty || entry.Milestones.Length > MaximumMilestones)
                            throw new InvalidDataException("Invalid achievement journal frame.");
                        foreach (var milestone in entry.Milestones) ValidateMilestone(milestone);
                        var lineHash = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(line)));
                        if (sequences.TryGetValue(entry.Sequence, out var oldHash))
                        {
                            if (oldHash != lineHash) issues.Add("journal-sequence-conflict");
                            continue;
                        }
                        if (entry.Sequence <= maximumSequence) { issues.Add("journal-sequence-out-of-order"); continue; }
                        if (entry.Sequence != maximumSequence + 1) issues.Add("journal-sequence-gap");
                        if (milestones.Count + entry.Milestones.Count(m => !milestones.ContainsKey(m.Id)) > MaximumMilestones)
                            throw new InvalidDataException("Achievement milestone limit reached.");
                        var conflict = entry.Milestones.Any(m => milestones.TryGetValue(m.Id, out var old) && !SameMilestoneFact(old, m));
                        if (conflict) { issues.Add("journal-milestone-conflict"); continue; }
                        sequences.Add(entry.Sequence, lineHash);
                        maximumSequence = entry.Sequence;
                        foreach (var milestone in entry.Milestones) milestones.TryAdd(milestone.Id, milestone);
                        if (snapshot is null || entry.Sequence > snapshot.JournalWatermark)
                            snapshot = entry.Recovery;
                    }
                    catch (Exception error) when (IsStorageError(error))
                    {
                        unsupported |= error is UnsupportedAchievementSchemaException;
                        issues.Add(error is UnsupportedAchievementSchemaException ? "journal-schema-unsupported" : "journal-frame-unreadable");
                    }
                }
                if (unterminated) issues.Add("journal-tail-incomplete");
            }
        }
        catch (Exception error) when (IsStorageError(error)) { issues.Add("journal-unreadable"); }
        if (snapshot is { } saved && saved.JournalWatermark > maximumSequence) issues.Add("journal-watermark-unavailable");
        if (snapshot is not null && issues.Count > 0) snapshot = snapshot with { HistoryGap = true };
        return new(new(snapshot, milestones.Values.OrderBy(m => m.FirstObservedAt).ThenBy(m => m.Id, StringComparer.Ordinal).ToImmutableArray(),
            issues.Order(StringComparer.Ordinal).ToImmutableArray()), maximumSequence, unterminated, unsupported);
    }

    private sealed record StoreRead(AchievementStoreRead Result, long MaximumSequence, bool UnterminatedTail, bool UnsupportedSchema);
    private sealed record SnapshotEnvelope(int SchemaVersion, AchievementSavedSnapshot Snapshot);
    private sealed record JournalEntry(int SchemaVersion, long Sequence, AchievementSavedSnapshot Recovery, ImmutableArray<AchievementMilestone> Milestones);
    private sealed record DeletionEnvelope(int SchemaVersion);
    private sealed class UnsupportedAchievementSchemaException : IOException { }

    internal static void ValidateBinding(AchievementProfileBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateOpaqueId(binding.ProfileId);
        ValidateOpaqueId(binding.Revision);
    }

    private static void ValidateOpaqueId(string value)
    {
        if (value is null || value.Length != 32 || !Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty
            || id.ToString("N") != value) throw new InvalidDataException("Achievement storage identity must be an opaque GUID.");
    }

    private static void ValidateSnapshot(AchievementSavedSnapshot snapshot)
    {
        if (snapshot is null) throw new InvalidDataException("Missing achievement snapshot.");
        ValidateBinding(snapshot.Binding);
        ValidateRaw(snapshot.Raw);
        if (snapshot.ObservedAt == default || snapshot.MetadataBuild < 0 || snapshot.Generation < 0 || snapshot.SourceSequence < 0
            || snapshot.JournalWatermark < 0 || !ValidToken(snapshot.RuleVersion, 160) || !ValidToken(snapshot.Coverage, 160))
            throw new InvalidDataException("Invalid achievement snapshot metadata.");
    }

    private static void ValidateMilestone(AchievementMilestone milestone)
    {
        if (milestone is null || !ValidToken(milestone.Id, 200) || !Enum.IsDefined(milestone.Kind)
            || milestone.FamilyId is <= 0 || milestone.StageId is <= 0 || milestone.FirstObservedAt == default
            || milestone.OccurredAt == default(DateTimeOffset) || milestone.MetadataBuild < 0
            || milestone.Origin is not ("Baseline" or "Live" or "Recovery" or "Requalified")
            || !ValidToken(milestone.RuleVersion, 160)
            || milestone.PeriodId is { } period && !ValidToken(period, 200)
            || milestone.LabelDe?.Length > 512 || milestone.LabelEn?.Length > 512)
            throw new InvalidDataException("Invalid achievement milestone.");
        ValidateRaw(milestone.RawPatch);
    }

    private static bool ValidToken(string? value, int maximum) => !string.IsNullOrEmpty(value) && value.Length <= maximum
        && value.All(c => c is >= '!' and <= '~');

    private static void ValidateRaw(CharacterAchievementState raw)
    {
        if (raw?.Achievements is null || raw.RewardGroups is null || raw.Achievements.Count > 50_000 || raw.RewardGroups.Count > 16_384
            || raw.Achievements.Any(row => row.Value is null) || raw.RewardGroups.Any(group => group.Value.IsDefault || group.Value.Length > 16_384)
            || raw.RewardGroups.Sum(group => (long)group.Value.Length) > 1_000_000)
            throw new InvalidDataException("Invalid or oversized raw achievement state.");
    }

    private static bool SameMilestoneFact(AchievementMilestone left, AchievementMilestone right) =>
        left.Kind == right.Kind && left.FamilyId == right.FamilyId && left.StageId == right.StageId && left.PeriodId == right.PeriodId;

    private bool IsDeleted(string profileId)
    {
        if (_deleted.Contains(profileId)) return true;
        var tombstone = TombstonePath(profileId);
        ValidateLocalPath(tombstone);
        return File.Exists(tombstone);
    }

    private string TombstonePath(string profileId) => Path.Combine(_root, ".deleted-" + profileId + ".json");
    private string ProfileDirectory(string profileId) => ChildPath(_root, profileId);
    private string BindingDirectory(AchievementProfileBinding binding) => ChildPath(ProfileDirectory(binding.ProfileId), binding.Revision);

    private static string ChildPath(string parent, string child)
    {
        var path = Path.GetFullPath(Path.Combine(parent, child));
        if (!path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Achievement path escaped its storage root.");
        ValidateLocalPath(path);
        return path;
    }

    private static T Deserialize<T>(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("SchemaVersion", out var schema)
            && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version != SchemaVersion)
            throw new UnsupportedAchievementSchemaException();
        return document.Deserialize<T>(Options) ?? throw new InvalidDataException("Missing achievement JSON value.");
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate achievement JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static bool ReadBoundedLine(StreamReader reader, out string line, out bool oversized)
    {
        var builder = new StringBuilder();
        oversized = false;
        var any = false;
        for (int next; (next = reader.Read()) >= 0;)
        {
            any = true;
            if (next == '\n') break;
            if (builder.Length < MaximumRecordBytes) builder.Append((char)next); else oversized = true;
        }
        line = builder.ToString();
        return any;
    }

    private static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or DecoderFallbackException or ArgumentException or NotSupportedException or OverflowException;

    private static void ValidateLocalPath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path[root.Length..].Contains(':') || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException("Achievement history requires local regular paths.");
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not valid achievement storage.");
        }
    }

    private static void ValidateTree(string directory)
    {
        ValidateLocalPath(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            ValidateLocalPath(path);
            if (Directory.Exists(path)) ValidateTree(path);
        }
    }

    private static void AppendDurably(string path, string line)
    {
        ValidateLocalPath(path);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Utf8.GetBytes(line));
        stream.Flush(flushToDisk: true);
    }

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
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                ValidateLocalPath(path);
                ValidateLocalPath(temporary);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { ValidateLocalPath(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (IsStorageError(error)) { }
            }
        });
    }
}
