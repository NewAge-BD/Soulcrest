using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

/// <summary>Transient shared Genus evidence. It never supplies character or active-preset authority.</summary>
public interface INetworkGenusInsightSource
{
    NetworkGenusInsightSnapshot GenusInsightSnapshot { get; }
    event Action? GenusInsightChanged;
}

public sealed record NetworkGenusInsightSnapshot(long Generation, long Sequence, NetworkCombatDomain<PetProgressSnapshot> Domain)
{
    public static NetworkGenusInsightSnapshot Empty { get; } = new(0, 0, NetworkCombatDomain<PetProgressSnapshot>.Unknown);
}

public sealed partial class NetworkLootService
{
    private NetworkGenusInsightSnapshot _genusInsightSnapshot = NetworkGenusInsightSnapshot.Empty;
    private string? _genusInsightStream;
    private long _genusInsightPetGeneration = -1, _genusInsightCaptureGeneration = -1;
    public event Action? GenusInsightChanged;

    public NetworkGenusInsightSnapshot GenusInsightSnapshot
    {
        get
        {
            lock (_gate)
            {
                var snapshot = _genusInsightSnapshot;
                return !snapshot.Domain.IsFresh || GenusInsightWitnessCurrent()
                    ? snapshot : snapshot with { Domain = Stale(snapshot.Domain) };
            }
        }
    }

    private bool GenusInsightWitnessCurrent() => Running && Volatile.Read(ref _disposed) == 0
        && _genusInsightCaptureGeneration == _generation && _genusInsightPetGeneration == _petGeneration
        && _genusInsightStream is { } stream && _petStreams.Contains(stream);

    /// <summary>Only the parser object already accepted by the global pet transaction can enter this RAM source.</summary>
    private void PublishAcceptedGenusInsight(string stream, PetProgressSnapshot accepted, DateTimeOffset at,
        long petGeneration, long captureGeneration, long sourceRevision)
    {
        if (!Running || Volatile.Read(ref _disposed) != 0 || petGeneration != _petGeneration || captureGeneration != _generation
            || sourceRevision != _sourceRevisions[0] || !_petStreams.Contains(stream)
            || _genusInsightSnapshot.Domain.ObservedAt is { } previous && at < previous) return;
        var generation = _genusInsightSnapshot.Generation;
        if (generation == 0 || _genusInsightStream != stream || _genusInsightPetGeneration != petGeneration
            || _genusInsightCaptureGeneration != captureGeneration) generation++;
        var sequence = NextGenusInsightSequence();
        _genusInsightStream = stream;
        _genusInsightPetGeneration = petGeneration;
        _genusInsightCaptureGeneration = captureGeneration;
        _genusInsightSnapshot = new(generation, sequence,
            Observed(accepted with { SelectedGenusPanel = null }, at, sequence, "own-pet-genus-list"));
        NotifyGenusInsightChanged();
    }

    /// <summary>Revoke before callbacks; notification is delayed until the surrounding reset has finished mutating.</summary>
    private void InvalidateGenusInsight(NetworkCombatSourceStatus status = NetworkCombatSourceStatus.Stale)
    {
        var previous = _genusInsightSnapshot;
        var sequence = NextGenusInsightSequence();
        _genusInsightStream = null;
        _genusInsightPetGeneration = _genusInsightCaptureGeneration = -1;
        var domain = previous.Domain with { Status = previous.Domain.Value is null ? NetworkCombatSourceStatus.Unknown : status,
            IsFresh = false, Sequence = sequence };
        _genusInsightSnapshot = new(previous.Generation + 1, sequence, domain);
    }

    private void NotifyGenusInsightChanged() => SafeEvent.Raise(GenusInsightChanged, "Gemeinsamer Genus-Netzwerkstand");

    // Own transitions reset _progressSequence, while the shared collection can remain fresh.
    // Keep both the shared ordering and the existing character-history ordering comparable.
    private long NextGenusInsightSequence() => _progressSequence = Math.Max(_progressSequence, _genusInsightSnapshot.Sequence) + 1;

    /// <summary>Reuse the qualified decoder and exact target checks, with an additional shared-source transport witness.</summary>
    private bool ProcessGenusInsightDelta(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (opcode is not (0x9018 or 0x901A) || !GenusInsightWitnessCurrent() || _genusInsightSnapshot.Domain is not
                { IsFresh: true, Status: NetworkCombatSourceStatus.Complete, Value: not null, ObservedAt: { } observed }
            || at < observed) return false;
        if (_genusInsightStream != stream)
        {
            if (!_combatOwnQualified || _identityStream != stream || _ownCharacter?.ObservedAt is not { } entered || at < entered)
                return false;
            // A qualified own change on another transport cannot refine this baseline, nor leave its old rolls live.
            InvalidateGenusInsight();
            return true;
        }
        if (!_combatOwnQualified || _ownCharacter is not { } own || own.ObservedAt is not { } ownEntered || at < ownEntered
            || !ReferenceEquals(_combatSnapshot.Character, own))
        {
            // A change on the exact baseline transport revokes its old values even when no full Own can qualify the reply.
            InvalidateGenusInsight();
            return true;
        }
        // Shared Genus and Own can be observed on different independently owned connections.
        // The unchanged baseline transport qualifies the reply; the full Own supplies only current character authority.
        var before = new NetworkCharacterCombatSnapshot { Pets = _genusInsightSnapshot.Domain };
        var sequence = NextGenusInsightSequence();
        var next = ApplyCombatMessage(before, own, opcode, message, at, sequence);
        if (ReferenceEquals(before, next)) return false;
        if (!next.Pets.IsFresh || next.Pets.Status != NetworkCombatSourceStatus.Complete)
        {
            InvalidateGenusInsight(next.Pets.Status);
            return true;
        }
        _genusInsightSnapshot = new(_genusInsightSnapshot.Generation, sequence, next.Pets with
        {
            Sequence = sequence,
            Value = next.Pets.Value! with { SelectedGenusPanel = null }
        });
        return true;
    }
}
