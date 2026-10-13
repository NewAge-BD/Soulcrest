namespace Soulcrest.App.Network;

internal enum NetworkProgressEventKind { Reset, Rejected, Snapshot, Acquired, Upgraded, Gained, Spent, FilterExpanded, FilterFailed }

/// <summary>Bounded anonymous protocol trace: fixed kinds/reasons and public pet numbers only, never identities or payloads.</summary>
internal sealed record NetworkProgressDiagnosticEvent(DateTimeOffset At, NetworkProgressEventKind Kind,
    ushort? Opcode = null, int? PetNumber = null, int? Level = null, int? Souls = null,
    string? ResetReason = null, int? Count = null);
