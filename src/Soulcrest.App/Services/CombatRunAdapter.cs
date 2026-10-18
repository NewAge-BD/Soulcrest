using System.Globalization;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public enum CombatBossVictoryProof { MiddleBossReward, DungeonSuccess, None }

/// <summary>An explicitly reviewed single-entity encounter join, not a high-HP or name heuristic.
/// Target rules do not grant amount semantics or infer a server instance.</summary>
public sealed record CombatBossRule(int DungeonDataId, int MapDataId, int NpcDataId,
    CombatEncounterDefinition Encounter, CombatBossVictoryProof VictoryProof, string EvidenceId,
    string? AmountEvidenceScope = null);

public sealed class CombatBossCatalog
{
    private readonly CombatBossRule[] _rules;
    public static CombatBossCatalog Empty { get; } = new([]);
    public CombatBossCatalog(IEnumerable<CombatBossRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.Take(1001).ToArray();
        if (_rules.Length > 1000 || _rules.Any(r => r is null || r.DungeonDataId <= 0 || r.MapDataId <= 0 ||
            r.NpcDataId <= 0 || r.Encounter is null || string.IsNullOrWhiteSpace(r.EvidenceId) ||
            !Enum.IsDefined(r.VictoryProof)) || _rules.GroupBy(r => (r.DungeonDataId, r.MapDataId, r.NpcDataId)).Any(g => g.Count() != 1) ||
            _rules.GroupBy(r => (r.DungeonDataId, r.MapDataId, r.Encounter.BossKey, r.Encounter.EncounterVariantKey)).Any(g => g.Count() != 1))
            throw new ArgumentException("Invalid or ambiguous encounter joins.", nameof(rules));
    }
    public bool ContainsDungeon(int dungeon, int map) => _rules.Any(r => r.DungeonDataId == dungeon && r.MapDataId == map);
    public CombatBossRule? Find(int dungeon, int map, int npc) => _rules.SingleOrDefault(r =>
        r.DungeonDataId == dungeon && r.MapDataId == map && r.NpcDataId == npc);
}

public sealed record CombatRunAdapterSnapshot(long Observations, long UnconfirmedDungeons, long UnknownAmounts,
    long EndProofs, long IgnoredEndSignals, string? LastBoundaryCode);

/// <summary>Single ordered consumer. Authority comes from CombatNetworkService's fresh Own + roster.
/// RawMapKey, HP zero, party deaths, silence and dungeon deadlines are never instance/wipe proofs.</summary>
public sealed class CombatRunAdapter
{
    private sealed record Binding(CombatBossRule Rule, CombatEntityReference Target, long AppearanceSequence)
    {
        public long? MaximumHp { get; set; }
    }
    private readonly CombatRunController _controller;
    private readonly CombatBossCatalog _catalog;
    private readonly ICombatAmountClassifier? _amounts;
    private readonly Func<CombatSourceContext, IEnumerable<int>, bool> _bindTargets;
    private CombatSourceContext? _context;
    private OwnWorldObservation? _world;
    private CombatPartyMember[] _members = [];
    private readonly Dictionary<int, Binding> _bindings = [];
    private int? _dungeon;
    private CombatControllerStamp? _dungeonStamp;
    private DungeonRunState? _dungeonState;
    private bool _runConfirmed;
    private Binding? _selected;
    private CombatControllerStamp? _death;
    private CombatControllerStamp? _lastStamp;
    private Guid? _deathAttempt;
    private long _observations, _unconfirmedDungeons, _unknownAmounts, _endProofs, _ignoredEndSignals;
    private string? _lastBoundary;

    public CombatRunAdapter(CombatRunController controller, CombatBossCatalog catalog,
        Func<CombatSourceContext, IEnumerable<int>, bool> bindTargets, ICombatAmountClassifier? amounts = null)
    {
        _controller = controller;
        _catalog = catalog;
        _bindTargets = bindTargets;
        _amounts = amounts;
    }

    public CombatRunAdapterSnapshot Snapshot => new(_observations, _unconfirmedDungeons, _unknownAmounts,
        _endProofs, _ignoredEndSignals, _lastBoundary);

    public void Observe(CombatNetworkObservation input, OwnWorldObservation? confirmedWorld)
    {
        _observations++;
        var stamp = Stamp(input);
        if (_context == input.Context && _world is not null && (confirmedWorld is null || confirmedWorld != _world))
        {
            Interrupt(stamp, CombatGapReason.UnknownAttribution);
            return;
        }
        if (input.Observation is CombatWireParty party)
        {
            if (confirmedWorld is null) return;
            if (_context is not null && _context != input.Context)
                Boundary(new(input.Sequence, _context, "source-context-changed", input.ReceivedElapsedTicks, input.CapturedAtUtc));
            if (_context == input.Context && party.Value.Members.Any(m => _bindings.ContainsKey(m.EntityKey) ||
                _selected?.Target.EntityId == Number(m.EntityKey)))
            {
                Interrupt(stamp, CombatGapReason.UnknownAttribution);
                return;
            }
            _context = input.Context;
            _lastStamp = stamp;
            _world = confirmedWorld;
            _members = party.Value.Members.ToArray();
            if (_dungeon is not null && _death is null) _controller.SetRoster(stamp, Participants());
            return;
        }
        if (_context != input.Context || _world is null || _members.Length == 0) return;
        _lastStamp = stamp;

        switch (input.Observation)
        {
            case CombatWireDungeonState state:
                if (!_catalog.ContainsDungeon(state.Value.DungeonId, _world.MapDataId)) { _unconfirmedDungeons++; return; }
                if (_dungeon == state.Value.DungeonId && state.Value.State == DungeonRunState.Prepare &&
                    _dungeonState is not DungeonRunState.Prepare)
                {
                    // Re-entry/reset semantics are not proven. Revoke old authority without declaring a wipe.
                    Interrupt(stamp, CombatGapReason.UnknownAttribution);
                    return;
                }
                if (state.Value.State is DungeonRunState.Prepare or DungeonRunState.Process)
                {
                    if (_dungeon is not null && _dungeon != state.Value.DungeonId) Close(stamp);
                    if (_dungeon is null)
                    {
                        // Retain the observed run anchor, but publish no empty history before a measured pull.
                        _dungeon = state.Value.DungeonId;
                        _dungeonStamp = stamp;
                        _runConfirmed = false;
                    }
                }
                else if (state.Value.State == DungeonRunState.End && _dungeon == state.Value.DungeonId) Close(stamp);
                if (_dungeon == state.Value.DungeonId) _dungeonState = state.Value.State;
                break;
            case CombatWireNpcAppearance npc when _dungeon is { } dungeon:
                // Every new appearance is a new lifetime, even when the numeric entity key repeats.
                if (_selected?.Target.EntityId == Number(npc.Value.EntityKey))
                {
                    Interrupt(stamp, CombatGapReason.UnknownAttribution);
                    return;
                }
                _bindings.Remove(npc.Value.EntityKey);
                var rule = _catalog.Find(dungeon, _world.MapDataId, npc.Value.NpcDataId);
                if (rule is null) return;
                // A replacement lifetime invalidates a held terminal candidate even after departure.
                if (_death is not null && _selected?.Rule == rule)
                {
                    Interrupt(stamp, CombatGapReason.UnknownAttribution);
                    return;
                }
                if (_bindings.Count >= 16 || npc.Value.ParentEntityKey is not (null or 0)) return;
                // Duplicate candidates are ambiguous. Never select the first clone by arrival order.
                if (_bindings.Values.Any(b => b.Rule == rule))
                {
                    Interrupt(stamp, CombatGapReason.UnknownAttribution);
                    return;
                }
                _bindings[npc.Value.EntityKey] = new(rule, Entity(npc.Value.EntityKey), input.Sequence)
                    { MaximumHp = npc.Value.MaximumHp };
                if (!_bindTargets(input.Context, _bindings.Keys)) Interrupt(stamp, CombatGapReason.UnknownAttribution);
                break;
            case CombatWireHit hit:
                Hit(stamp, hit.Value);
                break;
            case CombatWireMemberStatus status:
                _members = _members.Select(m => m.CharacterId == status.Value.CharacterId
                    ? m with { ClassDataId = status.Value.ClassDataId, CombatPower = status.Value.CombatPower } : m).ToArray();
                if (_dungeon is not null && _death is null) _controller.SetRoster(stamp, Participants());
                break;
            case CombatWireHp hp when _death is null && _bindings.TryGetValue(hp.Value.EntityId, out var hpBinding):
                // Max-only updates change the lifetime's baseline, but do not manufacture a current-HP point.
                hpBinding.MaximumHp = hp.Value.Maximum ?? hpBinding.MaximumHp;
                if (hp.Value.Current is { } current)
                {
                    // A stale maximum cannot constrain a newer observed current value.
                    if (current > hpBinding.MaximumHp) hpBinding.MaximumHp = null;
                    _controller.Observe(stamp, new CombatHpEvent(hpBinding.Target, current, hpBinding.MaximumHp));
                }
                break;
            case CombatWireDeath death:
                var member = Member(death.Value.EntityKey);
                if (member is not null && _death is null) _controller.Observe(stamp, new CombatDeathEvent(Identity(member)));
                else if (_selected is not null && _selected.Target.EntityId == Number(death.Value.EntityKey) &&
                    _controller.ActiveAttemptId is { } attempt)
                {
                    // Freeze ingestion at the candidate terminal death. No result until the separate proof arrives.
                    _death ??= stamp;
                    _deathAttempt ??= attempt;
                }
                break;
            case CombatWireNpcDeparture gone when _bindings.Remove(gone.Value.EntityKey, out var binding):
                if (binding == _selected && _death is null) Interrupt(stamp, CombatGapReason.UnknownAttribution);
                break;
            case CombatWireMiddleBossReward reward:
                if (_selected?.Rule is { VictoryProof: CombatBossVictoryProof.MiddleBossReward } rewardRule &&
                    rewardRule.NpcDataId == reward.Value.NpcDataId) Victory(stamp);
                else _ignoredEndSignals++;
                break;
            case CombatWireDungeonResult result:
                if (_dungeon != result.Value.DungeonId) { _ignoredEndSignals++; return; }
                if (result.Value.Succeeded && _selected?.Rule.VictoryProof == CombatBossVictoryProof.DungeonSuccess) Victory(stamp);
                // A failed dungeon result does not prove that the active boss attempt was a wipe.
                Close(stamp);
                break;
        }
    }

    public void Boundary(CombatNetworkBoundary input)
    {
        if (_context is null || input.Context is not null && input.Context != _context) return;
        _lastBoundary = input.Code;
        if (_lastStamp is { } last)
        {
            var ticks = Math.Max(input.ReceivedElapsedTicks ?? last.ElapsedTicks, last.ElapsedTicks);
            var stamp = input.Sequence <= last.Sequence ? last : new CombatControllerStamp(input.Sequence, ticks,
                input.CapturedAtUtc ?? last.CapturedAtUtc.AddTicks(ticks - last.ElapsedTicks), _context);
            stamp = stamp with { IsCaptureTimestampObserved = input.Sequence > last.Sequence && input.CapturedAtUtc is not null };
            _controller.Interrupt(stamp,
                input.Code == "QueueOverflow" ? CombatGapReason.QueueOverflow : CombatGapReason.Transport);
            if (_controller.ActiveAttemptId is not null)
                throw new InvalidOperationException("Source authority was revoked but the active attempt could not be interrupted.");
        }
        Reset();
    }

    private void Hit(CombatControllerStamp stamp, CombatHitObservation hit)
    {
        var source = Member(hit.SourceEntityId);
        var targetMember = Member(hit.TargetEntityId);
        var boss = _bindings.GetValueOrDefault(hit.TargetEntityId);
        if (_death is not null)
        {
            if (source is not null && (boss is not null || _selected?.Target.EntityId == Number(hit.TargetEntityId)))
                Interrupt(stamp, CombatGapReason.UnknownAttribution);
            return;
        }
        if (source is null || boss is null && targetMember is null) return;
        var classification = _amounts?.Classify(hit, boss?.Rule.AmountEvidenceScope ?? _selected?.Rule.AmountEvidenceScope);
        if (boss is not null && classification is { Kind: CombatAmountKind.Damage, Amount: > 0 })
        {
            if (_selected is not null && boss != _selected && _controller.ActiveAttemptId is not null)
            { Interrupt(stamp, CombatGapReason.UnknownAttribution); return; }
            if (!_runConfirmed)
            {
                if (_dungeonStamp is null || _dungeon is null ||
                    !_controller.ConfirmDungeon(_dungeonStamp, Number(_dungeon.Value), null)) return;
                _controller.SetRoster(stamp, Participants());
                _runConfirmed = true;
            }
            _selected = boss;
            // Partial effect facts cannot promise complete damage or healing coverage.
            var definition = boss.Rule.Encounter with
            {
                Capabilities = boss.Rule.Encounter.Capabilities & ~(CombatCapabilities.Damage | CombatCapabilities.Healing),
                MeasurementProfile = boss.Rule.Encounter.MeasurementProfile with
                {
                    CatalogVersion = boss.Rule.Encounter.MeasurementProfile.CatalogVersion ?? boss.Rule.EvidenceId,
                    RulesetVersion = boss.Rule.Encounter.MeasurementProfile.RulesetVersion + "|amount:" + _amounts!.EvidenceId
                }
            };
            _controller.BindEncounter(stamp, definition, [boss.Target]);
            _controller.Observe(stamp, new CombatDamageEvent(Identity(source), boss.Target,
                Skill(hit), classification.Amount.Value, IsPeriodic: hit.IsPeriodic) { ObservedValues = ObservedValues(hit) });
        }
        else if (targetMember is not null && classification is { Kind: CombatAmountKind.Healing, Amount: { } amount })
            _controller.Observe(stamp, new CombatHealEvent(Identity(source), Identity(targetMember), Skill(hit), amount, hit.IsPeriodic) { ObservedValues = ObservedValues(hit) });
        else
        {
            _unknownAmounts++;
            _controller.Observe(stamp, new CombatUnclassifiedAmountEvent(Identity(source),
                targetMember is null ? null : Identity(targetMember), boss?.Target,
                hit.SkillDataId is { } skill ? Number(skill) : null, Number(hit.EffectDataId), hit.Amount, hit.IsPeriodic) { ObservedValues = ObservedValues(hit) });
        }
    }

    private static CombatObservedHitValues? ObservedValues(CombatHitObservation hit)
    {
        var block = hit.DirectFields?.HitBlockValue;
        var direct = hit.DirectFields?.Flag400Value;
        var periodic = hit.PeriodicFields?.Flag1Value;
        return block is null && direct is null && periodic is null ? null : new(block, direct, periodic);
    }

    private void Victory(CombatControllerStamp stamp)
    {
        if (_death is null || _deathAttempt is null || _selected is null ||
            !_controller.ConfirmEnd(stamp, _deathAttempt.Value, CombatEndReason.Victory, _death.ElapsedTicks))
        { _ignoredEndSignals++; return; }
        _endProofs++;
        _bindings.Remove(int.Parse(_selected.Target.EntityId, CultureInfo.InvariantCulture));
        _selected = null; _death = null; _deathAttempt = null;
    }

    private void Close(CombatControllerStamp stamp)
    {
        if (_runConfirmed) _controller.CloseDungeon(stamp);
        _dungeonStamp = null; _dungeonState = null; _runConfirmed = false;
        _dungeon = null; _bindings.Clear(); _selected = null; _death = null; _deathAttempt = null;
        _bindTargets(stamp.Context, []);
    }
    private void Interrupt(CombatControllerStamp stamp, CombatGapReason reason)
    {
        _controller.Interrupt(stamp, reason);
        _bindTargets(stamp.Context, []);
        Reset();
    }
    private void Reset()
    {
        _context = null; _world = null; _members = []; _dungeon = null; _lastStamp = null;
        _dungeonStamp = null; _dungeonState = null; _runConfirmed = false;
        _bindings.Clear(); _selected = null; _death = null; _deathAttempt = null;
    }
    private CombatPartyMember? Member(int entity) => _members.SingleOrDefault(m => m.EntityKey == entity);
    private CombatParticipant[] Participants() => _members.Select(m => new CombatParticipant(Identity(m), m.Name,
        Number(m.ClassDataId), m.CombatPower, m.EntityKey == _world!.Identity.EntityKey)).ToArray();
    private CombatEntityReference Entity(int key) => new(Number(key), _context!.Generation, _context.StreamId);
    private static CombatControllerStamp Stamp(CombatNetworkObservation input) =>
        new(input.Sequence, input.ReceivedElapsedTicks, input.CapturedAtUtc, input.Context);
    private static string Identity(CombatPartyMember member) => $"character:{Number(member.BirthServerId)}:{Number(member.CharacterId)}";
    private static string Skill(CombatHitObservation hit) => hit.SkillDataId is { } skill ? Number(skill) : $"effect:{Number(hit.EffectDataId)}";
    private static string Number<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);
}
