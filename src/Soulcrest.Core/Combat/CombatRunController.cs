namespace Soulcrest.Core.Combat;

public sealed record CombatControllerStamp(long Sequence, long ElapsedTicks, DateTimeOffset CapturedAtUtc,
    CombatSourceContext Context)
{
    public bool IsCaptureTimestampObserved { get; init; } = true;
}

/// <summary>The caller independently confirms this definition and every bound entity; no catalog inference lives here.</summary>
public sealed record CombatEncounterDefinition(string BossKey, string? EncounterVariantKey, CombatAttemptKind Kind,
    CombatCapabilities Capabilities, CombatMeasurementProfile MeasurementProfile);

public sealed record CombatRunControllerOptions
{
    public TimeSpan ResultDisplayDuration { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxEventsPerAttempt { get; init; } = 50_000;
    public int MaxEventsPerRun { get; init; } = 200_000;
    public int MaxAttemptsPerRun { get; init; } = 64;
    public int MaxPendingRuns { get; init; } = 4;
}

/// <summary>Documents are defensive deep copies. Mutating an array here cannot rewrite controller history.</summary>
public sealed record CombatRunControllerSnapshot(long Revision, CombatRunDocument? CurrentRun, Guid? ActiveAttemptId,
    CombatAttemptDocument? DisplayAttempt, long? DisplayDurationTicks, bool IsResultVisible,
    long? ResultRemainingTicks, bool HasFreshRoster, bool HasBoundEncounter, string? LimitReason);

/// <summary>
/// Pure bounded run/attempt state. It performs no I/O and owns no wall clock, timer, decoder or UI.
/// Only a positive, confirmed group damage event on an explicitly bound boss starts an attempt.
/// CaptureUTC is preserved on events; run metadata uses a UTC anchor plus monotone elapsed time.
/// </summary>
public sealed class CombatRunController
{
    private sealed class Attempt(CombatAttemptDocument document, long startedTicks)
    {
        public CombatAttemptDocument Document = document;
        public readonly long StartedTicks = startedTicks;
        public readonly List<CombatEventEnvelope> Events = [];
    }
    private sealed class Run(CombatRunDocument document, long startedTicks, CombatSourceContext context)
    {
        public CombatRunDocument Document = document;
        public readonly long StartedTicks = startedTicks;
        public CombatSourceContext ControlContext = context;
        public readonly List<Attempt> Attempts = [];
        public int EventCount;
    }
    private sealed record Binding(CombatEncounterDefinition Definition, CombatEntityReference[] Targets);
    private readonly object _gate = new();
    private readonly CombatRunControllerOptions _options;
    private readonly Func<Guid> _newId;
    private readonly Dictionary<Guid, Run> _pending = [];
    private Run? _run;
    private Attempt? _active, _result;
    private Binding? _binding;
    private CombatParticipant[]? _roster;
    private CombatSourceContext? _context;
    private CombatControllerStamp? _lastInput;
    private CombatGapReason? _pendingQuality;
    private bool _dungeonConfirmed;
    private long _revision, _eventSequence, _nowTicks, _resultShownAtTicks, _lastEndInputSequence;
    private TimeSpan _resultDuration;
    private string? _limitReason;

    public CombatRunController(CombatRunControllerOptions? options = null, Func<Guid>? newId = null)
    {
        _options = (options ?? new()) with { };
        if (_options.ResultDisplayDuration < TimeSpan.Zero || _options.ResultDisplayDuration > TimeSpan.FromDays(1) ||
            _options.MaxEventsPerAttempt < 4 || _options.MaxEventsPerRun < 4 ||
            _options.MaxAttemptsPerRun < 1 || _options.MaxPendingRuns < 2)
            throw new ArgumentOutOfRangeException(nameof(options));
        _resultDuration = _options.ResultDisplayDuration;
        _newId = newId ?? Guid.NewGuid;
    }

    public CombatRunControllerSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                var visible = _active is null && _result is not null && ResultRemaining() > 0;
                var display = _active ?? (visible ? _result : null);
                return new(_revision, _run is null ? null : Document(_run), _active?.Document.Id,
                    display is null ? null : Document(display), display is null ? null :
                    display.Document.DurationTicks ?? Math.Max(0, _nowTicks - display.StartedTicks),
                    visible, visible ? ResultRemaining() : null, _roster is not null && _dungeonConfirmed,
                    _binding is not null && _dungeonConfirmed, _limitReason);
            }
        }
    }

    public Guid? ActiveAttemptId { get { lock (_gate) return _active?.Document.Id; } }

    /// <summary>At most MaxPendingRuns latest checkpoints; callers choose their own persistence scheduling.</summary>
    public IReadOnlyList<CombatRunDocument> DrainCheckpoints()
    {
        lock (_gate)
        {
            var result = _pending.Values.Select(Document).ToArray();
            _pending.Clear();
            return result;
        }
    }

    public bool ConfirmDungeon(CombatControllerStamp stamp, string key, string? confirmedInstanceKey)
    {
        Text(key); if (confirmedInstanceKey is not null) Text(confirmedInstanceKey);
        lock (_gate)
        {
            if (!Accept(stamp)) return false;
            var same = _run is { Document.EndedAtUtc: null } && _run.Document.DungeonKey == key &&
                _run.Document.InstanceKey == confirmedInstanceKey;
            if (same && _dungeonConfirmed && _context == stamp.Context) return false;
            var resume = same && confirmedInstanceKey is not null;
            if (!resume)
            {
                // No old checkpoint may disappear while an adapter is behind on persistence.
                var required = _pending.Count + (_run is not null && !_pending.ContainsKey(_run.Document.Id) ? 1 : 0) + 1;
                if (required > _options.MaxPendingRuns)
                    throw new InvalidOperationException("Drain combat checkpoints before accepting another run.");
            }
            if (_active is not null) EndInterrupted(stamp, CombatGapReason.Transport);
            if (!resume)
            {
                if (_run is { Document.EndedAtUtc: null }) CloseRun(stamp.ElapsedTicks);
                var anchor = stamp.CapturedAtUtc;
                _run = new(new() { Id = Id(), DungeonKey = key, InstanceKey = confirmedInstanceKey,
                    StartedAtUtc = anchor }, stamp.ElapsedTicks, stamp.Context);
                Dirty();
            }
            _context = stamp.Context;
            _run!.ControlContext = stamp.Context;
            _dungeonConfirmed = true;
            _roster = null;
            _binding = null;
            _pendingQuality = null;
            _limitReason = null;
            Changed();
            return true;
        }
    }

    public bool SetRoster(CombatControllerStamp stamp, IReadOnlyList<CombatParticipant> participants)
    {
        var roster = Roster(participants);
        lock (_gate)
        {
            if (!InContext(stamp) || !Accept(stamp)) return false;
            if (_roster is not null && _roster.SequenceEqual(roster)) return false;
            if (_active is not null && !Append(stamp, new CombatRosterEvent(roster))) return false;
            _roster = roster;
            Changed();
            return true;
        }
    }

    public bool BindEncounter(CombatControllerStamp stamp, CombatEncounterDefinition definition,
        IReadOnlyList<CombatEntityReference> confirmedBossTargets)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Text(definition.BossKey);
        if (definition.EncounterVariantKey is not null) Text(definition.EncounterVariantKey);
        ArgumentNullException.ThrowIfNull(definition.MeasurementProfile);
        Text(definition.MeasurementProfile.RulesetVersion);
        const CombatCapabilities all = CombatCapabilities.Damage | CombatCapabilities.Healing |
            CombatCapabilities.SkillCasts | CombatCapabilities.Deaths | CombatCapabilities.Buffs;
        if (!Enum.IsDefined(definition.Kind) || (definition.Capabilities & ~all) != 0)
            throw new ArgumentException("Invalid encounter definition.", nameof(definition));
        ArgumentNullException.ThrowIfNull(confirmedBossTargets);
        var targets = confirmedBossTargets.ToArray();
        if (targets.Length is < 1 or > 64 || targets.Distinct().Count() != targets.Length)
            throw new ArgumentException("A bounded unique set of confirmed boss entities is required.", nameof(confirmedBossTargets));
        foreach (var target in targets)
        {
            ArgumentNullException.ThrowIfNull(target); Text(target.EntityId);
            if (target.StreamId != stamp.Context.StreamId || target.Generation != stamp.Context.Generation)
                throw new ArgumentException("Boss entity belongs to another source context.", nameof(confirmedBossTargets));
        }
        lock (_gate)
        {
            if (!InContext(stamp) || !Accept(stamp) || stamp.Sequence <= _lastEndInputSequence) return false;
            if (_active is not null)
            {
                if (_binding?.Definition != definition) return false;
                var combined = _active.Document.BossTargets.Concat(targets).Distinct().ToArray();
                if (combined.Length > 64) throw new ArgumentException("Too many confirmed boss entities.", nameof(confirmedBossTargets));
                if (combined.Length == _active.Document.BossTargets.Length) return false;
                _active.Document = _active.Document with { BossTargets = combined };
                _binding = new(definition, combined);
                Dirty(); Changed(); return true;
            }
            if (_binding is not null && _binding.Definition == definition && _binding.Targets.SequenceEqual(targets)) return false;
            _binding = new(definition with { MeasurementProfile = definition.MeasurementProfile with { } }, targets);
            Changed(); return true;
        }
    }

    public bool Observe(CombatControllerStamp stamp, CombatEvent observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation is CombatRosterEvent roster) return SetRoster(stamp, roster.Members);
        if (observation is CombatGapEvent gap) return Interrupt(stamp, gap.Reason);
        lock (_gate)
        {
            if (!InContext(stamp) || !Accept(stamp) || _binding is null || _roster is null) return false;
            bool Member(string? id) => id is not null && _roster.Any(member => member.ParticipantId == id);
            bool Boss(CombatEntityReference entity) => _binding.Targets.Contains(entity);
            var eligible = observation switch
            {
                CombatDamageEvent hit => hit.Amount >= 0 && ValidText(hit.SkillId) && Boss(hit.Target) &&
                    Member(hit.ConfirmedOwnerParticipantId ?? hit.SourceParticipantId),
                CombatHealEvent heal => heal.Amount >= 0 && ValidText(heal.SkillId) &&
                    Member(heal.SourceParticipantId) && Member(heal.TargetParticipantId),
                CombatCastEvent cast => Member(cast.SourceParticipantId) && ValidText(cast.SkillId),
                CombatDeathEvent death => Member(death.ParticipantId),
                CombatUnclassifiedAmountEvent unknown => Member(unknown.SourceParticipantId) &&
                    ValidText(unknown.EffectId) && (unknown.SkillId is null || ValidText(unknown.SkillId)) && unknown.Amount is null or >= 0 &&
                    ((unknown.TargetParticipantId is not null && unknown.TargetEntity is null && Member(unknown.TargetParticipantId)) ||
                     (unknown.TargetParticipantId is null && unknown.TargetEntity is not null && Boss(unknown.TargetEntity))),
                CombatHpEvent hp => hp.Current >= 0 && (hp.Maximum is not { } maximum || maximum > 0 && hp.Current <= maximum) && Boss(hp.Target),
                CombatBuffEvent buff => ValidText(buff.BuffId) && Enum.IsDefined(buff.Action) &&
                    buff.StackCount is null or >= 0 && buff.Level is null or >= 0 &&
                    ((buff.TargetParticipantId is not null && buff.TargetEntity is null && Member(buff.TargetParticipantId)) ||
                     (buff.TargetParticipantId is null && buff.TargetEntity is not null && Boss(buff.TargetEntity))),
                _ => false
            };
            if (!eligible) return false;
            if (_active is null)
            {
                if (observation is not CombatDamageEvent { Amount: > 0 }) return false;
                if (_run!.Attempts.Count >= _options.MaxAttemptsPerRun) return Limit("attempt-limit");
                if (_run.EventCount >= _options.MaxEventsPerRun - 3) return Limit("run-event-limit");
                var definition = _binding.Definition;
                _active = new(new() { Id = Id(), BossKey = definition.BossKey,
                    EncounterVariantKey = definition.EncounterVariantKey, Kind = definition.Kind,
                    Capabilities = definition.Capabilities, MeasurementProfile = definition.MeasurementProfile with { },
                    BossTargets = _binding.Targets.ToArray(), Participants = _roster.ToArray(),
                    StartedAtUtc = MetadataAt(_run, stamp.ElapsedTicks) }, stamp.ElapsedTicks);
                _run.Attempts.Add(_active);
                _result = null;
                if (_pendingQuality is { } quality)
                {
                    _active.Document = _active.Document with { HasIncompleteJournal = true };
                    Add(stamp, new CombatGapEvent(quality));
                    Add(stamp, new CombatRosterEvent(_roster.ToArray()));
                    _pendingQuality = null;
                }
            }
            if (observation is CombatBuffEvent effect && effect.SourceParticipantId is not null && !Member(effect.SourceParticipantId))
                observation = effect with { SourceParticipantId = null };
            return Append(stamp, observation);
        }
    }

    /// <summary>An exact attempt and explicit end proof are required; no HP, death, timeout or disappearance inference.</summary>
    public bool ConfirmEnd(CombatControllerStamp stamp, Guid exactAttemptId, CombatEndReason reason,
        long? confirmedEndElapsedTicks = null)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        lock (_gate)
        {
            if (!InContext(stamp) || !Accept(stamp) || _active?.Document.Id != exactAttemptId) return false;
            var end = confirmedEndElapsedTicks ?? stamp.ElapsedTicks;
            if (end < _active.StartedTicks || end > stamp.ElapsedTicks ||
                _active.Events.Any(item => item.OffsetTicks > end - _active.StartedTicks)) return false;
            if (reason == CombatEndReason.Interrupted)
            {
                if (end != stamp.ElapsedTicks) return false;
                return EndInterrupted(stamp, CombatGapReason.Transport);
            }
            End(stamp, end, reason);
            return true;
        }
    }

    /// <summary>Authority loss ends only the active attempt; confirmed instances may resume after fresh confirmation.</summary>
    public bool Interrupt(CombatControllerStamp stamp, CombatGapReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        lock (_gate)
        {
            if (!CanControl(stamp) || !Accept(stamp)) return false;
            var changed = _dungeonConfirmed || _binding is not null || _roster is not null || _active is not null;
            if (_active is not null) EndInterrupted(stamp, reason);
            else Revoke();
            if (_run is { Document.EndedAtUtc: null, Document.InstanceKey: null }) CloseRun(stamp.ElapsedTicks);
            if (changed) Changed();
            return changed;
        }
    }

    /// <summary>Measurement quality only. The gap revokes roster attribution until SetRoster explicitly restores it.</summary>
    public bool MarkIncomplete(CombatControllerStamp stamp, CombatGapReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        lock (_gate)
        {
            if (!InContext(stamp) || !Accept(stamp)) return false;
            if (_active is null) _pendingQuality = reason;
            else
            {
                _active.Document = _active.Document with { HasIncompleteJournal = true };
                if (!Append(stamp, new CombatGapEvent(reason))) return false;
                _roster = null;
            }
            Changed(); return true;
        }
    }

    public bool CloseDungeon(CombatControllerStamp stamp)
    {
        lock (_gate)
        {
            if (!CanControl(stamp) || !Accept(stamp) || _run is null || _run.Document.EndedAtUtc is not null) return false;
            if (_active is not null) EndInterrupted(stamp, CombatGapReason.Transport);
            CloseRun(stamp.ElapsedTicks);
            Revoke(); Changed(); return true;
        }
    }

    /// <summary>A dungeon result closes the run, but cannot manufacture a particular boss's victory or wipe.</summary>
    public bool ConfirmedDungeonResult(CombatControllerStamp stamp, bool succeeded) => CloseDungeon(stamp);

    public bool Advance(long elapsedTicks)
    {
        if (elapsedTicks < 0) throw new ArgumentOutOfRangeException(nameof(elapsedTicks));
        lock (_gate)
        {
            if (elapsedTicks <= _nowTicks) return false;
            _nowTicks = elapsedTicks;
            if (_result is not null && ResultRemaining() == 0) { _result = null; Changed(); return true; }
            if (_active is not null) { Changed(); return true; }
            return false;
        }
    }

    public void SetResultDisplayDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_gate) { if (_resultDuration != duration) { _resultDuration = duration; Changed(); } }
    }

    private bool Accept(CombatControllerStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp); ArgumentNullException.ThrowIfNull(stamp.Context);
        Text(stamp.Context.StreamId);
        if (stamp.Sequence <= 0 || stamp.ElapsedTicks < 0 || stamp.CapturedAtUtc == default ||
            stamp.CapturedAtUtc.Offset != TimeSpan.Zero || stamp.Context.Generation < 0)
            throw new ArgumentException("Invalid combat controller stamp.", nameof(stamp));
        if (_lastInput is { } last && (stamp.Sequence < last.Sequence || stamp.ElapsedTicks < last.ElapsedTicks ||
            stamp.Sequence == last.Sequence && stamp with { IsCaptureTimestampObserved = last.IsCaptureTimestampObserved } != last)) return false;
        _lastInput = stamp;
        _nowTicks = Math.Max(_nowTicks, stamp.ElapsedTicks);
        return true;
    }

    private bool InContext(CombatControllerStamp stamp) => stamp is not null && _dungeonConfirmed && _context == stamp.Context &&
        _run is { Document.EndedAtUtc: null };
    private bool CanControl(CombatControllerStamp stamp) => stamp is not null &&
        (_context == stamp.Context || _context is null && _run?.ControlContext == stamp.Context);

    private bool Append(CombatControllerStamp stamp, CombatEvent observation)
    {
        if (_active!.Events.Count >= _options.MaxEventsPerAttempt - 1 || _run!.EventCount >= _options.MaxEventsPerRun - 1)
        {
            _limitReason = "event-limit";
            EndInterrupted(stamp, CombatGapReason.QueueOverflow);
            if (_run!.Document.InstanceKey is null) CloseRun(stamp.ElapsedTicks);
            return false;
        }
        Add(stamp, observation); Dirty(); Changed(); return true;
    }

    private void Add(CombatControllerStamp stamp, CombatEvent observation)
    {
        _active!.Events.Add(new(++_eventSequence, stamp.ElapsedTicks - _active.StartedTicks, stamp.CapturedAtUtc,
            _context!, Clone(observation)) { IsCaptureTimestampObserved = stamp.IsCaptureTimestampObserved });
        _run!.EventCount++;
    }

    private bool EndInterrupted(CombatControllerStamp stamp, CombatGapReason reason)
    {
        if (_active is null) return false;
        _active.Document = _active.Document with { HasIncompleteJournal = true };
        // The last slot is reserved for an explicit overflow/transport gap, even at the event limit.
        Add(stamp, new CombatGapEvent(reason));
        End(stamp, stamp.ElapsedTicks, CombatEndReason.Interrupted);
        Revoke();
        return true;
    }

    private void End(CombatControllerStamp stamp, long endTicks, CombatEndReason reason)
    {
        _active!.Document = _active.Document with { DurationTicks = endTicks - _active.StartedTicks, EndReason = reason };
        _result = _active;
        _resultShownAtTicks = stamp.ElapsedTicks;
        _active = null;
        _binding = null;
        _lastEndInputSequence = stamp.Sequence;
        Dirty(); Changed();
    }

    private void Revoke() { _dungeonConfirmed = false; _context = null; _binding = null; _roster = null; _pendingQuality = null; }
    private void CloseRun(long atTicks) { _run!.Document = _run.Document with { EndedAtUtc = MetadataAt(_run, atTicks) }; Dirty(); }
    private static DateTimeOffset MetadataAt(Run run, long elapsedTicks) => run.Document.StartedAtUtc.AddTicks(elapsedTicks - run.StartedTicks);
    private long ResultRemaining() => _result is null ? 0 : Math.Max(0, _resultDuration.Ticks - Math.Max(0, _nowTicks - _resultShownAtTicks));
    private void Changed() => _revision++;
    private void Dirty() { _pending[_run!.Document.Id] = _run; }
    private bool Limit(string reason) { _limitReason = reason; Changed(); return false; }
    private Guid Id()
    {
        var id = _newId();
        if (id == Guid.Empty || _pending.ContainsKey(id) || _run?.Document.Id == id || _run?.Attempts.Any(a => a.Document.Id == id) == true)
            throw new InvalidOperationException("Combat identity factory returned an empty or duplicate identity.");
        return id;
    }
    private static void Text(string value)
    {
        if (!ValidText(value))
            throw new ArgumentException("A bounded nonempty combat key is required.");
    }
    private static bool ValidText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);
    private static CombatParticipant[] Roster(IReadOnlyList<CombatParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count > 5) throw new ArgumentException("At most five current group slots are supported.", nameof(participants));
        var copy = participants.ToArray();
        foreach (var member in copy)
        {
            ArgumentNullException.ThrowIfNull(member); Text(member.ParticipantId);
            if (member.CombatPower < 0) throw new ArgumentException("Negative combat power.", nameof(participants));
            if (member.Name is { Length: > 128 }) throw new ArgumentException("Unbounded participant name.", nameof(participants));
            if (member.ClassKey is not null) Text(member.ClassKey);
        }
        if (copy.Select(member => member.ParticipantId).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Duplicate group identities.", nameof(participants));
        return copy;
    }
    private static CombatEvent Clone(CombatEvent item) => item is CombatRosterEvent roster
        ? roster with { Members = roster.Members.ToArray() } : item;
    private static CombatAttemptDocument Document(Attempt attempt) => attempt.Document with
    {
        BossTargets = attempt.Document.BossTargets.ToArray(), Participants = attempt.Document.Participants.ToArray(),
        Events = attempt.Events.Select(item => item with { Event = Clone(item.Event) }).ToArray()
    };
    private static CombatRunDocument Document(Run run) => run.Document with { Attempts = run.Attempts.Select(Document).ToArray() };
}
