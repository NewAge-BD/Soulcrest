using System.Drawing.Imaging;
using System.Collections.Immutable;
using Soulcrest.App.Overlay;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Anonymous production paint, without a window, game, capture or profile access.</summary>
public sealed class AchievementOverlayRenderDiagnostic
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void RenderHistoricalFavoriteProgress()
    {
        var target = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_ACHIEVEMENT_HISTORICAL_PNG"));
        var root = Path.GetFullPath(Path.Combine(TestPrerequisites.RepositoryRoot, "output", "social-media"));
        Assert.StartsWith(root + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var binding = new AchievementProfileBinding(new string('a', 32), new string('b', 32));
        var raw = new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty
            .Add(433101200, new(7, 0, 0)).Add(401101000, new(26, 0, 1)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        var build = AchievementDefinitionCatalog.Default.MetadataBuild;
        var state = new AchievementViewState { Binding = binding, ProfileId = binding.ProfileId, Source = AchievementViewSource.Saved,
            MetadataBuild = build, Progress = new AchievementRuleEvaluator().Evaluate(raw, build, 1, 1) };
        var snapshot = new AchievementOwnSnapshot(binding, state, raw, 0);
        var resolver = new AchievementTrackerStageResolver();
        var title = CombatTitleDisplayCatalog.Default.Find(23040023)!;
        var titleTarget = Assert.Single(new AchievementTitleTrackingResolver().Resolve(snapshot, [title.Id]));
        Assert.Equal(433101200, titleTarget.FamilyId);
        var titleRow = resolver.Resolve(snapshot, titleTarget.FamilyId!.Value).Row with { IsFavorite = true,
            TrackedTitleId = title.Id, TitleNameDe = title.NameDe, TitleNameEn = title.NameEn };
        var directRow = resolver.Resolve(snapshot, 401101000).Row with { IsFavorite = true };
        Assert.Equal(7, titleRow.Current); Assert.Equal(20, titleRow.Target);
        Assert.Equal(26, directRow.Current); Assert.Equal(40, directRow.Target);
        AchievementTrackerRow[] rows = [titleRow, directRow];
        Assert.All(rows, row => { Assert.True(row.IsHistorical); Assert.True(row.HasQualifiedGoal); Assert.False(row.IsAnimating); });
        foreach (var language in new[] { "de", "en" })
        foreach (var scale in new[] { 1d, 2d })
        {
            using var image = AchievementOverlayRenderer.Render(rows, language: language, scale: scale);
            image.Save(Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target)
                + "-" + language + (scale == 2 ? "-2x" : "") + ".png"), ImageFormat.Png);
        }
    }

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void RenderConfirmedDaevanionProgress()
    {
        var target = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_ACHIEVEMENT_CONFIRMED_PNG"));
        var root = Path.GetFullPath(Path.Combine(TestPrerequisites.RepositoryRoot, "output", "social-media"));
        Assert.StartsWith(root + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var owner = new AchievementProfileBinding(new string('a', 32), new string('b', 32));
        var raw = new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty
            .Add(401100500, new(66, 0, 3)).Add(401100600, new(66, 0, 3)).Add(401100700, new(61, 0, 3))
            .Add(401100800, new(77, 0, 3)).Add(401101100, new(40, 0, 4))
            .Add(401100900, new(80, 0, 4)).Add(401101000, new(30, 0, 1))
            .Add(401101200, new(50, 0, 4)).Add(401101300, new(20, 0, 4)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        var state = new AchievementViewState
        {
            Binding = owner, ProfileId = owner.ProfileId, Source = AchievementViewSource.Live,
            Generation = 1, Sequence = 1, MetadataBuild = AchievementDefinitionCatalog.Default.MetadataBuild,
            Progress = new AchievementRuleEvaluator().Evaluate(raw, AchievementDefinitionCatalog.Default.MetadataBuild, 1, 1),
        };
        var snapshot = new AchievementOwnSnapshot(owner, state, raw, 0);
        var resolver = new AchievementTrackerStageResolver();
        var selections = new[] { 401100500, 401100600, 401100700, 401100800, 401100900,
            401101000, 401101100, 401101200, 401101300 }.Select(id => resolver.Resolve(snapshot, id)).ToArray();
        Assert.All(selections, selection => Assert.True(selection.Row.HasQualifiedGoal));
        Assert.Equal(new long?[] { 66, 66, 61, 77, 80, 30, 40, 50, 20 }, selections.Select(selection => selection.Row.Current));
        Assert.Equal(new long?[] { 80, 80, 80, 80, 100, 40, 60, 60, 20 }, selections.Select(selection => selection.Row.Target));
        Assert.Equal(401101300, Assert.Single(selections, selection => selection.AllClaimed).Row.FamilyId);
        var rows = selections.Where(selection => !selection.AllClaimed
            && selection.Row.FamilyId is 401100900 or 401101000 or 401101200).Select(selection => selection.Row).ToArray();
        Assert.Equal(3, rows.Length);
        foreach (var language in new[] { "de", "en" })
        {
            using var image = AchievementOverlayRenderer.Render(rows, language: language);
            image.Save(Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + "-" + language + ".png"), ImageFormat.Png);
        }
    }

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void RenderNativeAchievementTracker()
    {
        var target = TestPrerequisites.RequiredValue("SOULCREST_ACHIEVEMENT_OVERLAY_PNG");
        var rows = OverlayPreviewRenderer.AchievementExamples();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        using var image = AchievementOverlayRenderer.Render(rows, language: "en");
        image.Save(target, ImageFormat.Png);
        using var german = AchievementOverlayRenderer.Render(rows, language: "de");
        german.Save(Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + "-de.png"), ImageFormat.Png);
        var family = CombatAchievementDisplayCatalog.Default.Find(420101100)!;
        var stage = family.Stages[3];
        var title = CombatTitleDisplayCatalog.Default.Find(23030006)!;
        AchievementTrackerRow titleRow = new()
        {
            FamilyId = family.Id, StageId = stage.Id, NameDe = stage.NameDe, NameEn = stage.NameEn,
            ConditionDe = stage.ConditionDe, ConditionEn = stage.ConditionEn, IconPath = stage.IconPath,
            IsFavorite = true, State = AchievementTrackerRowState.InProgress, Current = 71, Target = 100, RawValue = 71,
            Unit = CombatAchievementProgressUnit.Count, TrackedTitleId = title.Id, TitleNameDe = title.NameDe, TitleNameEn = title.NameEn,
        };
        foreach (var language in new[] { "en", "de" })
        {
            using var markerImage = AchievementOverlayRenderer.Render([rows[0] with { IsFavorite = true }, rows[0] with { IsFavorite = false }], language: language);
            markerImage.Save(Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target)
                + "-favorite-markers-" + language + ".png"), ImageFormat.Png);
            using var titleImage = AchievementOverlayRenderer.Render([rows[0], titleRow], language: language);
            titleImage.Save(Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target)
                + "-title-favorites-" + language + ".png"), ImageFormat.Png);
        }
    }
}
