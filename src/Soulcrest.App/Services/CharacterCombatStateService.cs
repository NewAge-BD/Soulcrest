using System.Collections.Immutable;
using System.Diagnostics;
using Soulcrest.App.Network;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public sealed record CharacterCombatPointPoolSnapshot(DaevanionBudgetPool Pool, int? Free = null,
    int? Spent = null, int? Total = null, int? Bonus = null, int? Capacity = null,
    NetworkCombatSourceStatus Status = NetworkCombatSourceStatus.Unknown, string Source = "unknown");

/// <summary>Immutable RAM projection. Local ProfileId is the only character identifier exposed to consumers.</summary>
public sealed record CharacterCombatSnapshot
{
    public string? ProfileId { get; init; }
    public string? ClassId { get; init; }
    public int? CharacterLevel { get; init; }
    public int? CombatLevel { get; init; }
    public int? ConquerorLevel { get; init; }
    public long? CurrentCombatScore { get; init; }
    public bool IsFresh { get; init; }
    public long Generation { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public NetworkCombatDomain<ImmutableDictionary<ushort, int>> NativeStats { get; init; } = NetworkCombatDomain<ImmutableDictionary<ushort, int>>.Unknown;
    public ImmutableDictionary<int, OwnSkillLevel> Skills { get; init; } = ImmutableDictionary<int, OwnSkillLevel>.Empty;
    public NetworkCombatSourceStatus SkillStatus { get; init; } = NetworkCombatSourceStatus.Unknown;
    public DateTimeOffset? SkillsObservedAt { get; init; }
    public NetworkCombatDomain<NetworkCharacterResourceMaximum> HpMaximum { get; init; } = NetworkCombatDomain<NetworkCharacterResourceMaximum>.Unknown;
    public NetworkCombatDomain<NetworkCharacterResourceMaximum> MpMaximum { get; init; } = NetworkCombatDomain<NetworkCharacterResourceMaximum>.Unknown;
    public CharacterDisplayStats DisplayStats { get; init; } = CharacterDisplayStatProjection.Evaluate(new());
    public NetworkCombatDomain<CharacterDaevanionState> ActivatedBoards { get; init; } = NetworkCombatDomain<CharacterDaevanionState>.Unknown;
    public NetworkCombatDomain<CharacterDaevanionPointState> NativePoints { get; init; } = NetworkCombatDomain<CharacterDaevanionPointState>.Unknown;
    public NetworkCombatDomain<CharacterTitleState> Titles { get; init; } = NetworkCombatDomain<CharacterTitleState>.Unknown;
    public NetworkCombatDomain<ImmutableHashSet<int>> TitleOwnership { get; init; } = NetworkCombatDomain<ImmutableHashSet<int>>.Unknown;
    public NetworkCombatDomain<CharacterWingOwnership> WingOwnership { get; init; } = NetworkCombatDomain<CharacterWingOwnership>.Unknown;
    public NetworkCombatDomain<CharacterWingEquipment> EquippedWing { get; init; } = NetworkCombatDomain<CharacterWingEquipment>.Unknown;
    public NetworkCombatDomain<CharacterAchievementState> Achievements { get; init; } = NetworkCombatDomain<CharacterAchievementState>.Unknown;
    public NetworkCombatDomain<CharacterEquipmentState> Equipment { get; init; } = NetworkCombatDomain<CharacterEquipmentState>.Unknown;
    public NetworkCombatDomain<PetProgressSnapshot> Pets { get; init; } = NetworkCombatDomain<PetProgressSnapshot>.Unknown;
    public NetworkCombatDomain<CharacterAbnormalState> Abnormals { get; init; } = NetworkCombatDomain<CharacterAbnormalState>.Unknown;
    public IReadOnlyList<CharacterCombatPointPoolSnapshot> PointPools { get; init; } = [];
    public CombatSourceGraph? Graph { get; init; }
    public CombatEvaluation? Breakdown { get; init; }
    public IReadOnlyList<string> MissingRequirements { get; init; } = [];
    public static CharacterCombatSnapshot Empty { get; } = new();
}

/// <summary>One profile-bound adapter over the existing passive stream. Never reads game files or starts a capture.</summary>
public sealed class CharacterCombatStateService : IDisposable
{
    public const string DerivedPointBudgetSource = "derived-level-base-plus-own-bonus-minus-used-v1";
    private readonly INetworkCharacterCombatSource _source;
    private readonly OwnSkillStateService _skills;
    private readonly ExplorationService? _exploration;
    private readonly CombatEffectCatalog _catalog;
    private readonly Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> _resolve;
    private readonly object _gate = new();
    private CharacterCombatSnapshot _snapshot = CharacterCombatSnapshot.Empty;
    private NetworkCharacterCombatSnapshot? _evidence;
    private OwnSkillStateSnapshot? _skillEvidence;
    private (CharacterCombatSnapshot Snapshot, long Revision)? _pending;
    private long _revision;
    private bool _workerRunning, _disposed;
    public event Action? Changed;

    /// <summary>Current own profile authority only; independent of cached skill/graph projections.</summary>
    internal string? ResolveCurrentOwnProfileId(NetworkCharacterCombatSnapshot expected)
    {
        if (Volatile.Read(ref _disposed) || !_source.Running || expected.Character is not { } own
            || own.Generation <= 0 || expected.Generation != own.Generation
            || !ReferenceEquals(expected, _source.CombatSnapshot)) return null;
        // A new native owner must have reached our own publication boundary. CP-only
        // network revisions keep this same OwnContext and need no graph/skill refresh.
        lock (_gate)
            if (_disposed || _evidence is not { } published || !ReferenceEquals(published.Character, own)
                || published.Generation != own.Generation) return null;
        var profileId = _resolve(own)?.ProfileId;
        if (string.IsNullOrWhiteSpace(profileId) || Volatile.Read(ref _disposed) || !_source.Running
            || !ReferenceEquals(expected, _source.CombatSnapshot) || !ReferenceEquals(expected.Character, own)
            || expected.Generation != own.Generation) return null;
        bool publishedOwn;
        lock (_gate)
            publishedOwn = !_disposed && _evidence is { } witness && ReferenceEquals(witness.Character, own)
                && witness.Generation == own.Generation;
        return publishedOwn && !Volatile.Read(ref _disposed) && _source.Running
            && ReferenceEquals(expected, _source.CombatSnapshot) ? profileId : null;
    }

    public CharacterCombatSnapshot Snapshot
    {
        get
        {
            // Source callbacks can publish a newer adapter revision while a producer read
            // waits for its dispatch gate. Retry that coherent revision instead of rendering
            // Empty from the old local references. Actual loss of authority still returns Empty.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                NetworkCharacterCombatSnapshot? evidence;
                OwnSkillStateSnapshot? skillEvidence;
                CharacterCombatSnapshot result;
                long revision;
                lock (_gate)
                {
                    if (_disposed) return CharacterCombatSnapshot.Empty;
                    evidence = _evidence; skillEvidence = _skillEvidence; result = _snapshot; revision = _revision;
                }
                var valid = _source.Running && evidence?.Character is { } own
                    && ReferenceEquals(evidence, _source.CombatSnapshot)
                    && _resolve(own)?.ProfileId == result.ProfileId && skillEvidence == _skills.Snapshot;
                var sourceStillCurrent = ReferenceEquals(evidence, _source.CombatSnapshot) && _source.Running;
                lock (_gate)
                {
                    if (_disposed) return CharacterCombatSnapshot.Empty;
                    if (revision != _revision || !ReferenceEquals(result, _snapshot)) continue;
                }
                return valid && sourceStillCurrent ? result : CharacterCombatSnapshot.Empty;
            }
            return CharacterCombatSnapshot.Empty;
        }
    }

    public CharacterCombatStateService(INetworkCharacterCombatSource source, OwnSkillStateService skills, ExplorationService exploration)
        : this(source, skills, exploration.TryGetBoundCharacter, exploration: exploration) { }

    internal CharacterCombatStateService(INetworkCharacterCombatSource source, OwnSkillStateService skills,
        Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> resolve, CombatEffectCatalog? catalog = null,
        ExplorationService? exploration = null)
    {
        _source = source; _skills = skills; _resolve = resolve; _catalog = catalog ?? CombatEffectCatalog.Default;
        _exploration = exploration;
        source.Changed += Refresh; source.OwnCharacterChanged += Refresh; skills.Changed += Refresh;
        if (exploration is not null) exploration.Changed += ProfileChanged;
        Refresh();
    }

    /// <summary>Saved observations for the bound own profile, otherwise the selected local profile; always historical.</summary>
    public ProfileTitleSnapshot? SavedTitles
    {
        get
        {
            lock (_gate) if (_disposed) return null;
            if (_exploration is null) return null;
            var running = _source.Running;
            var evidence = _source.CombatSnapshot;
            var own = evidence.Character;
            var profile = running && own is not null ? _resolve(own) : null;
            var profileId = profile?.ProfileId is { Length: > 0 } id && _source.Running
                && ReferenceEquals(evidence, _source.CombatSnapshot) ? id : null;
            var result = _exploration.TryGetSavedTitles(profileId);
            // Neither producer is called under our lock. A character/profile transition
            // during the historical read must not return the previous character's history.
            if (profileId is not null && (own is null || _resolve(own)?.ProfileId != profileId)
                || profileId is null && result is not null && _exploration.ActiveId != result.ProfileId) return null;
            lock (_gate) if (_disposed) return null;
            return ReferenceEquals(evidence, _source.CombatSnapshot) && _source.Running == running ? result : null;
        }
    }

    /// <summary>Saved observations for the bound own profile, otherwise the selected local profile; always historical.</summary>
    public ProfileWingSnapshot? SavedWings
    {
        get
        {
            lock (_gate) if (_disposed) return null;
            if (_exploration is null) return null;
            var running = _source.Running;
            var evidence = _source.CombatSnapshot;
            var own = evidence.Character;
            var profile = running && own is not null ? _resolve(own) : null;
            var profileId = profile?.ProfileId is { Length: > 0 } id && _source.Running
                && ReferenceEquals(evidence, _source.CombatSnapshot) ? id : null;
            var result = _exploration.TryGetSavedWings(profileId);
            // Neither producer is called under our lock. A character/profile transition
            // during the historical read must not return the previous character's history.
            if (profileId is not null && (own is null || _resolve(own)?.ProfileId != profileId)
                || profileId is null && result is not null && _exploration.ActiveId != result.ProfileId) return null;
            lock (_gate) if (_disposed) return null;
            return ReferenceEquals(evidence, _source.CombatSnapshot) && _source.Running == running ? result : null;
        }
    }

    /// <summary>Shared saved rolls with exactly the bound or selected profile's active preset. Never a live domain.</summary>
    public ProfileGenusInsightSnapshot? SavedGenusInsight
    {
        get
        {
            lock (_gate) if (_disposed) return null;
            if (_exploration is null) return null;
            var running = _source.Running;
            var evidence = _source.CombatSnapshot;
            var own = evidence.Character;
            var profile = running && own is not null ? _resolve(own) : null;
            if (running && own is not null && profile is null) return null;
            var profileId = profile?.ProfileId is { Length: > 0 } id && _source.Running
                && ReferenceEquals(evidence, _source.CombatSnapshot) ? id : null;
            var result = _exploration.TryGetSavedGenusInsight(profileId);
            if (profileId is not null && (own is null || _resolve(own)?.ProfileId != profileId)
                || profileId is null && result is not null && _exploration.ActiveId != result.ProfileId) return null;
            lock (_gate) if (_disposed) return null;
            return ReferenceEquals(evidence, _source.CombatSnapshot) && _source.Running == running ? result : null;
        }
    }

    internal bool PersistObservedGenusPreset(CharacterCombatSnapshot expected, byte preset, Func<bool>? sourceStillCurrent = null)
    {
        lock (_gate) if (_disposed) return false;
        if (_exploration is null || !expected.IsFresh || string.IsNullOrWhiteSpace(expected.ProfileId)) return false;
        var evidence = _source.CombatSnapshot;
        if (!_source.Running || evidence.Character is not { } own || own.Generation != expected.Generation
            || evidence.Generation != expected.Generation || evidence.Sequence != expected.Sequence
            || _resolve(own)?.ProfileId != expected.ProfileId
            || !ReferenceEquals(evidence, _source.CombatSnapshot)) return false;
        if (sourceStillCurrent?.Invoke() == false || !ReferenceEquals(evidence, _source.CombatSnapshot)
            || _resolve(own)?.ProfileId != expected.ProfileId) return false;
        return _exploration.ApplyObservedGenusPreset(own, expected.ProfileId, preset, DateTimeOffset.UtcNow);
    }

    /// <summary>Persist already accepted shared rolls only while both their source and this own binding still agree.</summary>
    internal bool PersistObservedSharedGenus(CharacterCombatSnapshot expected, NetworkCombatDomain<PetProgressSnapshot> domain,
        Func<bool> sourceStillCurrent)
    {
        lock (_gate) if (_disposed) return false;
        if (_exploration is null || !expected.IsFresh || string.IsNullOrWhiteSpace(expected.ProfileId)) return false;
        var evidence = _source.CombatSnapshot;
        if (!_source.Running || evidence.Character is not { } own || own.Generation != expected.Generation
            || evidence.Generation != expected.Generation || evidence.Sequence != expected.Sequence
            || _resolve(own)?.ProfileId != expected.ProfileId || !ReferenceEquals(evidence, _source.CombatSnapshot)) return false;
        if (!sourceStillCurrent() || !ReferenceEquals(evidence, _source.CombatSnapshot)
            || _resolve(own)?.ProfileId != expected.ProfileId) return false;
        // Global collection evidence deliberately has no character-local preset authority.
        var shared = domain.Value is { } value ? domain with { Value = value with { SelectedGenusPanel = null } } : domain;
        return _exploration.ApplyNetworkGenusInsight(own, expected.ProfileId, shared);
    }

    private void ProfileChanged()
    {
        lock (_gate) if (_disposed) return;
        Refresh();
        SafeEvent.Raise(Changed, "Gespeicherter Titelbesitz");
    }

    private void Refresh()
    {
        var evidence = _source.CombatSnapshot;
        var own = evidence.Character;
        var profile = _source.Running && own is not null ? _resolve(own) : null;
        var skills = _skills.Snapshot;
        var bound = profile?.ProfileId is { Length: > 0 } && own is not null && ReferenceEquals(evidence, _source.CombatSnapshot);
        CharacterCombatSnapshot next;
        if (!bound) next = CharacterCombatSnapshot.Empty;
        else
        {
            var skillMatches = skills.ProfileId == profile!.ProfileId && skills.Generation == own!.Generation
                && skills.ClassId == evidence.ClassId;
            next = new()
            {
                ProfileId = profile.ProfileId, ClassId = evidence.ClassId, CharacterLevel = evidence.CharacterLevel,
                CombatLevel = evidence.CombatLevel, ConquerorLevel = evidence.ConquerorLevel,
                CurrentCombatScore = evidence.CurrentCombatScore, IsFresh = true, Generation = evidence.Generation,
                Sequence = evidence.Sequence, ObservedAt = evidence.ObservedAt, NativeStats = WithFreshness(evidence.Stats),
                HpMaximum = WithFreshness(evidence.HpMaximum), MpMaximum = WithFreshness(evidence.MpMaximum),
                DisplayStats = ProjectDisplayStats(evidence),
                Skills = skillMatches ? skills.Skills : ImmutableDictionary<int, OwnSkillLevel>.Empty,
                SkillStatus = skillMatches && skills.IsKnown ? skills.IsFresh ? NetworkCombatSourceStatus.Complete : NetworkCombatSourceStatus.Stale : NetworkCombatSourceStatus.Unknown,
                SkillsObservedAt = skillMatches ? skills.ObservedAt : null,
                ActivatedBoards = WithFreshness(evidence.DaevanionBoards), NativePoints = WithFreshness(evidence.DaevanionPoints),
                Titles = WithFreshness(evidence.Titles), TitleOwnership = WithFreshness(evidence.TitleOwnership),
                WingOwnership = WithFreshness(evidence.WingOwnership), EquippedWing = WithFreshness(evidence.EquippedWing),
                Achievements = WithFreshness(evidence.Achievements), Equipment = WithFreshness(evidence.Equipment), Pets = WithFreshness(evidence.Pets),
                Abnormals = WithFreshness(evidence.Abnormals),
                PointPools = ProjectPoints(evidence),
                MissingRequirements = Array.AsReadOnly(new[] { "Quellen werden ausgewertet." })
            };
        }
        var startWorker = false;
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(_evidence, evidence) && _skillEvidence == skills && _snapshot.IsFresh == next.IsFresh) return;
            _evidence = evidence; _skillEvidence = skills; _snapshot = next;
            var revision = ++_revision;
            _pending = next.IsFresh ? (next, revision) : null;
            if (_pending is not null && !_workerRunning) { _workerRunning = true; startWorker = true; }
        }
        if (_exploration is not null && next.IsFresh && own is not null && evidence.Generation == own.Generation
            && next.ProfileId is { } profileId
            && _source.Running && ReferenceEquals(evidence, _source.CombatSnapshot)
            && _resolve(own)?.ProfileId == profileId)
        {
            _exploration.ApplyNetworkTitleOwnership(own, profileId, next.TitleOwnership);
            // The title callback can reentrantly publish a later source/profile revision.
            if (_source.Running && ReferenceEquals(evidence, _source.CombatSnapshot)
                && _resolve(own)?.ProfileId == profileId)
                _exploration.ApplyNetworkWingOwnership(own, profileId, next.WingOwnership);
            if (_source.Running && ReferenceEquals(evidence, _source.CombatSnapshot)
                && _resolve(own)?.ProfileId == profileId)
                _exploration.ApplyNetworkGenusInsight(own, profileId, next.Pets);
        }
        SafeEvent.Raise(Changed, "Charakterwerte");
        if (startWorker) _ = Task.Run(ProjectPending);
    }

    private void ProjectPending()
    {
        while (true)
        {
            (CharacterCombatSnapshot Snapshot, long Revision) work;
            lock (_gate)
            {
                if (_disposed || _pending is null) { _workerRunning = false; return; }
                work = _pending.Value; _pending = null;
            }
            CharacterCombatSnapshot projected;
            try
            {
                var snapshot = work.Snapshot;
                var graph = CombatSourceGraphBuilder.Build(new()
                {
                    ClassId = snapshot.ClassId ?? "", CharacterLevel = snapshot.CharacterLevel,
                    NativeStats = new(snapshot.NativeStats.Value, Status(snapshot.NativeStats)),
                    Skills = new(Array.AsReadOnly(snapshot.Skills.Values.OrderBy(skill => skill.SkillId).ToArray()), Status(snapshot.SkillStatus)),
                    Titles = new(snapshot.Titles.Value, Status(snapshot.Titles)), Equipment = new(snapshot.Equipment.Value, Status(snapshot.Equipment)),
                    TitleOwnership = new(snapshot.TitleOwnership.Value, Status(snapshot.TitleOwnership)),
                    Achievements = new(snapshot.Achievements.Value, Status(snapshot.Achievements)), Boards = new(snapshot.ActivatedBoards.Value, Status(snapshot.ActivatedBoards)),
                    ActiveAbnormals = new(snapshot.Abnormals.Value is { } abnormals ? (IReadOnlyList<CharacterAbnormal>)abnormals.Abnormals : null, Status(snapshot.Abnormals)),
                    Pets = new(snapshot.Pets.Value, Status(snapshot.Pets))
                });
                var breakdown = CombatScoreModel.Evaluate(graph);
                projected = snapshot with { Graph = graph, Breakdown = breakdown,
                    MissingRequirements = Array.AsReadOnly(breakdown.MissingRequirements.Distinct(StringComparer.Ordinal).ToArray()) };
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or OverflowException)
            {
                Trace.TraceWarning("Character combat projection: {0}", error.GetType().Name);
                projected = work.Snapshot with { MissingRequirements = Array.AsReadOnly(new[] { "Die Charakterquellen konnten noch nicht vollständig ausgewertet werden." }) };
            }
            lock (_gate)
            {
                if (_disposed) { _workerRunning = false; return; }
                if (work.Revision != _revision) continue;
                _snapshot = projected;
            }
            SafeEvent.Raise(Changed, "Charakterwerte");
        }
    }

    private CharacterDisplayStats ProjectDisplayStats(NetworkCharacterCombatSnapshot evidence) =>
        CharacterDisplayStatProjection.Evaluate(new()
        {
            ClientBuild = _catalog.ClientBuild, ClassId = evidence.ClassId, Generation = evidence.Generation,
            NativeStats = evidence.Stats.Value is { } values
                ? new(values, Status(evidence.Stats), evidence.Stats.IsFresh, evidence.Generation,
                    evidence.Stats.ObservedAt, evidence.Stats.Source) : null,
            HpMaximum = MaximumFact(evidence.HpMaximum, evidence.Generation),
            MpMaximum = MaximumFact(evidence.MpMaximum, evidence.Generation)
        });

    private static CharacterDisplayMaximumFact? MaximumFact(NetworkCombatDomain<NetworkCharacterResourceMaximum> domain, long generation) =>
        domain.Value is { } maximum ? new(maximum.Value, Status(domain), domain.IsFresh, generation,
            domain.ObservedAt, domain.Source, domain.Source switch
            {
                "own-entry-resource-maxima" => CharacterDisplayVitalSourceKind.Own,
                "own-resource-maxima-update" => CharacterDisplayVitalSourceKind.StateUpdate8D00,
                _ => CharacterDisplayVitalSourceKind.Unknown
            }) : null;

    private IReadOnlyList<CharacterCombatPointPoolSnapshot> ProjectPoints(NetworkCharacterCombatSnapshot evidence) =>
        Array.AsReadOnly(Enum.GetValues<DaevanionBudgetPool>().Select(pool =>
        {
            var points = WithFreshness(evidence.DaevanionPoints);
            // Types 1/2/3 are qualified paid node costs. The level/growth contract below
            // establishes a bounded derived budget, never a native free-wallet reading.
            var type = pool switch { DaevanionBudgetPool.Normal => (byte)1, DaevanionBudgetPool.Ariel => (byte)2, _ => (byte)3 };
            var usable = points.IsFresh && points.Status is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial;
            int? spent = usable && points.Value?.Used.TryGetValue(type, out var used) == true ? used : null;
            int? bonus = usable && points.Value?.Bonus.TryGetValue(type, out var extra) == true ? extra : null;
            var capacity = pool == DaevanionBudgetPool.Normal ? 840 : 232;
            var status = points.Status is NetworkCombatSourceStatus.Stale or NetworkCombatSourceStatus.Conflict ? points.Status
                : spent is not null || bonus is not null ? NetworkCombatSourceStatus.Partial : NetworkCombatSourceStatus.Unknown;
            var source = usable ? points.Source : "capacity-catalog; point-balances-unconfirmed";
            int? total = null, free = null;
            var qualification = _catalog.NativeExpPointQualification;
            var scope = _catalog.ClientBuild == 26262663 && qualification.ClientBuild == _catalog.ClientBuild
                && qualification.BudgetContractStatus == CombatBudgetContractStatus.QualifiedBoundedInitialGrowth45
                && !qualification.IsNativeFreeWallet && evidence.CharacterLevel is not null
                && qualification.RequiredCombatLevel is { } combatLevel && evidence.CombatLevel == combatLevel
                && qualification.RequiredConquerorLevel is { } conquerorLevel && evidence.ConquerorLevel == conquerorLevel;
            var normal = pool == DaevanionBudgetPool.Normal;
            var qualifiedPool = scope && (normal
                ? qualification.NormalBudgetContractVersion == "normal-budget-26262663-level45-v1"
                    && qualification.NormalBudgetQualifiedLevels.Contains(evidence.CharacterLevel!.Value)
                : qualification.SpecialBudgetContractVersion == "special-budget-26262663-level45-v1"
                    && qualification.SpecialPoolsStatus == CombatKnowledgeStatus.Complete
                    && qualification.SpecialBudgetQualifiedLevels.Contains(evidence.CharacterLevel!.Value));
            var basePoints = normal && evidence.CharacterLevel is { } currentLevel ? _catalog.NormalBasePointsAt(currentLevel) : 0;
            if (qualifiedPool && spent is { } observedSpent && bonus is { } observedBonus && basePoints is { } basis)
            {
                source = DerivedPointBudgetSource;
                var candidateTotal = basis + observedBonus;
                var candidateFree = candidateTotal - observedSpent;
                if (candidateFree < 0 || candidateTotal > capacity || observedSpent > capacity)
                    status = NetworkCombatSourceStatus.Conflict;
                else { total = candidateTotal; free = candidateFree; status = NetworkCombatSourceStatus.Complete; }
            }
            else if (spent is { } actualSpent && actualSpent > capacity) status = NetworkCombatSourceStatus.Conflict;
            return new CharacterCombatPointPoolSnapshot(pool, free, spent, total, bonus, capacity, status, source);
        }).ToArray());

    private static CombatKnowledgeStatus Status<T>(NetworkCombatDomain<T> domain) where T : class =>
        !domain.IsFresh && domain.Status is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial
            ? CombatKnowledgeStatus.Stale : Status(domain.Status);
    private static NetworkCombatDomain<T> WithFreshness<T>(NetworkCombatDomain<T> domain) where T : class =>
        !domain.IsFresh && domain.Status is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial
            ? domain with { Status = NetworkCombatSourceStatus.Stale } : domain;
    private static CombatKnowledgeStatus Status(NetworkCombatSourceStatus status) => status switch
    {
        NetworkCombatSourceStatus.Complete => CombatKnowledgeStatus.Complete, NetworkCombatSourceStatus.Partial => CombatKnowledgeStatus.Partial,
        NetworkCombatSourceStatus.Stale => CombatKnowledgeStatus.Stale, NetworkCombatSourceStatus.Conflict => CombatKnowledgeStatus.Conflict,
        _ => CombatKnowledgeStatus.Unknown
    };

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _pending = null; _snapshot = CharacterCombatSnapshot.Empty; ++_revision; }
        _source.Changed -= Refresh; _source.OwnCharacterChanged -= Refresh; _skills.Changed -= Refresh;
        if (_exploration is not null) _exploration.Changed -= ProfileChanged;
    }
}
