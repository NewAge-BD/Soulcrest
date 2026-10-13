namespace Soulcrest.Core.Progress;

/// <summary>Ordering of one passive pet source; pet progress is global (user request 2026-10-13).</summary>
public sealed record PetProgressContext(long Generation, long Sequence, DateTimeOffset At);
