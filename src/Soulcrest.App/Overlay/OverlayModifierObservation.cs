namespace Soulcrest.App.Overlay;

/// <summary>
/// Only the latest Alt/Shift observation is retained. A newer release from either passive
/// source supersedes a stale press from the other (user diagnosis 2026-10-11).
/// </summary>
internal sealed record OverlayModifierObservation(bool? Held, long Sequence)
{
    private static long _nextSequence;
    internal static readonly OverlayModifierObservation Unknown = new(null, 0);

    internal static OverlayModifierObservation Capture(bool? held) => held is null ? Unknown
        : new(held, Interlocked.Increment(ref _nextSequence));

    internal static bool? Latest(OverlayModifierObservation first, OverlayModifierObservation second) =>
        first.Held is null ? second.Held : second.Held is null ? first.Held
        : first.Sequence > second.Sequence ? first.Held : second.Held;
}
