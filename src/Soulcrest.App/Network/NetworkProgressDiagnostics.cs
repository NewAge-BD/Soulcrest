namespace Soulcrest.App.Network;

/// <summary>Anonymous capture counters only; no profile identifiers, endpoints, or packet content.</summary>
internal sealed record NetworkProgressDiagnostics(bool IdentityBound, long IdentityBindings, long ContextResets,
    string LastResetReason, DateTimeOffset? LastResetAt, long RejectedProgressMessages, ushort? LastRejectedOpcode,
    DateTimeOffset? LastRejectedAt, long AcceptedSoulMessages, long AcceptedSoulPoints, DateTimeOffset? LastSoulAt,
    int PendingMessages, int PendingBytes, bool PendingOverflow);
