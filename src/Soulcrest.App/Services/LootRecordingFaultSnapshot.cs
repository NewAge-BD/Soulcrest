using System.Text.Json;

namespace Soulcrest.App.Services;

public enum LootRecordingFaultStage
{
    CommandQueue, Observation, Activity, JournalPrepare, JournalAppend, Checkpoint, Publication, Shutdown
}

public enum LootRecordingFaultKind
{
    QueueOverflow, Validation, AccessDenied, Io, Serialization, NumericOverflow, Unexpected
}

/// <summary>The first fatal recorder error, without paths, session/profile identities or exception messages.</summary>
public sealed record LootRecordingFaultSnapshot(DateTimeOffset At, LootRecordingFaultStage Stage,
    LootRecordingFaultKind Kind, string ExceptionType, int HResult)
{
    public string Status => Kind switch
    {
        LootRecordingFaultKind.QueueOverflow => "Loot-Aufzeichnung wegen einer überfüllten Ereigniswarteschlange unterbrochen.",
        LootRecordingFaultKind.Validation => "Loot-Aufzeichnung wegen ungültiger Sessiondaten unterbrochen.",
        LootRecordingFaultKind.AccessDenied => "Loot-Aufzeichnung wegen fehlender Schreibberechtigung unterbrochen.",
        LootRecordingFaultKind.Io => "Loot-Aufzeichnung wegen eines Schreibfehlers unterbrochen.",
        LootRecordingFaultKind.Serialization => "Loot-Aufzeichnung wegen eines Serialisierungsfehlers unterbrochen.",
        LootRecordingFaultKind.NumericOverflow => "Loot-Aufzeichnung wegen eines Zahlenüberlaufs unterbrochen.",
        _ => "Loot-Aufzeichnung wegen eines internen Fehlers unterbrochen."
    };
}

internal static class LootRecordingFaults
{
    private const string StageKey = "Soulcrest.LootRecordingStage";

    // Preserve the original exception type/HResult and stack for existing callers and private logs.
    internal static void Mark(Exception error, LootRecordingFaultStage stage)
    {
        if (!error.Data.Contains(StageKey)) error.Data[StageKey] = stage;
    }

    internal static LootRecordingFaultSnapshot Create(Exception error, LootRecordingFaultStage fallback, DateTimeOffset at) =>
        new(at, error.Data[StageKey] is LootRecordingFaultStage stage ? stage : fallback,
            fallback == LootRecordingFaultStage.CommandQueue ? LootRecordingFaultKind.QueueOverflow : error switch
            {
                InvalidDataException => LootRecordingFaultKind.Validation,
                UnauthorizedAccessException => LootRecordingFaultKind.AccessDenied,
                IOException => LootRecordingFaultKind.Io,
                JsonException or NotSupportedException => LootRecordingFaultKind.Serialization,
                OverflowException => LootRecordingFaultKind.NumericOverflow,
                _ => LootRecordingFaultKind.Unexpected
            }, error.GetType().Name, error.HResult);
}
