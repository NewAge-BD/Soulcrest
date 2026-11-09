using System.Collections.Frozen;
using Soulcrest.App.Components;
using Soulcrest.Core.Network;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Achievements;

namespace Soulcrest.App.Services;

/// <summary>A measured stage rule bound to the complete metadata/operand fingerprint, never selected by text.</summary>
public sealed record AchievementBoundStageRule(int MetadataBuild, int FamilyId, int StageId, string DefinitionSignature,
    long RawTarget, AchievementSemanticRule Semantics, CombatAchievementProgressUnit Unit)
{
    public bool PermanentPeriodQualified { get; init; }
}

/// <summary>Versioned evaluation over qualified evidence only. No transport or UI side effects.</summary>
public sealed class AchievementRuleEvaluator
{
    public const string Version = "achievement-rules-v3";
    private readonly CombatAchievementProgressContractCatalog _contracts;
    private readonly IReadOnlyList<AchievementBoundStageRule> _additional;
    private readonly AchievementDefinitionCatalog _definitions;
    private readonly AchievementProgressThresholdCatalog _thresholds;
    public AchievementRuleEvaluator() : this(CombatAchievementProgressContractCatalog.Default) { }
    internal AchievementRuleEvaluator(CombatAchievementProgressContractCatalog contracts,
        IEnumerable<AchievementBoundStageRule>? additional = null, AchievementDefinitionCatalog? definitions = null,
        AchievementProgressThresholdCatalog? thresholds = null)
    {
        _contracts = contracts; _additional = Array.AsReadOnly((additional ?? []).ToArray());
        _definitions = definitions ?? AchievementDefinitionCatalog.Default;
        _thresholds = thresholds ?? AchievementProgressThresholdCatalog.Default;
        if (_additional.GroupBy(rule => (rule.FamilyId, rule.StageId)).Any(group => group.Count() != 1))
            throw new ArgumentException("Duplicate achievement stage rule.", nameof(additional));
    }

    /// <summary>Historical stage facts must be independently confirmed for this binding and the qualified current period.</summary>
    public CombatAchievementProgressSnapshot Evaluate(CharacterAchievementState raw, int metadataBuild,
        long generation, long sequence, IReadOnlySet<int>? earnedStageIds = null,
        IReadOnlySet<int>? knownClaimedStageIds = null)
    {
        var contracts = _contracts;
        var result = new Dictionary<int, CombatAchievementFamilyProgress>();
        if (contracts.MetadataBuild != metadataBuild) return new(generation, sequence, result.ToFrozenDictionary());
        foreach (var type in contracts.Contracts)
        foreach (var family in type.Families)
        {
            raw.Achievements.TryGetValue(family.Id, out var observed);
            var measured = type.CounterRule == CombatAchievementCounterRule.Cumulative;
            var inferred = !measured ? FindThresholds(family, metadataBuild) : null;
            var modelCurrentState = inferred?.ModelCurrentState == true;
            var numericCounter = measured || inferred is not null;
            var hasEarned = family.Stages.Any(stage => earnedStageIds?.Contains(stage.Id) == true);
            var claimHistoryQualified = CanUseClaimHistory(family.Id, metadataBuild);
            bool HistoricalClaim(int stageId) => claimHistoryQualified && knownClaimedStageIds?.Contains(stageId) == true;
            var hasClaimHistory = family.Stages.Any(stage => HistoricalClaim(stage.Id));
            if (observed is null && !hasEarned && !hasClaimHistory) continue;
            var claimKnown = observed is not null && (type.ClaimRule switch
            {
                CombatAchievementClaimRule.FirstRewardOrdinal => observed.RewardedStep <= 1,
                CombatAchievementClaimRule.ContiguousRewardOrdinal => observed.RewardedStep <= family.Stages.Count,
                _ => false
            });
            if (observed is null || !numericCounter || observed.Count < 0)
            {
                // Unknown/negative/absent counters cannot undo independently earned facts or claims.
                if (!hasEarned && !claimKnown && !hasClaimHistory) continue;
                var claimedStages = family.Stages.ToFrozenDictionary(stage => stage.Id, stage =>
                {
                    var earned = earnedStageIds?.Contains(stage.Id) == true;
                    var stageClaimKnown = claimKnown && (type.ClaimRule == CombatAchievementClaimRule.ContiguousRewardOrdinal
                        || stage.Ordinal == 1);
                    var claimed = HistoricalClaim(stage.Id) ? true : stageClaimKnown ? stage.Ordinal <= observed!.RewardedStep : (bool?)null;
                    return new CombatAchievementStageProgress(stage.Id, null, null, earned || claimed == true ? true : null,
                        claimed) { Unit = CombatAchievementProgressUnit.Unknown };
                });
                // The independently qualified reward prefix chooses the first not-yet-claimed
                // native stage even when the counter's unit and target are still unknown.
                var unclaimed = family.Stages.FirstOrDefault(stage => claimedStages[stage.Id].Claimed == false);
                if (unclaimed is null && family.HasCompleteStageScope && claimedStages.Values.All(stage => stage.Claimed == true))
                    unclaimed = family.Stages[^1];
                result.Add(family.Id, new(family.Id, unclaimed?.Id, claimedStages) { Status = CombatKnowledgeStatus.Partial });
                continue;
            }
            var unit = measured ? type.Unit : CombatAchievementProgressUnit.Count;
            var targets = measured ? family.Stages.ToDictionary(stage => stage.Id, stage => stage.Target!.Value)
                : inferred!.Stages.ToDictionary(stage => stage.Id, stage => stage.Target);
            HashSet<int> requirementStages = modelCurrentState ? inferred!.Stages
                .Where(stage => stage.Presentation == AchievementProgressPresentation.Requirement)
                .Select(stage => stage.Id).ToHashSet() : [];
            var effectiveClaims = family.Stages.ToDictionary(stage => stage.Id, stage => HistoricalClaim(stage.Id)
                ? true : claimKnown ? stage.Ordinal <= observed.RewardedStep : (bool?)null);
            // Keep the already qualified schema-2 contract behavior through the shared semantic engine.
            var evaluations = family.Stages.ToDictionary(stage => stage.Id, stage => AchievementSemanticEngine.Evaluate(
                new(measured ? type.Id : "metadata-count-" + family.Id, 1,
                    modelCurrentState ? AchievementSemanticKind.CurrentState
                    : unit == CombatAchievementProgressUnit.Minutes ? AchievementSemanticKind.Duration
                    : AchievementSemanticKind.CumulativeCount, targets[stage.Id])
                {
                    IsQualified = true, CounterCanConfirmCompletion = true, MonotoneCounterQualified = !modelCurrentState,
                    ClaimConfirmsCompletion = true,
                    Duration = unit == CombatAchievementProgressUnit.Minutes
                        ? new(AchievementDurationUnit.Minutes, AchievementDurationUnit.Minutes) : null,
                }, new(observed.Count)
                {
                    CompleteTime = observed.CompleteTime, PeriodQualified = true,
                    PreviouslyEarned = earnedStageIds?.Contains(stage.Id) == true ? AchievementTruth.True : AchievementTruth.Unknown,
                    Claimed = ToTruth(effectiveClaims[stage.Id]),
                }));
            var conflict = evaluations.Values.Any(value => value.HasConflict);
            var stages = family.Stages.ToFrozenDictionary(stage => stage.Id, stage => new CombatAchievementStageProgress(
                stage.Id, observed.Count, targets[stage.Id], earnedStageIds?.Contains(stage.Id) == true || HistoricalClaim(stage.Id) ? true
                    : conflict ? null : Truth(evaluations[stage.Id].EarnedCompletion),
                effectiveClaims[stage.Id])
                {
                    Unit = unit,
                    RequirementsMetNow = measured && earnedStageIds is null ? null : Truth(evaluations[stage.Id].RequirementsMetNow),
                    DisplayCurrent = requirementStages.Contains(stage.Id)
                        ? Truth(evaluations[stage.Id].RequirementsMetNow) switch { true => 1m, false => 0m, _ => null } : null,
                    DisplayTarget = requirementStages.Contains(stage.Id)
                        && evaluations[stage.Id].RequirementsMetNow != AchievementTruth.Unknown ? 1m : null,
                });
            var current = AchievementProgressStageResolver.Select(family.Stages.Select(stage =>
                    new AchievementProgressThresholdStage(stage.Id, targets[stage.Id])).ToArray(), observed.Count,
                effectiveClaims.Where(pair => pair.Value == true).Select(pair => pair.Key).ToHashSet(), family.HasCompleteStageScope);
            result.Add(family.Id, new(family.Id, current, stages)
            {
                Status = conflict ? CombatKnowledgeStatus.Conflict : claimKnown && family.HasCompleteStageScope ? CombatKnowledgeStatus.Complete : CombatKnowledgeStatus.Partial
            });
        }
        foreach (var group in _additional.GroupBy(rule => rule.FamilyId))
        {
            raw.Achievements.TryGetValue(group.Key, out var observed);
            if (observed is null && !group.Any(binding => earnedStageIds?.Contains(binding.StageId) == true)) continue;
            var definition = _definitions.Find(group.Key);
            var stages = new Dictionary<int, CombatAchievementStageProgress>();
            var conflict = false;
            foreach (var binding in group)
            {
                if (binding.MetadataBuild != metadataBuild || _definitions.MetadataBuild != metadataBuild
                    || definition is null || definition.Signature != binding.DefinitionSignature
                    || !definition.StageIds.Contains(binding.StageId) || binding.RawTarget < 0) continue;
                var value = AchievementSemanticEngine.Evaluate(binding.Semantics, new(observed?.Count)
                {
                    CompleteTime = observed?.CompleteTime, PeriodQualified = binding.PermanentPeriodQualified,
                    PreviouslyEarned = earnedStageIds?.Contains(binding.StageId) == true
                        || CanUseClaimHistory(group.Key, metadataBuild) && knownClaimedStageIds?.Contains(binding.StageId) == true
                            ? AchievementTruth.True : AchievementTruth.Unknown,
                    Claimed = CanUseClaimHistory(group.Key, metadataBuild) && knownClaimedStageIds?.Contains(binding.StageId) == true
                        ? AchievementTruth.True : AchievementTruth.Unknown,
                });
                conflict |= value.HasConflict;
                stages.Add(binding.StageId, new(binding.StageId, value.ProgressValue is null ? null : observed!.Count,
                    value.TargetValue is null ? null : binding.RawTarget, Truth(value.EarnedCompletion), Truth(value.Claimed))
                {
                    RequirementsMetNow = Truth(value.RequirementsMetNow), DisplayCurrent = value.ProgressValue,
                    DisplayTarget = value.TargetValue, Unit = binding.Unit,
                });
            }
            if (stages.Count > 0)
            {
                var existing = result.GetValueOrDefault(group.Key);
                var merged = existing?.Stages.ToDictionary() ?? [];
                foreach (var stage in stages) merged[stage.Key] = stage.Value;
                result[group.Key] = new(group.Key, existing?.CurrentStageId, merged.ToFrozenDictionary())
                { Status = conflict || existing?.Status == CombatKnowledgeStatus.Conflict ? CombatKnowledgeStatus.Conflict : CombatKnowledgeStatus.Partial };
            }
        }
        return new(generation, sequence, result.ToFrozenDictionary());
    }

    public string? QualifiedPeriodId(int familyId) => _additional.Any(rule => rule.FamilyId == familyId)
        ? _additional.Where(rule => rule.FamilyId == familyId).All(rule => rule.PermanentPeriodQualified) ? "permanent" : null
        : _contracts.FindFamily(familyId) is not null ? "permanent" : null;

    private bool CanUseClaimHistory(int familyId, int metadataBuild) => _definitions.MetadataBuild == metadataBuild
        && _definitions.Find(familyId) is { Area: AchievementArea.Normal, Role: AchievementRole.PlayerFacing }
        && QualifiedPeriodId(familyId) == "permanent" && _contracts.FindFamily(familyId)?.ClaimRule is
            CombatAchievementClaimRule.ContiguousRewardOrdinal or CombatAchievementClaimRule.FirstRewardOrdinal;

    private AchievementProgressThresholdFamily? FindThresholds(CombatAchievementFamilyContract family, int metadataBuild)
    {
        var thresholds = _thresholds.MetadataBuild == metadataBuild ? _thresholds.FindFamily(family.Id) : null;
        return thresholds is not null && _definitions.MetadataBuild == metadataBuild && family.HasCompleteStageScope
            && _definitions.Find(family.Id)?.Signature == thresholds.DefinitionSignature
            && family.Stages.Select(stage => stage.Id).SequenceEqual(thresholds.Stages.Select(stage => stage.Id))
                ? thresholds : null;
    }

    public bool? ClaimFact(CharacterAchievementState raw, int familyId, int stageId)
    {
        var type = _contracts.FindFamily(familyId);
        var family = type?.Families.FirstOrDefault(item => item.Id == familyId);
        var stage = family?.Stages.FirstOrDefault(item => item.Id == stageId);
        if (type is null || stage is null || !raw.Achievements.TryGetValue(familyId, out var observed)) return null;
        var known = type.ClaimRule switch
        {
            CombatAchievementClaimRule.FirstRewardOrdinal => observed.RewardedStep <= 1,
            CombatAchievementClaimRule.ContiguousRewardOrdinal => observed.RewardedStep <= family!.Stages.Count,
            _ => false,
        };
        return known ? stage.Ordinal <= observed.RewardedStep : null;
    }

    private static bool? Truth(AchievementTruth value) => value switch
        { AchievementTruth.True => true, AchievementTruth.False => false, _ => null };
    private static AchievementTruth ToTruth(bool? value) => value switch
        { true => AchievementTruth.True, false => AchievementTruth.False, _ => AchievementTruth.Unknown };
}
