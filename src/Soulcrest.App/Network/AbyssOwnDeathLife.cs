using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

/// <summary>
/// Conservative RAM-only death gate. Soft Own/world/gap resets never rearm a seen victim entity.
/// Only a real capture-generation change clears the bounded set; there is no eviction or timer.
/// A same-entity revival can remain undercounted until native alive-state evidence is qualified.
/// </summary>
internal sealed class AbyssOwnDeathLife
{
    internal const int MaximumSeenEntities = 256;
    private readonly record struct Key(long CharacterId, int ServerId, int EntityKey);
    private readonly HashSet<Key> _seen = [];
    private long? _captureGeneration;
    internal bool Overflowed { get; private set; }

    internal bool TryConsume(OwnCharacterContext own, AbyssPlayerCasterDeathObservation death,
        DateTimeOffset at, DateTimeOffset now, long captureGeneration)
    {
        if (own.Generation <= 0 || own.ObservedAt is not { } entered || entered == default
            || at == default || at < entered || at > now || own.Identity.EntityKey != death.VictimEntityKey
            || own.Identity.CharacterId <= 0 || own.Identity.ServerId <= 0 || own.Identity.EntityKey <= 0
            || death.CasterServerId == own.Identity.ServerId
                && string.Equals(death.CasterName, own.Identity.Name, StringComparison.Ordinal)
            || !ObserveCapture(captureGeneration) || Overflowed) return false;
        var key = new Key(own.Identity.CharacterId, own.Identity.ServerId, own.Identity.EntityKey);
        if (_seen.Contains(key)) return false;
        if (_seen.Count >= MaximumSeenEntities) { Overflowed = true; return false; }
        _seen.Add(key);
        return true;
    }

    internal bool ObserveCapture(long generation)
    {
        if (generation < 0 || _captureGeneration is { } current && generation < current) return false;
        if (_captureGeneration != generation)
        {
            _seen.Clear();
            Overflowed = false;
            _captureGeneration = generation;
        }
        return true;
    }
}
