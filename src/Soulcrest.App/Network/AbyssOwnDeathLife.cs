using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

/// <summary>
/// Conservative RAM-only death gate. Own/world/gap resets alone never rearm a seen victim entity.
/// A complete later own message must independently prove positive health after its last death.
/// Only a real capture-generation change clears the bounded history; there is no eviction or timer.
/// </summary>
internal sealed class AbyssOwnDeathLife
{
    internal const int MaximumSeenEntities = 256;
    private readonly record struct Key(long CharacterId, int ServerId, int EntityKey);
    private readonly HashSet<Key> _seen = [];
    private readonly Dictionary<Key, DateTimeOffset> _lastDeaths = [];
    private long? _captureGeneration;
    internal bool Overflowed { get; private set; }

    internal bool TryConsume(OwnCharacterContext own, AbyssPlayerCasterDeathObservation death,
        DateTimeOffset at, DateTimeOffset now, long captureGeneration, bool currentOwnTransportQualified = false)
    {
        if (own.Generation <= 0 || own.ObservedAt is not { } entered || entered == default
            || at == default || at < entered || at > now || own.Identity.EntityKey != death.VictimEntityKey
            || own.Identity.CharacterId <= 0 || own.Identity.ServerId <= 0 || own.Identity.EntityKey <= 0
            || death.CasterServerId == own.Identity.ServerId
                && string.Equals(death.CasterName, own.Identity.Name, StringComparison.Ordinal)
            || !ObserveCapture(captureGeneration) || Overflowed) return false;
        var key = new Key(own.Identity.CharacterId, own.Identity.ServerId, own.Identity.EntityKey);
        if (_seen.Contains(key))
        {
            // The current immutable Own carries this independently validated health fact at its
            // original observation time. Reentrant callbacks cannot insert a separate rearm gap.
            // A duplicate death cannot reuse the same earlier positive-health observation.
            if (!currentOwnTransportQualified || own.HasPositiveHealth != true || !_lastDeaths.TryGetValue(key, out var lastDeath)
                || entered <= lastDeath || at <= entered) return false;
        }
        else if (_seen.Count >= MaximumSeenEntities) { Overflowed = true; return false; }
        _seen.Add(key);
        _lastDeaths[key] = at;
        return true;
    }

    internal bool ObserveCapture(long generation)
    {
        if (generation < 0 || _captureGeneration is { } current && generation < current) return false;
        if (_captureGeneration != generation)
        {
            _seen.Clear();
            _lastDeaths.Clear();
            Overflowed = false;
            _captureGeneration = generation;
        }
        return true;
    }
}
