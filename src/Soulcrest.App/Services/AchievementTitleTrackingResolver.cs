using System.Collections.Immutable;
using Soulcrest.App.Components;
using Soulcrest.App.Network;

namespace Soulcrest.App.Services;

internal sealed record AchievementTitleTrackingTarget(CombatTitleDisplayDefinition Title, int? FamilyId);

/// <summary>Explicit reward references select tracking goals; neither a reference nor a claim proves title ownership.</summary>
internal sealed class AchievementTitleTrackingResolver
{
    private readonly CombatTitleDisplayCatalog _titles;
    private readonly CombatTitleUnlockCatalog _unlocks;
    private readonly AchievementDefinitionCatalog _definitions;
    private readonly CombatAchievementDisplayCatalog _display;

    internal AchievementTitleTrackingResolver(CombatTitleDisplayCatalog? titles = null, CombatTitleUnlockCatalog? unlocks = null,
        AchievementDefinitionCatalog? definitions = null, CombatAchievementDisplayCatalog? display = null)
    {
        _titles = titles ?? CombatTitleDisplayCatalog.Default;
        _unlocks = unlocks ?? CombatTitleUnlockCatalog.Default;
        _definitions = definitions ?? AchievementDefinitionCatalog.Default;
        _display = display ?? CombatAchievementDisplayCatalog.Default;
    }

    internal ImmutableArray<AchievementTitleTrackingTarget> Resolve(AchievementOwnSnapshot own, ImmutableArray<int> titleIds,
        CharacterCombatSnapshot? character = null)
    {
        if (own.Binding is null || titleIds.IsDefaultOrEmpty) return [];
        var targets = ImmutableArray.CreateBuilder<AchievementTitleTrackingTarget>();
        foreach (var titleId in titleIds)
        {
            if (_titles.Find(titleId) is not { } title) continue;
            if (BuildsMatch(own.State.MetadataBuild) && HasOnlyRetiredRoutes(titleId)) continue;
            var observed = BuildsMatch(own.State.MetadataBuild)
                ? Families(titleId).Where(family => own.ForProgressFamily(family) is { HasProgressEvidence: true, Raw: not null } evidence
                    && evidence.Raw.Achievements.ContainsKey(family)).ToArray() : [];
            if (observed.Length != 0)
            {
                foreach (var family in observed) targets.Add(new(title, family));
            }
            else if (!HasOwnTitle(own, character, titleId)) targets.Add(new(title, null));
        }
        return targets.ToImmutable();
    }

    internal static AchievementTrackerRow Placeholder(CombatTitleDisplayDefinition title) => new()
    {
        TrackedTitleId = title.Id, TitleNameDe = title.NameDe, TitleNameEn = title.NameEn, IsTitlePlaceholder = true,
        NameDe = title.NameDe, NameEn = title.NameEn, IconPath = title.IconPath,
        ConditionDe = "Noch kein eigener Fortschritt für diese Freischaltung erkannt.",
        ConditionEn = "No own progress has been observed for this unlock yet.",
        IsFavorite = true, State = AchievementTrackerRowState.Unknown,
    };

    private bool BuildsMatch(int build) => build == _titles.MetadataBuild && build == _unlocks.MetadataBuild
        && build == _definitions.MetadataBuild && build == _display.MetadataBuild;

    private bool HasOnlyRetiredRoutes(int titleId) => _unlocks.Find(titleId)?.Routes is { Count: > 0 } routes
        && routes.All(route => route.Kind == "AchievementRewardItem"
            && AchievementContentPolicy.IsRetired(route.SourceId, _definitions.MetadataBuild)
            && route.StageId is { } stage && _definitions.Find(route.SourceId)?.StageIds.Contains(stage) == true
            && _display.Find(route.SourceId)?.Stages.Any(item => item.Id == stage) == true);

    private IEnumerable<int> Families(int titleId) => (_unlocks.Find(titleId)?.Routes ?? [])
        .Where(route => route.Kind == "AchievementRewardItem" && route.StageId is { } stage
            && _definitions.Find(route.SourceId) is { Role: not AchievementRole.Reference } definition
            && definition.StageIds.Contains(stage) && _display.Find(route.SourceId) is { HasNativeName: true } display
            && display.Stages.Any(item => item.Id == stage)
            && !AchievementContentPolicy.IsRetired(route.SourceId, _definitions.MetadataBuild))
        .Select(route => route.SourceId).Distinct();

    private static bool HasOwnTitle(AchievementOwnSnapshot own, CharacterCombatSnapshot? character, int titleId) => own.IsLive
        && character is { IsFresh: true, TitleOwnership: { IsFresh: true, Status: NetworkCombatSourceStatus.Partial or NetworkCombatSourceStatus.Complete } }
        && character.ProfileId == own.Binding?.ProfileId && character.Generation == own.State.Generation
        && character.TitleOwnership.Value?.Contains(titleId) == true;
}
