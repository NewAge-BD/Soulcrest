using System.Collections.Immutable;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private NetworkSkillSnapshot _skillSnapshot = NetworkSkillSnapshot.Empty;
    private long _skillMessageRevision;
    private bool _skillOwnQualified;
    public NetworkSkillSnapshot SkillSnapshot { get { lock (_gate) return _skillSnapshot; } }

    private void ResetSkillContext()
    {
        _skillOwnQualified = false;
        _skillSnapshot = NetworkSkillSnapshot.Empty;
    }

    private void BindSkillBaseline(EntryCandidate? transaction)
    {
        if (_ownCharacter is not { } own) return;
        _skillSnapshot = new(own, ImmutableDictionary<int, OwnSkillLevel>.Empty,
            false, false, null, 0, "unknown");
        if (_skillOwnQualified && transaction is { SkillRejected: false, Skills: { } skills, SkillsAt: { } at }
            && own.ObservedAt is { } entered && at <= entered)
            _skillSnapshot = new(own, skills.Skills, true, true, at, ++_progressSequence, "own-login-skill-list");
        else if (transaction is { SkillRejected: true })
            _skillSnapshot = _skillSnapshot with { FormatRejected = true };
    }

    private void ProcessOwnSkillMessage(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        // No authority from an arbitrary native connection. A prepared matching login is provisional
        // until the complete Own confirms its character/entity on this same transport.
        var own = _skillOwnQualified && _identityStream == stream ? _ownCharacter : null;
        var pending = own is null && _entryCandidate is { Prepared: true } candidate
            && candidate.Stream == stream && candidate.PreparedAt is { } prepared && at >= prepared ? candidate : null;
        if (own is null && pending is null) return;
        _skillMessageRevision++;
        if (opcode == 0x5100)
        {
            var parsed = OwnSkillMessage.TryParseList(message);
            if (pending is not null)
            {
                if (pending.SkillsSeen) { pending.SkillRejected = true; pending.Skills = null; return; }
                pending.SkillsSeen = true;
                pending.Skills = parsed;
                pending.SkillsAt = parsed is null ? null : at;
                pending.SkillRejected = parsed is null;
                return;
            }
            if (own!.ObservedAt is not { } entered || at < entered
                || _skillSnapshot.ObservedAt is { } previous && at < previous) return;
            _skillSnapshot = parsed is null
                ? _skillSnapshot with { Character = own, IsFresh = false, FormatRejected = true }
                : new(own, parsed.Skills, true, true, at, ++_progressSequence, "own-skill-list");
        }
        else
        {
            // Login changes precede the full list in the observed handshake; they never create a
            // full snapshot. A change after its list would make that provisional list incomplete.
            if (pending is not null)
            {
                if (pending.SkillsSeen) { pending.SkillRejected = true; pending.Skills = null; }
                return;
            }
            if (own!.ObservedAt is not { } entered || at < entered
                || _skillSnapshot.ObservedAt is { } previous && at < previous) return;
            var change = OwnSkillMessage.TryParseChanges(message);
            if (change is null)
                _skillSnapshot = _skillSnapshot with { Character = own, IsFresh = false, FormatRejected = true };
            else if (_skillSnapshot is { IsKnown: true, IsFresh: true, FormatRejected: false })
                _skillSnapshot = new(own, _skillSnapshot.Skills.SetItems(change.Skills), true, true,
                    at, ++_progressSequence, "own-skill-level-update");
        }
        SafeEvent.Raise(Changed, "Eigene Skillstufen");
    }
}
