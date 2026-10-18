namespace Soulcrest.Core.Combat;

/// <summary>Deterministic projections of locally observed events; the same path is used for replay and live views.</summary>
public static class CombatProjection
{
    public static CombatAttemptSummary Project(CombatAttemptDocument attempt, long? asOfTicks = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var issues = new List<CombatQualityIssue>();
        var duration = attempt.DurationTicks ?? asOfTicks;
        if (duration is < 0) throw new ArgumentOutOfRangeException(nameof(attempt), "Negative combat duration.");
        if (asOfTicks is < 0) throw new ArgumentOutOfRangeException(nameof(asOfTicks));
        var horizon = duration ?? long.MaxValue;
        var players = new Dictionary<string, Player>(StringComparer.Ordinal);
        var current = new HashSet<string>(StringComparer.Ordinal);
        var buffs = new Dictionary<BuffKey, Buff>();
        var hp = new List<CombatHpPoint>();
        var maximumHp = new Dictionary<CombatEntityReference, long>();
        var timeline = new List<CombatEventEnvelope>();
        var bosses = attempt.BossTargets.ToHashSet();
        var sources = bosses.Select(b => new CombatSourceContext(b.StreamId, b.Generation)).ToHashSet();
        long previousOffset = 0;
        SetRoster(attempt.Participants, null);

        var sequences = new HashSet<long>();
        foreach (var envelope in attempt.Events.OrderBy(e => e.Sequence))
        {
            if (envelope.SchemaVersion != 1) { Issue("unsupported-event-schema", envelope.Sequence); continue; }
            if (envelope.Sequence <= 0 || envelope.OffsetTicks < 0 || envelope.Source.Generation < 0)
            { Issue("invalid-event-context", envelope.Sequence); continue; }
            if (!sequences.Add(envelope.Sequence)) { Issue("duplicate-sequence", envelope.Sequence); continue; }
            if (envelope.OffsetTicks > horizon) { Issue("event-after-end", envelope.Sequence); continue; }
            if (envelope.OffsetTicks < previousOffset) { Issue("non-monotonic-event-time", envelope.Sequence); continue; }
            previousOffset = envelope.OffsetTicks;
            if (!sources.Contains(envelope.Source))
            { Issue("unbound-source-context", envelope.Sequence); continue; }
            var at = envelope.OffsetTicks;
            switch (envelope.Event)
            {
                case CombatRosterEvent roster:
                    if (SetRoster(roster.Members, envelope.Sequence)) timeline.Add(envelope);
                    break;
                case CombatDamageEvent damage:
                    var owner = damage.ConfirmedOwnerParticipantId ?? damage.SourceParticipantId;
                    if (!IsBoss(damage.Target, envelope)) break;
                    if (owner is null) { Issue("unknown-damage-attribution", envelope.Sequence); break; }
                    if (!current.Contains(owner)) break;
                    if (damage.Amount < 0 || string.IsNullOrWhiteSpace(damage.SkillId))
                    { Issue("invalid-damage", envelope.Sequence); break; }
                    var attacker = players[owner];
                    var skill = attacker.Skill(damage.SkillId);
                    try
                    {
                        var total = checked(attacker.Damage + damage.Amount);
                        var skillTotal = checked(skill.Damage + damage.Amount);
                        attacker.Damage = total; skill.Damage = skillTotal;
                        skill.Hits++; if (damage.IsPeriodic) skill.Ticks++;
                        skill.Maximum = Math.Max(skill.Maximum, damage.Amount);
                        skill.Accept(damage.Flags);
                        ObserveValues(damage.ObservedValues, envelope.Sequence);
                        timeline.Add(envelope);
                    }
                    catch (OverflowException) { Issue("damage-overflow", envelope.Sequence); }
                    break;
                case CombatCastEvent cast when current.Contains(cast.SourceParticipantId):
                    if (string.IsNullOrWhiteSpace(cast.SkillId)) { Issue("invalid-cast", envelope.Sequence); break; }
                    players[cast.SourceParticipantId].Skill(cast.SkillId).Casts++;
                    timeline.Add(envelope);
                    break;
                case CombatHealEvent heal when current.Contains(heal.SourceParticipantId) && current.Contains(heal.TargetParticipantId):
                    if (heal.Amount < 0 || string.IsNullOrWhiteSpace(heal.SkillId))
                    { Issue("invalid-heal", envelope.Sequence); break; }
                    var healer = players[heal.SourceParticipantId];
                    var healingSkill = healer.Skill(heal.SkillId);
                    try
                    {
                        var total = checked(healer.Healing + heal.Amount);
                        var skillTotal = checked(healingSkill.Healing + heal.Amount);
                        healer.Healing = total; healingSkill.Healing = skillTotal;
                        ObserveValues(heal.ObservedValues, envelope.Sequence);
                        timeline.Add(envelope);
                    }
                    catch (OverflowException) { Issue("healing-overflow", envelope.Sequence); }
                    break;
                case CombatDeathEvent death when current.Contains(death.ParticipantId):
                    players[death.ParticipantId].Deaths++;
                    timeline.Add(envelope);
                    break;
                case CombatBuffEvent effect:
                    var participantTarget = effect.TargetParticipantId is not null;
                    if (participantTarget == (effect.TargetEntity is not null))
                    { Issue("invalid-buff-target", envelope.Sequence); break; }
                    if (participantTarget ? !current.Contains(effect.TargetParticipantId!) : !IsBoss(effect.TargetEntity!, envelope)) break;
                    if (string.IsNullOrWhiteSpace(effect.BuffId) || effect.StackCount is < 0 || effect.Level is < 0
                        || !Enum.IsDefined(effect.Action)) { Issue("invalid-buff", envelope.Sequence); break; }
                    var key = new BuffKey(effect.BuffId, effect.TargetParticipantId, effect.TargetEntity);
                    if (!buffs.TryGetValue(key, out var buff)) buffs[key] = buff = new();
                    var source = effect.SourceParticipantId is { } sourceId && players.ContainsKey(sourceId) ? sourceId : null;
                    if (!effect.InitialStateKnown) { buff.InitialKnown = false; Issue("unknown-buff-start", envelope.Sequence); }
                    if (effect.Action == CombatBuffAction.Apply) buff.Applications++;
                    if (effect.Action == CombatBuffAction.Remove)
                    {
                        var matching = buff.Active.Keys.Where(s => effect.RemoveAllSources || s == (source ?? "")).ToArray();
                        if (matching.Length == 0) { buff.InitialKnown = false; Issue("unknown-buff-start", envelope.Sequence); }
                        foreach (var active in matching) buff.Close(active, at);
                    }
                    else
                    {
                        var sourceKey = source ?? "";
                        if (effect.Action == CombatBuffAction.Refresh && !buff.Active.ContainsKey(sourceKey))
                        { buff.InitialKnown = false; Issue("unknown-buff-start", envelope.Sequence); }
                        buff.Close(sourceKey, at);
                        buff.Active[sourceKey] = new(at, effect.StackCount, effect.Level);
                    }
                    timeline.Add(envelope with { Event = effect with { SourceParticipantId = source } });
                    break;
                case CombatHpEvent point when IsBoss(point.Target, envelope):
                    if (point.Current < 0) { Issue("invalid-hp", envelope.Sequence); break; }
                    if (point.Maximum is { } max)
                    {
                        if (max <= 0 || point.Current > max) { Issue("invalid-maximum-hp", envelope.Sequence); maximumHp.Remove(point.Target); }
                        else maximumHp[point.Target] = max;
                    }
                    var knownMax = maximumHp.TryGetValue(point.Target, out var known) ? (long?)known : null;
                    if (knownMax < point.Current) { knownMax = null; maximumHp.Remove(point.Target); Issue("invalid-maximum-hp", envelope.Sequence); }
                    hp.Add(new(at, point.Target, point.Current, knownMax));
                    timeline.Add(envelope with { Event = point with { Maximum = knownMax } });
                    break;
                case CombatUnclassifiedAmountEvent unknown when current.Contains(unknown.SourceParticipantId):
                    if ((unknown.TargetParticipantId is null) == (unknown.TargetEntity is null) ||
                        unknown.Amount is < 0 || string.IsNullOrWhiteSpace(unknown.EffectId))
                    { Issue("invalid-unclassified-amount", envelope.Sequence); break; }
                    if (unknown.TargetParticipantId is { } targetId ? !current.Contains(targetId) : !IsBoss(unknown.TargetEntity!, envelope)) break;
                    Issue("amount-semantics-unconfirmed", envelope.Sequence);
                    ObserveValues(unknown.ObservedValues, envelope.Sequence);
                    timeline.Add(envelope);
                    break;
                case CombatGapEvent gap:
                    Issue("gap-" + gap.Reason, envelope.Sequence);
                    foreach (var affected in buffs.Values)
                    {
                        affected.InitialKnown = false;
                        foreach (var active in affected.Active.Keys.ToArray()) affected.Close(active, at);
                    }
                    maximumHp.Clear();
                    current.Clear(); // Attribution resumes only after an explicit roster snapshot following the gap.
                    timeline.Add(envelope);
                    break;
            }
        }

        void ObserveValues(CombatObservedHitValues? values, long sequence)
        {
            if (values is { DirectHitBlockValue: > 0 } or { DirectFlag400Value: > 0 } or { PeriodicFlag1Value: > 0 })
                Issue("auxiliary-amount-semantics-unconfirmed", sequence);
        }

        if (duration is null) Issue("unknown-duration");
        if (attempt.HasIncompleteJournal) Issue("incomplete-journal");
        if (attempt.EndReason == CombatEndReason.Interrupted) Issue("interrupted-attempt");
        if (!attempt.Capabilities.HasFlag(CombatCapabilities.Damage)) Issue("damage-coverage-unknown");
        var totals = players.Values.Select(p => p.Damage).Aggregate(0m, (sum, value) => sum + value);
        var totalDamage = totals <= long.MaxValue ? (long)totals : long.MaxValue;
        if (totals > long.MaxValue) Issue("damage-overflow");
        var seconds = duration is > 0 ? (decimal?)duration.Value / TimeSpan.TicksPerSecond : null;
        var summaries = players.Values.OrderBy(p => p.Participant.ParticipantId, StringComparer.Ordinal).Select(p =>
            new CombatPlayerSummary(p.Participant, p.Damage, Has(CombatCapabilities.Damage) ? Divide(p.Damage, seconds) : null,
                Has(CombatCapabilities.Damage) && totalDamage > 0 ? (decimal)p.Damage / totalDamage : null,
                Has(CombatCapabilities.Healing) ? p.Healing : null,
                Has(CombatCapabilities.Healing) ? Divide(p.Healing, seconds) : null,
                Has(CombatCapabilities.Deaths) ? p.Deaths : null,
                p.Skills.Values.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => new CombatSkillSummary(s.Id,
                    s.Damage, s.Hits, s.Ticks, Has(CombatCapabilities.SkillCasts) ? s.Casts : null,
                    Has(CombatCapabilities.Healing) ? s.Healing : null, s.Hits > 0 ? s.Maximum : null,
                    s.Hits > 0 ? (decimal)s.Damage / s.Hits : null, s.Rates.ToDictionary(pair => pair.Key,
                        pair => new CombatRate(pair.Value.Numerator, pair.Value.Denominator), StringComparer.Ordinal))).ToArray())).ToArray();
        var buffSummaries = buffs.OrderBy(b => b.Key.BuffId, StringComparer.Ordinal)
            .ThenBy(b => b.Key.ParticipantId, StringComparer.Ordinal).ThenBy(b => b.Key.Entity?.EntityId, StringComparer.Ordinal)
            .Select(pair =>
            {
                var intervals = pair.Value.Intervals.ToList();
                if (duration is { } end)
                    intervals.AddRange(pair.Value.Active.Select(active => new CombatBuffInterval(active.Value.Start,
                        end, EmptyToNull(active.Key), active.Value.Stacks, active.Value.Level)));
                var ordered = intervals.Where(i => i.EndTicks > i.StartTicks).OrderBy(i => i.StartTicks)
                    .ThenBy(i => i.EndTicks).ThenBy(i => i.SourceParticipantId, StringComparer.Ordinal).ToArray();
                var activeTicks = UnionDuration(ordered);
                return new CombatBuffSummary(pair.Key.BuffId, pair.Key.ParticipantId, pair.Key.Entity,
                    pair.Value.Applications, activeTicks, Has(CombatCapabilities.Buffs) && duration is > 0 && pair.Value.InitialKnown
                        ? (decimal)activeTicks / duration.Value : null, pair.Value.InitialKnown, ordered);
            }).ToArray();
        return new(attempt.Id, attempt.BossKey, duration, attempt.EndReason, totalDamage,
            Has(CombatCapabilities.Damage) ? Divide(totalDamage, seconds) : null,
            attempt.EndReason is not null && duration is > 0 && issues.Count == 0, issues.ToArray(), summaries,
            buffSummaries, hp.ToArray(), timeline.ToArray());

        bool Has(CombatCapabilities capability) => attempt.Capabilities.HasFlag(capability);
        void Issue(string code, long? sequence = null) => issues.Add(new(code, sequence));
        bool IsBoss(CombatEntityReference target, CombatEventEnvelope envelope)
        {
            if (target.Generation != envelope.Source.Generation || target.StreamId != envelope.Source.StreamId)
            { Issue("stale-entity-generation", envelope.Sequence); return false; }
            return bosses.Contains(target);
        }
        bool SetRoster(CombatParticipant[] roster, long? sequence)
        {
            if (roster.Length > 5 || roster.Any(p => string.IsNullOrWhiteSpace(p.ParticipantId) || p.CombatPower is < 0)
                || roster.Select(p => p.ParticipantId).Distinct(StringComparer.Ordinal).Count() != roster.Length)
            { Issue("invalid-roster", sequence); return false; }
            var next = roster.Select(p => p.ParticipantId).ToHashSet(StringComparer.Ordinal);
            foreach (var leaving in current.Except(next).ToArray())
                foreach (var effect in buffs.Where(b => b.Key.ParticipantId == leaving).Select(b => b.Value))
                {
                    if (effect.Active.Count > 0) Issue("buff-observation-ended", sequence);
                    effect.InitialKnown = false;
                    foreach (var active in effect.Active.Keys.ToArray()) effect.Close(active, previousOffset);
                }
            current.Clear(); current.UnionWith(next);
            foreach (var participant in roster)
                if (players.TryGetValue(participant.ParticipantId, out var existing)) existing.Participant = participant;
                else players[participant.ParticipantId] = new(participant);
            return true;
        }
    }

    private static decimal? Divide(long value, decimal? denominator) => denominator is > 0 ? value / denominator.Value : null;
    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
    private static long UnionDuration(CombatBuffInterval[] intervals)
    {
        long total = 0, start = 0, end = 0;
        foreach (var interval in intervals)
        {
            if (interval.StartTicks > end) { total += end - start; start = interval.StartTicks; end = interval.EndTicks; }
            else end = Math.Max(end, interval.EndTicks);
        }
        return total + end - start;
    }

    private sealed class Player(CombatParticipant participant)
    {
        public CombatParticipant Participant = participant;
        public long Damage, Healing, Deaths;
        public readonly Dictionary<string, Skill> Skills = new(StringComparer.Ordinal);
        public Skill Skill(string id)
        {
            if (!Skills.TryGetValue(id, out var skill)) Skills[id] = skill = new(id);
            return skill;
        }
    }
    private sealed class Skill(string id)
    {
        public string Id { get; } = id;
        public long Damage, Hits, Ticks, Casts, Healing, Maximum;
        public readonly Dictionary<string, (long Numerator, long Denominator)> Rates = new(StringComparer.Ordinal);
        public void Accept(CombatHitFlags? flags)
        {
            if (flags is null) return;
            Add("critical", flags.Critical); Add("front", flags.Front); Add("back", flags.Back);
            Add("perfect", flags.Perfect); Add("crushing", flags.Crushing); Add("multiHit", flags.MultiHit); Add("parried", flags.Parried);
        }
        private void Add(string name, bool? flag)
        {
            if (flag is not { } value) return;
            var rate = Rates.GetValueOrDefault(name);
            Rates[name] = (rate.Numerator + (value ? 1 : 0), rate.Denominator + 1);
        }
    }
    private sealed record BuffKey(string BuffId, string? ParticipantId, CombatEntityReference? Entity);
    private sealed record ActiveBuff(long Start, int? Stacks, int? Level);
    private sealed class Buff
    {
        public long Applications;
        public bool InitialKnown = true;
        public readonly Dictionary<string, ActiveBuff> Active = new(StringComparer.Ordinal);
        public readonly List<CombatBuffInterval> Intervals = [];
        public void Close(string source, long at)
        {
            if (Active.Remove(source, out var active) && at > active.Start)
                Intervals.Add(new(active.Start, at, EmptyToNull(source), active.Stacks, active.Level));
        }
    }
}
