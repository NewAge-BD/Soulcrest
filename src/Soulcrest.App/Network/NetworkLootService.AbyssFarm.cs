using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private OwnCharacterContext? _abyssFarmCharacter;
    private long? _abyssFarmTotal;
    private DateTimeOffset? _abyssFarmAt;
    private long _abyssFarmRevision;
    private bool _abyssFarmRejected;
    private sealed record PendingAbyssFarmUpdate(string Stream, OwnCharacterContext Character,
        long CaptureGeneration, long Revision, DateTimeOffset At, long? Total);

    private void ProcessAbyssFarmMessage(string stream, byte[] message, DateTimeOffset at)
    {
        if (!Running || _ownCharacter is not { } own || _identity is null || _identityStream != stream
            || own.Generation != _progressGeneration || _abyssFarmRejected) return;
        var observation = new PendingAbyssFarmUpdate(stream, own, _generation, _abyssFarmRevision, at,
            AbyssFarmTotalMessage.TryParse(message)?.Total);
        EnqueueAbyssObservation(stream, isRank: false, () => ApplyAbyssFarmObservation(observation), isFarm: true);
    }

    private void ApplyAbyssFarmObservation(PendingAbyssFarmUpdate observation)
    {
        if (!AbyssFarmBindingMatches(observation)) return;
        if (observation.Total is not { } total || observation.Character.ObservedAt is not { } ownAt
            || ownAt == default || observation.At == default || observation.At < ownAt
            || observation.At > DateTimeOffset.UtcNow
            || _abyssFarmAt is { } previousAt && observation.At < previousAt
            || _abyssFarmTotal is { } previous && total < previous)
        {
            ResetAbyssFarmContext(rejected: true);
            SafeEvent.Raise(Changed, "Abyss-Farmstand-Netzwerkstatus");
            return;
        }
        _abyssFarmCharacter = observation.Character;
        _abyssFarmTotal = total;
        _abyssFarmAt = observation.At;
        _abyssSequence++;
        // A server farm total confirms the cap's absolute counter, never a second AP gain.
        SafeEvent.Raise(Changed, "Abyss-Farmstand-Netzwerkstatus");
    }

    private bool AbyssFarmBindingMatches(PendingAbyssFarmUpdate observation) => Running
        && _generation == observation.CaptureGeneration && _progressGeneration == observation.Character.Generation
        && _abyssFarmRevision == observation.Revision && !_abyssFarmRejected
        && _identityStream == observation.Stream && ReferenceEquals(_ownCharacter, observation.Character);

    private void ResetAbyssFarmContext(bool rejected = false)
    {
        _abyssFarmCharacter = null;
        _abyssFarmTotal = null;
        _abyssFarmAt = null;
        _abyssFarmRejected = rejected;
        _abyssFarmRevision++;
        var remaining = _abyssObservations.Where(observation => !observation.IsFarm).ToArray();
        _abyssObservations.Clear();
        foreach (var observation in remaining) _abyssObservations.Enqueue(observation);
        _abyssSequence++;
    }
}
