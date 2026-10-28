using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class UnifiedLootOverlayRendererTests
{
    [Fact]
    public void SharedMixedSessionShowsBothChannelsWithoutChangingAnyDropRows()
    {
        var pve = Example() with { HasLifeskillActivity = false };
        var mixed = pve with { HasLifeskillActivity = true };
        Assert.True(LootOverlayRenderer.ShowPveBlock(mixed));
        Assert.True(LootOverlayRenderer.ShowLifeskillBlock(mixed));
        Assert.Equal(480, LootOverlayRenderer.Measure(mixed).Width);
        Assert.Equal(27, LootOverlayRenderer.RowHeight);
        Assert.Equal(100, LootOverlayRenderer.DropsTopFor(mixed) - LootOverlayRenderer.DropsTopFor(pve));
        Assert.Same(pve.Drops, mixed.Drops);
        Assert.True(LootOverlayRenderer.HarvestNumberBoundsFor(mixed).Right < LootOverlayRenderer.HarvestRateBoundsFor(mixed).Left);
        Assert.True(LootOverlayRenderer.HarvestRateBoundsFor(mixed).Bottom < LootOverlayRenderer.DropsTopFor(mixed));
    }

    [Fact]
    public void PureLifeskillHasNoEmptyKinaApOrDailyBlockEvenWithStoredCurrencyFacts()
    {
        var life = Example() with { HasPveActivity = false, HasLifeskillActivity = true };
        Assert.False(LootOverlayRenderer.ShowPveBlock(life));
        Assert.True(LootOverlayRenderer.ShowLifeskillBlock(life));
        Assert.Equal(164, LootOverlayRenderer.DropsTopFor(life));
        Assert.Equal(RectangleF.Empty, LootOverlayRenderer.SessionSegmentBounds(life));
        var unrelated = life with { SessionKina = 999_999, DailyMonsterKina = 1_000_000,
            SessionKinaPerHour = 2_000_000, SessionAbyssPoints = 500, SessionAbyssPointsRate = 1500,
            FarmedAbyssPoints = 1_500_000, AbyssPointCap = 2_000_000 };
        Assert.False(unrelated.DailyLimitReached);
        using var first = LootOverlayRenderer.RenderBitmap(life, "aion2-stylized");
        using var second = LootOverlayRenderer.RenderBitmap(unrelated, "aion2-stylized");
        Assert.Equal(Pixels(first), Pixels(second));
    }

    [Fact]
    public void OpenWorldApIsASeparateCompactBlockAndLeavesTheDailyKinaDisplayIntact()
    {
        var before = Example();
        var points = before with { HasAbyssPointActivity = true, SessionAbyssPoints = 5678, SessionAbyssPointsRate = 11356 };
        Assert.True(LootOverlayRenderer.ShowOpenWorldPointsBlock(points));
        Assert.True(LootOverlayRenderer.ShowPveBlock(points));
        Assert.Equal(76, LootOverlayRenderer.Measure(points).Height - LootOverlayRenderer.Measure(before).Height);
        Assert.Equal(LootOverlayRenderer.SessionSegmentBounds(before), LootOverlayRenderer.SessionSegmentBounds(points));
        var number = LootOverlayRenderer.OpenWorldPointsNumberBoundsFor(points);
        var rate = LootOverlayRenderer.OpenWorldPointsRateBoundsFor(points);
        Assert.True(number.Right < rate.Left); Assert.True(rate.Bottom < LootOverlayRenderer.LifeskillTopFor(points));
        var pointOnly = points with { HasPveActivity = false, HasLifeskillActivity = false };
        Assert.False(LootOverlayRenderer.ShowPveBlock(pointOnly));
        Assert.Equal(RectangleF.Empty, LootOverlayRenderer.SessionSegmentBounds(pointOnly));
        Assert.True(LootOverlayRenderer.OpenWorldPointsRateBoundsFor(pointOnly).Bottom < LootOverlayRenderer.DropsTopFor(pointOnly));
        Assert.False(LootOverlayRenderer.ShowOpenWorldPointsBlock(points with { SessionActive = false }));
        Assert.False(LootOverlayRenderer.ShowOpenWorldPointsBlock(points with { SessionKind = LootTrackerSessionKind.Abyss }));
        using var original = LootOverlayRenderer.RenderBitmap(before, "aion2-stylized");
        using var changed = LootOverlayRenderer.RenderBitmap(points, "aion2-stylized");
        Assert.NotEqual(Pixels(original), Pixels(changed));
    }

    [Theory]
    [InlineData("de", false)] [InlineData("en", false)]
    [InlineData("de", true)] [InlineData("en", true)]
    public void OpenWorldApVariantsRenderInBothLanguagesWithoutInventingACapOrKinaBlock(string language, bool kina)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var source = Example() with
            {
                HasPveActivity = kina, HasLifeskillActivity = kina, HasAbyssPointActivity = true,
                SessionAbyssPoints = 5678, SessionAbyssPointsRate = 11356,
                AbyssPointIconPath = MonsterLootIconCatalog.Default.ItemAsset(930100003),
                KinaIconPath = MonsterLootIconCatalog.Default.ItemAsset(930100031), AnimationsEnabled = false,
                Drops = kina ? [new("Item:610610013", MonsterLootItemCatalog.Default.Name(610610013, language), 57)
                    { IconPath = MonsterLootIconCatalog.Default.DropAsset("Item:610610013"),
                        Rarity = MonsterLootItemCatalog.Default.DropRarity("Item:610610013") }] : [],
                ItemCount = kina ? 57 : 0
            };
            using var image = LootOverlayRenderer.RenderBitmap(source, "aion2-stylized");
            using var unrelatedCap = LootOverlayRenderer.RenderBitmap(source with
                { FarmedAbyssPoints = 999999, AbyssPointCap = 2500000, AbyssFarmConfirmed = true }, "aion2-stylized");
            Assert.Equal(LootOverlayRenderer.Measure(source), image.Size);
            Assert.Equal(Pixels(image), Pixels(unrelatedCap));
            if (Environment.GetEnvironmentVariable("SOULCREST_OPEN_WORLD_POINTS_PREVIEW") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                image.Save(Path.Combine(output, $"loot-open-world-points-{language}-{(kina ? "mixed" : "only")}.png"), ImageFormat.Png);
            }
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public void HistoricalLifeskillSnapshotGetsItsLifeBlockWithoutCurrencyFromLegacyDefaults()
    {
        var life = Example() with { SessionKind = LootTrackerSessionKind.Lifeskill, HasLifeskillActivity = false };
        Assert.True(life.HasPveActivity);
        Assert.False(LootOverlayRenderer.ShowPveBlock(life));
        Assert.True(LootOverlayRenderer.ShowLifeskillBlock(life));
        Assert.False((life with { DailyMonsterKina = 1_000_000 }).DailyLimitReached);
    }

    [Fact]
    public void AbyssGatheringKeepsApAndKinaWithASeparateHarvestBlock()
    {
        var abyss = Example() with { SessionKind = LootTrackerSessionKind.Abyss, SessionAbyssPoints = 2000,
            SessionAbyssPointsRate = 4000, FarmedAbyssPoints = 717_427, AbyssPointCap = 2_000_000 };
        Assert.False(LootOverlayRenderer.ShowPveBlock(abyss));
        Assert.True(LootOverlayRenderer.ShowLifeskillBlock(abyss));
        Assert.False((abyss with { DailyMonsterKina = 1_000_000 }).DailyLimitReached);
        Assert.Equal(100, LootOverlayRenderer.Measure(abyss).Height
            - LootOverlayRenderer.Measure(abyss with { HasLifeskillActivity = false }).Height);
        using var image = LootOverlayRenderer.RenderBitmap(abyss, "aion2-stylized");
        Assert.Equal(LootOverlayRenderer.Measure(abyss), image.Size);
    }

    [Fact]
    public void UnknownHarvestCountsAndRatesStayVisuallyDifferentFromConfirmedZero()
    {
        var missing = Example() with { HasPveActivity = false, HarvestCount = null, FailedHarvestCount = null,
            HarvestsPerHour = null, ItemCount = null };
        var zero = missing with { HarvestCount = 0, FailedHarvestCount = 0, HarvestsPerHour = 0, ItemCount = 0 };
        using var unknown = LootOverlayRenderer.RenderBitmap(missing, "aion2-stylized");
        using var known = LootOverlayRenderer.RenderBitmap(zero, "aion2-stylized");
        Assert.Equal(unknown.Size, known.Size);
        Assert.NotEqual(Pixels(unknown), Pixels(known));
    }

    [Theory]
    [InlineData(LootItemRarity.Common, false)]
    [InlineData(LootItemRarity.Rare, true)]
    [InlineData(LootItemRarity.Legend, true)]
    [InlineData(LootItemRarity.Unique, true)]
    [InlineData(LootItemRarity.Epic, true)]
    [InlineData(LootItemRarity.Mythic, true)]
    [InlineData(LootItemRarity.Special, true)]
    public void EveryGradeHasItsRequiredMotionAndStopsWithTheAnimationOption(LootItemRarity rarity, bool animated)
    {
        var snapshot = Example() with { HasPveActivity = false, HasLifeskillActivity = false,
            Drops = [new("synthetic-grade", "Synthetic grade", 7) { Rarity = rarity }] };
        using var first = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: .25);
        using var later = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: 2.75);
        if (animated) Assert.NotEqual(Pixels(first), Pixels(later)); else Assert.Equal(Pixels(first), Pixels(later));
        Assert.Equal(animated, new LootOverlayAnimation().IsAnimating(snapshot, 0));
        foreach (var stopped in new[] { snapshot with { AnimationsEnabled = false }, snapshot with { SessionActive = false } })
        {
            using var still = LootOverlayRenderer.RenderBitmap(stopped, "aion2-stylized", animationSeconds: .25);
            using var stillLater = LootOverlayRenderer.RenderBitmap(stopped, "aion2-stylized", animationSeconds: 2.75);
            Assert.Equal(Pixels(still), Pixels(stillLater));
            Assert.False(new LootOverlayAnimation().IsAnimating(stopped, 0));
        }
    }

    [Fact]
    public void RareAndLegendHaveNoParticlesOrFindCardsWhileExceptionalGradesIncrease()
    {
        Assert.Equal(0, LootRarityVisuals.ParticleCount(LootItemRarity.Rare));
        Assert.Equal(0, LootRarityVisuals.ParticleCount(LootItemRarity.Legend));
        LootItemRarity[] grades = [LootItemRarity.Unique, LootItemRarity.Epic, LootItemRarity.Mythic, LootItemRarity.Special];
        foreach (var pair in grades.Zip(grades.Skip(1)))
        {
            Assert.True(LootRarityVisuals.ParticleCount(pair.First) < LootRarityVisuals.ParticleCount(pair.Second));
            Assert.True(LootRarityVisuals.LightingStrength(pair.First) < LootRarityVisuals.LightingStrength(pair.Second));
        }
        var snapshot = Example() with { RareDrops = [Notice(LootItemRarity.Rare), Notice(LootItemRarity.Legend)] };
        Assert.Empty(LootOverlayRenderer.RareNotices(snapshot));
        var unique = Notice(LootItemRarity.Unique);
        Assert.Same(unique, Assert.Single(LootOverlayRenderer.RareNotices(snapshot with { RareDrops = [.. snapshot.RareDrops, unique] })));
    }

    [Fact]
    public void ColorLightingOverloadKeepsItsIndependentDefaultTreatment()
    {
        using var defaults = new Bitmap(180, 45);
        using var explicitDefaults = new Bitmap(180, 45);
        using (var g = Graphics.FromImage(defaults))
            LootRarityVisuals.PaintBackground(g, new(0, 0, 180, 45), Color.Gold, 1.4, true);
        using (var g = Graphics.FromImage(explicitDefaults))
            LootRarityVisuals.PaintBackground(g, new(0, 0, 180, 45), Color.Gold, 1.4, true, 1, 1, true, 13, 3);
        Assert.Equal(Pixels(defaults), Pixels(explicitDefaults));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void WindowsReducedOrUnknownMotionRendersStillRarityAndCompletionWhileKeepingTheLootFacts(bool? windowsAnimations)
    {
        var source = Example() with { DailyMonsterKina = 1_000_000,
            Drops = [new("synthetic-unique", "Synthetic unique", 7) { Rarity = LootItemRarity.Unique }] };
        var snapshot = LootOverlayForm.ApplyMotionPreference(source, true, windowsAnimations);
        Assert.False(new LootOverlayAnimation().IsAnimating(snapshot, 0));
        Assert.Same(source.Drops, snapshot.Drops);
        Assert.Equal(source.SessionElapsed, snapshot.SessionElapsed);
        Assert.True(source.AnimationsEnabled);
        using var first = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: .25, completionSeconds: .25);
        using var later = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: 2.75, completionSeconds: 1.25);
        Assert.Equal(Pixels(first), Pixels(later));
    }

    private static RareDropNotice Notice(LootItemRarity rarity) =>
        new("synthetic-" + rarity, "Synthetic notice", 1, rarity, null, DateTimeOffset.UnixEpoch);
    private static LootOverlaySnapshot Example() => new("synthetic-mixed", true, 184_720, 684_720,
        [new("synthetic-item", "Synthetic item", 57)])
    {
        SessionKind = LootTrackerSessionKind.Mixed, HasPveActivity = true, HasLifeskillActivity = true,
        HarvestCount = 24, FailedHarvestCount = 3, HarvestsPerHour = 48, ItemCount = 57,
        SessionElapsed = TimeSpan.FromMinutes(30), SessionKinaPerHour = 369_440
    };
    private static byte[] Pixels(Bitmap image)
    {
        using var memory = new MemoryStream(); image.Save(memory, ImageFormat.Png); return memory.ToArray();
    }
}
