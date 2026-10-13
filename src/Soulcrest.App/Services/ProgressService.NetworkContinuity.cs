using Soulcrest.Core.Network;
using Soulcrest.Core.Progress;

namespace Soulcrest.App.Services;

public sealed partial class ProgressService
{
    internal sealed record NetworkPetContinuityRaw(int Level, int AvailableSouls, int EarnedTotal);
    internal sealed record NetworkPetContinuityEntry(int Souls, DateTimeOffset UpdatedAt, string Source);

    /// <summary>Legacy, single-use proof of a previously confirmed source state; never persisted or exported.</summary>
    internal sealed class NetworkPetContinuityToken(ProgressProfile profile, int serverId, DateTimeOffset baselineAt,
        IReadOnlyDictionary<int, NetworkPetContinuityRaw> raw, IReadOnlyDictionary<string, NetworkPetContinuityEntry> entries)
    {
        internal ProgressProfile Profile { get; } = profile;
        internal int ServerId { get; } = serverId;
        internal DateTimeOffset BaselineAt { get; } = baselineAt;
        internal IReadOnlyDictionary<int, NetworkPetContinuityRaw> Raw { get; } = raw;
        internal IReadOnlyDictionary<string, NetworkPetContinuityEntry> Entries { get; } = entries;
        internal bool Consumed { get; set; }
    }

    internal NetworkPetContinuityToken? CaptureNetworkContinuity(NetworkProgressContext context)
    {
        lock (_gate)
        {
            if (_networkContext is not { } bound || bound.Generation != context.Generation ||
                bound.ServerId != context.ServerId || bound.CharacterId != context.CharacterId ||
                bound.Identity.EntityKey != context.Identity.EntityKey || context.Sequence < bound.Sequence ||
                context.Sequence < _networkSequence ||
                _profile.NetworkBaselineAt is not { } at) return null;
            var raw = _profile.NetworkPets.Where(pair => pair.Value.HasBaseline && pair.Value.LevelObserved && pair.Value.BalanceObserved)
                .ToDictionary(pair => pair.Key, pair => new NetworkPetContinuityRaw(pair.Value.Level, pair.Value.AvailableSouls, pair.Value.EarnedTotal));
            if (raw.Count == 0) return null;
            var entries = _profile.Pets.Where(pair => !pair.Value.ManualOverride && pair.Value.Freshness == ProgressFreshness.Fresh &&
                    SoulId(pair.Key) is { } number && raw.ContainsKey(number))
                .ToDictionary(pair => pair.Key, pair => new NetworkPetContinuityEntry(pair.Value.Souls, pair.Value.UpdatedAt, pair.Value.Source), StringComparer.Ordinal);
            return new(_profile, context.ServerId, at, raw, entries);
        }
    }

    /// <summary>Restore only unchanged confirmed values after a new own binding; the network caller proves uninterrupted continuity.</summary>
    internal bool TryRestoreNetworkContinuity(NetworkPetContinuityToken token, NetworkProgressContext context)
    {
        lock (_gate)
        {
            if (token.Consumed) return false;
            token.Consumed = true;
            if (_networkContext is not { } bound || bound.Generation != context.Generation || bound.ServerId != context.ServerId ||
                bound.CharacterId != context.CharacterId || bound.Identity.EntityKey != context.Identity.EntityKey ||
                context.Sequence < bound.Sequence || _networkSequence != -1 ||
                token.ServerId != context.ServerId || !ReferenceEquals(token.Profile, _profile) ||
                _profile.NetworkBaselineAt != token.BaselineAt) return false;
            // MarkNetworkStale/Bind change HasBaseline only. Any changed raw value or source
            // confirmation means the saved token no longer describes the current collection.
            foreach (var (number, previous) in token.Raw)
                if (!_profile.NetworkPets.TryGetValue(number, out var raw) || !raw.LevelObserved || !raw.BalanceObserved ||
                    raw.Level != previous.Level || raw.AvailableSouls != previous.AvailableSouls || raw.EarnedTotal != previous.EarnedTotal)
                    return false;
            foreach (var number in token.Raw.Keys) _profile.NetworkPets[number].HasBaseline = true;
            foreach (var (id, previous) in token.Entries)
                if (_profile.Pets.TryGetValue(id, out var entry) && !entry.ManualOverride && entry.Souls == previous.Souls &&
                    entry.UpdatedAt == previous.UpdatedAt && entry.Source == previous.Source)
                    entry.Freshness = ProgressFreshness.Fresh;
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Pet-Netzwerkstand");
        return true;
    }
}
