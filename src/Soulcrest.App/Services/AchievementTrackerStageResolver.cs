using System.Collections.Immutable;
using Soulcrest.App.Components;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Services;

internal sealed record AchievementTrackerStageSelection(AchievementTrackerRow Row,
    bool AllClaimed, bool CompletedUnclaimed);

/// <summary>Shared qualified progress stages, with independent claim retention. Labels and UI order never choose a live stage.</summary>
internal sealed class AchievementTrackerStageResolver
{
    private readonly CombatAchievementDisplayCatalog _display;
    private readonly CombatAchievementProgressContractCatalog _contracts;
    private readonly AchievementRuleEvaluator _rules;
    private readonly AchievementDefinitionCatalog _definitions;
    private readonly Dictionary<(int Family, int? Stage), AchievementTrackerRow> _labels = new();

    internal AchievementTrackerStageResolver(CombatAchievementDisplayCatalog? display = null,
        CombatAchievementProgressContractCatalog? contracts = null, AchievementDefinitionCatalog? definitions = null)
    {
        _display = display ?? CombatAchievementDisplayCatalog.Default;
        _contracts = contracts ?? CombatAchievementProgressContractCatalog.Default;
        _rules = new(_contracts);
        _definitions = definitions ?? AchievementDefinitionCatalog.Default;
    }

    internal bool IsEligible(int familyId, int metadataBuild, bool favorite = false) => metadataBuild == _display.MetadataBuild
        && metadataBuild == _definitions.MetadataBuild && _display.Find(familyId) is { HasNativeName: true }
        && _definitions.Find(familyId) is { Role: not AchievementRole.Reference } definition
        && (favorite || definition is { Area: AchievementArea.Normal, Role: AchievementRole.PlayerFacing })
        && !AchievementContentPolicy.IsRetired(familyId, metadataBuild);

    internal AchievementTrackerStageSelection Resolve(AchievementOwnSnapshot snapshot, int familyId)
    {
        snapshot = snapshot.ForProgressFamily(familyId);
        var definition = _display.Find(familyId);
        var familyFacts = snapshot.HasProgressEvidence ? snapshot.State.Progress.Find(familyId) : null;
        var type = snapshot.HasProgressEvidence
            && snapshot.State.MetadataBuild == _contracts.MetadataBuild && _display.MetadataBuild == _contracts.MetadataBuild
            ? _contracts.FindFamily(familyId) : null;
        var contract = type?.Families.FirstOrDefault(family => family.Id == familyId);
        var selected = familyFacts?.CurrentStageId;
        var projected = selected is { } currentStage ? familyFacts?.Find(currentStage) : null;
        var projectedGoal = familyFacts?.Status != CombatKnowledgeStatus.Conflict && projected is { Current: not null, Target: > 0 }
            && projected.Unit != CombatAchievementProgressUnit.Unknown;
        if (!projectedGoal && familyFacts?.Status != CombatKnowledgeStatus.Conflict && contract is not null)
            selected = contract.Stages.FirstOrDefault(stage => Claim(snapshot, familyId, stage.Id) == false)?.Id ?? selected;
        var allClaimed = contract is { HasCompleteStageScope: true }
            && contract.Stages.All(stage => Claim(snapshot, familyId, stage.Id) == true);
        var facts = selected is { } stageId ? familyFacts?.Find(stageId) : null;
        var metadata = Metadata(familyId, selected, definition);
        int? raw = null;
        if (snapshot.HasProgressEvidence && snapshot.Raw is { } rawState && rawState.Achievements.TryGetValue(familyId, out var observed))
            raw = observed.Count;
        var qualifiedGoal = familyFacts?.Status != CombatKnowledgeStatus.Conflict && facts is { Current: not null, Target: not null }
            && facts.Unit != CombatAchievementProgressUnit.Unknown;
        var pending = snapshot.IsLive && familyFacts?.Stages.Values.Any(stage => stage.Completed == true
            && Claim(snapshot, familyId, stage.StageId) == false) == true;
        var currentPending = snapshot.IsLive && selected is not null && facts?.Completed == true
            && Claim(snapshot, familyId, selected.Value) == false;
        return new(metadata with
        {
            State = currentPending ? AchievementTrackerRowState.AwaitingClaim : snapshot.IsLive
                ? AchievementTrackerRowState.InProgress : AchievementTrackerRowState.Unknown,
            HasUnclaimedRewards = pending,
            Current = qualifiedGoal ? facts!.Current : null,
            Target = qualifiedGoal ? facts!.Target : null,
            DisplayCurrent = qualifiedGoal ? facts!.DisplayCurrent : null,
            DisplayTarget = qualifiedGoal ? facts!.DisplayTarget : null,
            Unit = qualifiedGoal ? facts!.Unit : CombatAchievementProgressUnit.Unknown,
            RawValue = raw,
            IsHistorical = snapshot.IsHistorical && raw is not null,
        }, allClaimed, pending);
    }

    internal IEnumerable<int> PendingFamilies(AchievementOwnSnapshot snapshot) => !snapshot.IsLive
        ? [] : snapshot.State.Progress.Families.Keys.Where(id => Resolve(snapshot, id).CompletedUnclaimed);

    internal bool IsCompletedUnclaimed(AchievementOwnSnapshot snapshot, int familyId, int stageId) => snapshot.IsLive
        && snapshot.State.Progress.Find(familyId)?.Find(stageId)?.Completed == true
        && Claim(snapshot, familyId, stageId) == false;

    internal bool IsLaterStage(int familyId, int candidateStageId, int activeStageId)
    {
        var family = _contracts.FindFamily(familyId)?.Families.FirstOrDefault(item => item.Id == familyId);
        var candidate = family?.Stages.FirstOrDefault(stage => stage.Id == candidateStageId);
        var active = family?.Stages.FirstOrDefault(stage => stage.Id == activeStageId);
        return candidate is not null && active is not null && candidate.Ordinal > active.Ordinal;
    }

    internal bool HasNewQualifiedClaim(AchievementOwnSnapshot previous, AchievementOwnSnapshot current, int familyId)
    {
        if (!previous.IsLive || !current.IsLive || previous.Raw is null || current.Raw is null
            || previous.State.MetadataBuild != _contracts.MetadataBuild || current.State.MetadataBuild != _contracts.MetadataBuild)
            return false;
        var type = _contracts.FindFamily(familyId);
        var family = type?.Families.FirstOrDefault(item => item.Id == familyId);
        return family is not null && family.Stages.Any(stage => Claim(current, familyId, stage.Id) == true
            && Claim(previous, familyId, stage.Id) == false);
    }

    private bool? Claim(AchievementOwnSnapshot snapshot, int familyId, int stageId)
    {
        // Only the independently qualified permanent normal chains can latch a historical claim.
        // A lower raw prefix is not a reset and cannot announce the already collected tier again.
        if (_rules.QualifiedPeriodId(familyId) == "permanent" && snapshot.State.Milestones.Any(item =>
            item.Kind == AchievementMilestoneKind.RewardClaimed && item.FamilyId == familyId && item.StageId == stageId
            && item.MetadataBuild == snapshot.State.MetadataBuild && item.PeriodId == "permanent")) return true;
        return snapshot.Raw is null ? null : _rules.ClaimFact(snapshot.Raw, familyId, stageId);
    }

    private AchievementTrackerRow Metadata(int familyId, int? stageId, CombatAchievementDisplayDefinition? definition)
    {
        if (_labels.TryGetValue((familyId, stageId), out var cached)) return cached;
        var stage = stageId is { } id ? definition?.Stages.FirstOrDefault(item => item.Id == id) : null;
        var named = definition?.HasNativeName == true;
        var rewards = (stage?.Rewards ?? definition?.Rewards ?? []).Select(reward =>
            new AchievementTrackerReward(reward.NameDe, reward.NameEn, reward.IconPath, reward.Quantity)).ToImmutableArray();
        return _labels[(familyId, stageId)] = new()
        {
            FamilyId = familyId, StageId = stageId,
            NameDe = named ? stage?.NameDe ?? definition!.NameDe : "Erfolg noch nicht zugeordnet",
            NameEn = named ? stage?.NameEn ?? definition!.NameEn : "Achievement not mapped",
            ConditionDe = named ? stage?.ConditionDe ?? definition!.ConditionDe : "Die Bedingung ist noch nicht zugeordnet.",
            ConditionEn = named ? stage?.ConditionEn ?? definition!.ConditionEn : "The condition is not mapped yet.",
            IconPath = stage?.IconPath ?? definition?.IconPath, Rewards = rewards,
        };
    }
}
