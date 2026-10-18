namespace Soulcrest.App.Network;

/// <summary>A subscription to the existing passive game source; subscribing never starts a recorder.</summary>
public interface INetworkCombatSource
{
    NetworkCombatSubscription SubscribeCombat(int capacity = 2048, int maxBytes = 8 * 1024 * 1024);
}

/// <summary>Pause progress application without stopping another consumer's shared capture.</summary>
public interface INetworkProgressControl
{
    void SetProgressProcessingEnabled(bool enabled);
}

public enum CombatCaptureKind { Message, Gap, SessionReset, QueueOverflow }

/// <summary>
/// Transient transport input, never a persistent combat event. CapturedAtUtc is the original capture
/// time of the packet releasing a complete message, not a cast time or the timestamp of every byte.
/// Capture clocks may regress; ReceivedElapsedTicks and Sequence preserve observation order instead.
/// Lifecycle boundaries have no packet timestamp. StreamToken is opaque and contains no endpoints.
/// </summary>
public sealed record CombatCaptureEnvelope(long Sequence, long ReceivedElapsedTicks, DateTimeOffset? CapturedAtUtc,
    string? StreamToken, long Generation, CombatCaptureKind Kind, ReadOnlyMemory<byte> Message,
    string? Reason = null, long DroppedRecords = 0);
