using System.Collections.Frozen;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Components;

public sealed record CombatAchievementStageProgress(int StageId, long? Current, long? Target, bool? Completed, bool? Claimed)
{
    public decimal? DisplayCurrent { get; init; }
    public decimal? DisplayTarget { get; init; }
    public bool? RequirementsMetNow { get; init; }
    public CombatAchievementProgressUnit Unit { get; init; } = CombatAchievementProgressUnit.Count;
}
public sealed record CombatAchievementFamilyProgress(int Id, int? CurrentStageId,
    IReadOnlyDictionary<int, CombatAchievementStageProgress> Stages)
{
    public CombatKnowledgeStatus Status { get; init; } = CombatKnowledgeStatus.Complete;
    public CombatAchievementStageProgress? Find(int id) => Stages.GetValueOrDefault(id);
}
public sealed record CombatAchievementProgressSnapshot(long Generation, long SourceSequence,
    IReadOnlyDictionary<int, CombatAchievementFamilyProgress> Families)
{
    public CombatAchievementFamilyProgress? Find(int id) => Families.GetValueOrDefault(id);
}

/// <summary>Only explicit qualified type contracts interpret counters. CompleteTime and absent rows grant no facts.</summary>
public static class CombatAchievementProgressProjection
{
    public static CombatAchievementProgressSnapshot Create(CharacterCombatSnapshot snapshot, int metadataBuild) =>
        Create(snapshot, metadataBuild, CombatAchievementProgressContractCatalog.Default);

    internal static CombatAchievementProgressSnapshot Create(CharacterCombatSnapshot snapshot, int metadataBuild,
        CombatAchievementProgressContractCatalog contracts)
    {
        var source = snapshot.Achievements;
        var result = new Dictionary<int, CombatAchievementFamilyProgress>();
        if (contracts.MetadataBuild != metadataBuild || !snapshot.IsFresh || snapshot.Generation <= 0
            || string.IsNullOrWhiteSpace(snapshot.ProfileId) || !source.IsFresh || source.Value is null
            || source.ObservedAt is null || source.Sequence <= 0
            || source.Status is not (NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial)
            || source.Source is not ("own-achievement-list" or "own-achievement-update" or "own-achievement-reward"))
            return new(snapshot.Generation, source.Sequence, result.ToFrozenDictionary());

        return new AchievementRuleEvaluator(contracts).Evaluate(source.Value, metadataBuild, snapshot.Generation, source.Sequence);
    }

    internal static void ValidateContract(string json) => CombatAchievementProgressContractCatalog.FromJson(json, CombatAchievementDisplayCatalog.Default);
}
