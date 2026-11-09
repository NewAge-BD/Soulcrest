using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementOverlayIntegrationTests
{
    [Fact]
    public void AchievementLayoutAndVisibilityAreIndependentOfThePetTracker()
    {
        var settings = new AppSettings { OverlayEnabled = false, OverlayX = 71, OverlayY = 82, OverlayScale = .9 };
        Assert.True(OverlayRegistry.IsEnabled(settings, "achievements"));
        Assert.True(OverlayRegistry.Get("achievements").CanPlace);
        OverlayRegistry.WriteLayout(settings, "achievements", new(300, 400, 99));
        Assert.Equal(new(300, 400, 2.5), OverlayRegistry.ReadLayout(settings, "achievements"));
        Assert.Equal(new(71, 82, .9), OverlayRegistry.ReadLayout(settings, "loot"));
        OverlayRegistry.SetEnabled(settings, "achievements", false);
        Assert.False(settings.AchievementOverlayEnabled);
        Assert.False(settings.OverlayEnabled);
        Assert.True(OverlayHotkeys.ToggleVisibility(settings, "achievements.toggle"));
        Assert.True(settings.AchievementOverlayEnabled);
        Assert.False(settings.OverlayEnabled);
        Assert.DoesNotContain("achievements.toggle", OverlayHotkeys.Defaults().Keys);
    }

    [Fact]
    public void SharedCaptureAndContextPreferencesApplyWithoutChangingPetSettings()
    {
        var settings = new AppSettings();
        Assert.Null(settings.AchievementOverlayX);
        Assert.Null(settings.AchievementOverlayY);
        Assert.Equal(1, settings.AchievementOverlayScale);
        Assert.Equal(OverlayVisibilityPolicy.Get(settings, "loot"), OverlayVisibilityPolicy.Get(settings, "achievements"));
        Assert.True(OverlayVisibilityPolicy.IsForegroundSuppressed(settings, gameInForeground: false));
        settings.OverlayVisibility["achievements"] = new(true, true, true, true);
        Assert.NotEqual(OverlayVisibilityPolicy.Get(settings, "loot"), OverlayVisibilityPolicy.Get(settings, "achievements"));
        Assert.False(settings.OverlaysInRecordings);
    }

    [Fact]
    public void PlacementExampleUsesTheProductionRendererAndQualifiedNamesAndIcons()
    {
        var rows = OverlayPreviewRenderer.AchievementExamples();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, row => row.IsFavorite);
        Assert.Contains(rows, row => row.IsAnimating);
        Assert.All(rows, row => Assert.True(row.HasQualifiedGoal));
        Assert.Contains(rows, row => row.FamilyId == 411100100 && row.Target == 100000);
        Assert.Contains(rows, row => row.IsAnimating && row.CelebratedStageId is not null && row.CelebratedStageId != row.StageId);
        Assert.All(rows, row =>
        {
            Assert.NotEmpty(row.NameDe);
            Assert.NotEmpty(row.ConditionEn);
            Assert.NotEmpty(row.Rewards);
            Assert.False(AchievementContentPolicy.IsRetired(row.FamilyId, AchievementContentPolicy.MetadataBuild));
        });
        using var image = AchievementOverlayRenderer.Render(rows);
        Assert.Equal(360, image.Width);
        Assert.Equal(AchievementOverlayRenderer.HeightFor(rows.Count), image.Height);
    }
}
