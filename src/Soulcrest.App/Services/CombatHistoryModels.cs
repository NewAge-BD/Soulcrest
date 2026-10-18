using Soulcrest.Core.Combat;

namespace Soulcrest.App.Services;

/// <summary>A derived, replaceable run list. Detail records and event journals are authoritative.</summary>
public sealed record CombatHistoryIndex
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public CombatHistoryIndexEntry[] Runs { get; init; } = [];
}

public sealed record CombatHistoryIndexEntry(Guid Id, string DungeonKey, DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc, bool Favorite, int AttemptCount);

/// <summary>Incomplete final lines are preserved on disk and are never treated as a complete event.</summary>
public sealed record CombatJournalLoadResult(IReadOnlyList<CombatEventEnvelope> Events, bool HasIncompleteTail);
public sealed record CombatHistoryRunLoadResult(CombatRunDocument Run, IReadOnlyList<Guid> IncompleteJournalAttempts)
{
    public bool HasIncompleteJournal => IncompleteJournalAttempts.Count > 0;
}

/// <summary>Readable data is not replaced with an empty history when its version or content is unsupported.</summary>
public sealed class CombatHistoryFormatException(string path, string reason, Exception? innerException = null)
    : IOException($"Combat history '{path}': {reason}", innerException)
{
    public string FilePath { get; } = path;
}
