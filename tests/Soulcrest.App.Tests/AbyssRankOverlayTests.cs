using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AbyssRankOverlayTests
{
    [Fact]
    public void RankTrackerKeepsSavedLayoutKeyAndFitsMoreMetricsIntoASmallerPanel()
    {
        var descriptor = Assert.Single(OverlayRegistry.All, row => row.Id == "abyss-rank");
        Assert.Equal("Abyss Rank Tracker", descriptor.Title);
        Assert.True(AbyssRankOverlayRenderer.Width < 540);
        Assert.True(AbyssRankOverlayRenderer.Width * AbyssRankOverlayRenderer.Height < 540 * 172);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void TotalPointsAreInsideTheWiderBarAndLargeIconsShareItsVerticalCenter(string language)
    {
        var bar = AbyssRankOverlayRenderer.ProgressBounds;
        Assert.Equal(480, AbyssRankOverlayRenderer.Width);
        Assert.Equal(24, bar.Height);
        Assert.Equal(364, bar.Width);
        Assert.Equal(AbyssRankOverlayRenderer.SessionNumberBounds.Width, AbyssRankOverlayRenderer.SessionRateBounds.Width);
        Assert.True(AbyssRankOverlayRenderer.RemainingBounds.Bottom <= bar.Top);
        foreach (var icon in new[] { AbyssRankOverlayRenderer.CurrentIconBounds, AbyssRankOverlayRenderer.NextIconBounds })
        {
            Assert.Equal(new Size(48, 48), icon.Size);
            Assert.Equal(bar.Top + bar.Height / 2, icon.Top + icon.Height / 2);
            Assert.False(bar.IntersectsWith(icon));
        }
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            // At the maximum stage both snapshots have the same icon, remaining label and full fill.
            // Only their displayed total differs, so every changed pixel must lie inside the bar.
            var maximum = AbyssRankCatalog.Default.Stages[^1].Threshold;
            using var first = AbyssRankOverlayRenderer.RenderBitmap(new(maximum + 100, true, true), "aion2-stylized", language: language);
            using var second = AbyssRankOverlayRenderer.RenderBitmap(new(maximum + 200, true, true), "aion2-stylized", language: language);
            var changed = Positions(new(Point.Empty, first.Size))
                .Where(point => first.GetPixel(point.X, point.Y) != second.GetPixel(point.X, point.Y)).ToArray();
            Assert.NotEmpty(changed);
            Assert.All(changed, point => Assert.True(bar.Contains(point)));
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void SignedSessionNetAndHourlyLossAreVisibleInRedAndUnknownIsNotZero(string language)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var positive = OverlayPreviewRenderer.AbyssRankExample();
            var negative = positive with { SessionNetRankPoints = -573, SessionRankPointsPerHour = -1_719 };
            var unknown = positive with { SessionNetRankPoints = null, SessionRankPointsPerHour = null };
            using var gains = AbyssRankOverlayRenderer.RenderBitmap(positive, "aion2-stylized", language: language);
            using var losses = AbyssRankOverlayRenderer.RenderBitmap(negative, "aion2-stylized", language: language);
            using var missing = AbyssRankOverlayRenderer.RenderBitmap(unknown, "aion2-stylized", language: language);
            using var zero = AbyssRankOverlayRenderer.RenderBitmap(positive with { SessionNetRankPoints = 0, SessionRankPointsPerHour = 0 }, "aion2-stylized", language: language);
            Assert.NotEqual(Pixels(missing), Pixels(zero));
            foreach (var bounds in new[] { Rectangle.Ceiling(AbyssRankOverlayRenderer.SessionNumberBounds), Rectangle.Ceiling(AbyssRankOverlayRenderer.SessionRateBounds) })
            {
                Assert.Contains(Positions(bounds), point => gains.GetPixel(point.X, point.Y).G > 180 && gains.GetPixel(point.X, point.Y).R < 150);
                Assert.Contains(Positions(bounds), point => losses.GetPixel(point.X, point.Y).R > 220 && losses.GetPixel(point.X, point.Y).G < 160);
            }
            using var stopped = AbyssRankOverlayRenderer.RenderBitmap(positive with { SessionActive = false }, "aion2-stylized", language: language);
            Assert.Equal(Pixels(missing), Pixels(stopped));
            var culture = CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
            Assert.Equal((-1_719d).ToString("N0", culture), AbyssRankOverlayRenderer.SignedRateLabel(-1_719, culture));
            Assert.Equal("—", AbyssRankOverlayRenderer.SignedRateLabel(double.PositiveInfinity, culture));
            Assert.Equal("0", AbyssRankOverlayRenderer.SignedRateLabel(-.001, culture));
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EveryThemeAndScaleUsesProductionGeometryAndNoGuessedPoints(string language)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            foreach (var theme in OverlayThemeDefinition.All)
            foreach (var scale in new[] { .6, 1, 2 })
            {
                using var known = AbyssRankOverlayRenderer.RenderPreview(OverlayPreviewRenderer.AbyssRankExample(), theme.Id, scale, language: language);
                using var unknown = AbyssRankOverlayRenderer.RenderPreview(AbyssRankOverlaySnapshot.Empty, theme.Id, scale, language: language);
                Assert.Equal((int)Math.Ceiling(AbyssRankOverlayRenderer.Width * scale), known.Width);
                Assert.Equal((int)Math.Ceiling(AbyssRankOverlayRenderer.Height * scale), known.Height);
                Assert.Equal(known.Size, unknown.Size);
                Assert.NotEqual(Pixels(known), Pixels(unknown));
            }
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public void UnboundTotalsRenderExactlyAsUnknownAndNeverLeakAnotherCharactersTier()
    {
        using var unknown = AbyssRankOverlayRenderer.RenderBitmap(AbyssRankOverlaySnapshot.Empty, "aion2-stylized");
        using var unbound = AbyssRankOverlayRenderer.RenderBitmap(new(long.MaxValue, CharacterBound: false, Confirmed: true), "aion2-stylized");
        Assert.Equal(Pixels(unknown), Pixels(unbound));
    }

    [Fact]
    public void HighestTierHasNoNextIconAndCachedTotalsStayExplicitlyDistinct()
    {
        var highest = new AbyssRankOverlaySnapshot(long.MaxValue, true, true);
        using var image = AbyssRankOverlayRenderer.RenderBitmap(highest, "aion2-stylized");
        using var unknown = AbyssRankOverlayRenderer.RenderBitmap(new(null, true, true), "aion2-stylized");
        var slot = AbyssRankOverlayRenderer.NextIconBounds;
        foreach (var point in Positions(slot)) Assert.Equal(unknown.GetPixel(point.X, point.Y), image.GetPixel(point.X, point.Y));
        using var saved = AbyssRankOverlayRenderer.RenderBitmap(highest with { Confirmed = false }, "aion2-stylized");
        Assert.NotEqual(Pixels(image), Pixels(saved));
    }

    [Fact]
    public void KillFeedIsTypedBoundedAndNewestFirstWithoutGuessingFromPoints()
    {
        var at = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var entries = Enumerable.Range(0, 13).Select(index => new AbyssKillFeedEntry(at.AddSeconds(index),
            index % 2 == 0 ? AbyssKillFeedKind.Kill : AbyssKillFeedKind.Death, "Synthetic opponent", index - 4)
            { Generation = 2, Sequence = index }).ToArray();
        var snapshot = new AbyssRankOverlaySnapshot(null, true, false)
        {
            ShowDeaths = true,
            KillFeed = [.. entries, new(at.AddMinutes(1), (AbyssKillFeedKind)123, "Invalid", null)]
        };
        var selected = AbyssRankOverlayRenderer.KillFeedEntries(snapshot);
        Assert.Equal(new long[] { 12, 11, 10, 9, 8, 7, 6, 5, 4, 3 }, selected.Select(entry => entry.Sequence));
        Assert.Empty(AbyssRankOverlayRenderer.KillFeedEntries(snapshot with { CharacterBound = false }));
        Assert.Empty(AbyssRankOverlayRenderer.KillFeedEntries(snapshot with { KillFeedEnabled = false }));
        Assert.Empty(AbyssRankOverlayRenderer.KillFeedEntries(new(long.MaxValue, true, true)));
        Assert.Equal(AbyssRankOverlayRenderer.Height, AbyssRankOverlayRenderer.Measure(snapshot with { KillFeedEnabled = false }).Height);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void KillFeedExtendsEveryThemeBelowThePointsPanelAndDisablingRestoresItsOriginalPixels(string language)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var baseSnapshot = OverlayPreviewRenderer.AbyssRankExample();
            var feed = OverlayPreviewRenderer.AbyssRankExample(includeKillFeed: true) with { ShowDeaths = true };
            foreach (var theme in OverlayThemeDefinition.All)
            {
                using var plain = AbyssRankOverlayRenderer.RenderBitmap(baseSnapshot, theme.Id);
                using var withFeed = AbyssRankOverlayRenderer.RenderBitmap(feed, theme.Id);
                using var disabled = AbyssRankOverlayRenderer.RenderBitmap(feed with { KillFeedEnabled = false }, theme.Id);
                Assert.Equal(Pixels(plain), Pixels(disabled));
                Assert.Equal(AbyssRankOverlayRenderer.KillFeedTop + 3 * AbyssRankOverlayRenderer.KillFeedRowHeight + 9, withFeed.Height);
                // Some themes shade their surface over the complete panel height. Opaque progress
                // remains identical while translucent track pixels may reveal a different backdrop.
                var progressPixels = Positions(AbyssRankOverlayRenderer.ProgressBounds)
                    .Where(point => plain.GetPixel(point.X, point.Y).ToArgb() == theme.Gold.ToArgb()).ToArray();
                Assert.NotEmpty(progressPixels);
                Assert.All(progressPixels, point => Assert.Equal(plain.GetPixel(point.X, point.Y), withFeed.GetPixel(point.X, point.Y)));
                var row = new Rectangle(16, AbyssRankOverlayRenderer.KillFeedTop, withFeed.Width - 32, 3 * AbyssRankOverlayRenderer.KillFeedRowHeight);
                Assert.Contains(Positions(row), point => withFeed.GetPixel(point.X, point.Y).G > 180 && withFeed.GetPixel(point.X, point.Y).R < 150);
                Assert.Contains(Positions(row), point => withFeed.GetPixel(point.X, point.Y).R > 220 && withFeed.GetPixel(point.X, point.Y).G < 160);
                Assert.Contains(Positions(row), point => withFeed.GetPixel(point.X, point.Y).B > 220 && withFeed.GetPixel(point.X, point.Y).R < 160);
            }
            Assert.Equal(language == "de" ? "Unbekannter Gegner" : "Unknown opponent", AbyssRankOverlayRenderer.OpponentLabel("\r\n\t"));
            Assert.Equal("Syntheticopponent", AbyssRankOverlayRenderer.OpponentLabel("Synthetic\n\u202Eopponent"));
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void EachFeedCategoryCanBeSelectedIndependently(bool kills, bool deaths, bool assists)
    {
        var snapshot = OverlayPreviewRenderer.AbyssRankExample(includeKillFeed: true) with
        { ShowKills = kills, ShowDeaths = deaths, ShowAssists = assists };
        var selected = AbyssRankOverlayRenderer.KillFeedEntries(snapshot);
        AbyssKillFeedKind[] all = [AbyssKillFeedKind.Kill, AbyssKillFeedKind.Assist, AbyssKillFeedKind.Death];
        var expected = all.Where(kind => kind switch
        { AbyssKillFeedKind.Kill => kills, AbyssKillFeedKind.Assist => assists, _ => deaths });
        Assert.Equal(expected, selected.Select(entry => entry.Kind));
        if (assists) Assert.Null(Assert.Single(selected, entry => entry.Kind == AbyssKillFeedKind.Assist).RankPointDelta);
        Assert.Equal(0, (int)AbyssKillFeedKind.Kill);
        Assert.Equal(1, (int)AbyssKillFeedKind.Death);
        Assert.Equal(2, (int)AbyssKillFeedKind.Assist);
    }

    [Fact]
    public Task AHiddenOverlayNeverStopsTheSharedRankSoundItDidNotStart() => RunSta(() =>
    {
        var source = new Source(); var cancels = 0;
        var options = new AbyssRankOverlayOptions(false, -30_000, -30_000);
        using var panel = new AbyssRankOverlayForm(source, () => options, motionCanceled: () => cancels++);
        _ = panel.Handle;
        for (var i = 0; i < 3; i++)
        {
            source.Publish(new(45_000, true, true) { WorldContextVisible = false });
            Application.DoEvents();
            panel.ApplySettings();
        }
        Assert.False(panel.Visible);
        Assert.Equal(0, cancels);
    });

    [Fact]
    public Task OwnWindowRespectsVisibilityInteractionAndSourceLifetime() => RunSta(() =>
    {
        var source = new Source();
        var options = new AbyssRankOverlayOptions(false, -30_000, -30_000);
        using var panel = new AbyssRankOverlayForm(source, () => options);
        _ = panel.Handle;
        Assert.False(panel.Visible);
        Assert.Equal(1, source.Subscribers);
        options = options with { Enabled = true };
        panel.ApplySettings();
        Assert.True(panel.Visible);
        Assert.Equal(new Size(AbyssRankOverlayRenderer.Width, AbyssRankOverlayRenderer.Height), panel.Size);
        panel.ToggleManualInteractionFromHotkey();
        Assert.True(panel.ManualInteractionEnabled);
        panel.SetForegroundSuppressed(true);
        Assert.False(panel.Visible);
        Assert.False(panel.ManualInteractionEnabled);
        source.Publish(new(45_000, true, true) { WorldContextVisible = true });
        Application.DoEvents();
        Assert.False(panel.Visible);
        panel.SetForegroundSuppressed(false);
        Assert.True(panel.Visible);
        panel.SetPlacementHidden(true);
        Assert.False(panel.Visible);
        panel.SetPlacementHidden(false);
        Assert.True(panel.Visible);
        panel.SetSuppressed(true);
        Assert.False(panel.Visible);
        panel.SetSuppressed(false);
        Assert.True(panel.Visible);
        source.Publish(OverlayPreviewRenderer.AbyssRankExample(includeKillFeed: true));
        Application.DoEvents();
        Assert.Equal(AbyssRankOverlayRenderer.Measure(source.Snapshot), panel.Size);
        Assert.True(panel.Height > AbyssRankOverlayRenderer.Height);
        options = options with { KillFeedEnabled = false };
        panel.ApplySettings();
        Assert.Equal(AbyssRankOverlayRenderer.Height, panel.Height);
        options = options with { KillFeedEnabled = true };
        panel.ApplySettings();
        Assert.True(panel.Height > AbyssRankOverlayRenderer.Height);
        options = options with { ShowKills = false, ShowAssists = false, ShowDeaths = true };
        panel.ApplySettings(); // The source snapshot still disallows deaths.
        Assert.Equal(AbyssRankOverlayRenderer.Height, panel.Height);
        source.Publish(source.Snapshot with { ShowDeaths = true });
        Application.DoEvents();
        Assert.Equal(AbyssRankOverlayRenderer.KillFeedTop + AbyssRankOverlayRenderer.KillFeedRowHeight + 9, panel.Height);
        panel.ToggleManualInteractionFromHotkey();
        source.Publish(source.Snapshot with { WorldContextVisible = false });
        Application.DoEvents();
        Assert.False(panel.Visible);
        Assert.False(panel.ManualInteractionEnabled);
        Assert.Equal(options.X, panel.Location.X);
        source.Publish(source.Snapshot with { WorldContextVisible = true });
        Application.DoEvents();
        Assert.True(panel.Visible);
        Assert.False(panel.ManualInteractionEnabled);
        Assert.True(options.Enabled);
        panel.Dispose();
        Assert.Equal(0, source.Subscribers);
    });

    [Fact]
    public void SettingsPreserveIndependentTrackingPeriodAndRankLayoutAcrossRestart()
    {
        var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var language = UiText.Language;
        try
        {
            JsonFile.Save(AppPaths.SettingsFile, new { SettingsRevision = 5 });
            var originalFile = File.ReadAllBytes(AppPaths.SettingsFile);
            var settings = new SettingsService();
            Assert.True(settings.Current.AbyssTrackingEnabled);
            Assert.False(settings.Current.AbyssRankOverlayEnabled);
            Assert.True(settings.Current.AbyssKillFeedEnabled);
            Assert.True(settings.Current.AbyssKillFeedShowKills);
            Assert.False(settings.Current.AbyssKillFeedShowDeaths);
            Assert.True(settings.Current.AbyssKillFeedShowAssists);
            Assert.Equal(originalFile, File.ReadAllBytes(AppPaths.SettingsFile));
            Assert.Equal(AbyssPointRatePeriod.Hour, settings.Current.AbyssPointRatePeriod);
            settings.SetAbyssTrackingEnabled(false);
            settings.SetAbyssPointRatePeriod(AbyssPointRatePeriod.Minute);
            settings.SetAbyssRankOverlayEnabled(true);
            settings.SetAbyssKillFeedEnabled(false);
            settings.SetAbyssKillFeedShowKills(false);
            settings.SetAbyssKillFeedShowDeaths(true);
            settings.SetAbyssKillFeedShowAssists(false);
            settings.Update(value => OverlayRegistry.WriteLayout(value, "abyss-rank", new(-1200, 80, 1.4)));
            var loaded = new SettingsService().Current;
            Assert.False(loaded.AbyssTrackingEnabled);
            Assert.True(loaded.AbyssRankOverlayEnabled);
            Assert.False(loaded.AbyssKillFeedEnabled);
            Assert.False(loaded.AbyssKillFeedShowKills);
            Assert.True(loaded.AbyssKillFeedShowDeaths);
            Assert.False(loaded.AbyssKillFeedShowAssists);
            Assert.Equal(AbyssPointRatePeriod.Minute, loaded.AbyssPointRatePeriod);
            Assert.Equal(new(-1200, 80, 1.4), OverlayRegistry.ReadLayout(loaded, "abyss-rank"));
            settings.SetAbyssPointRatePeriod((AbyssPointRatePeriod)999);
            Assert.Equal(AbyssPointRatePeriod.Hour, new SettingsService().Current.AbyssPointRatePeriod);
            Assert.Null(OverlayHotkeys.Defaults().GetValueOrDefault("abyss-rank.toggle"));
            Assert.True(OverlayVisibilityPolicy.Get(loaded, "abyss-rank").Instances);
        }
        finally
        {
            if (backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, backup);
            UiText.Language = language;
        }
    }

    private static IEnumerable<Point> Positions(Rectangle bounds) => Enumerable.Range(bounds.Top, bounds.Height)
        .SelectMany(y => Enumerable.Range(bounds.Left, bounds.Width).Select(x => new Point(x, y)));
    private static byte[] Pixels(Bitmap image)
    {
        using var output = new MemoryStream();
        image.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
    private sealed class Source : IAbyssRankOverlaySource
    {
        private Action? _changed;
        public AbyssRankOverlaySnapshot Snapshot { get; private set; } = AbyssRankOverlaySnapshot.Empty with { WorldContextVisible = true };
        public int Subscribers { get; private set; }
        public event Action? Changed
        {
            add { _changed += value; Subscribers++; }
            remove { _changed -= value; Subscribers--; }
        }
        public void Publish(AbyssRankOverlaySnapshot snapshot) { Snapshot = snapshot; _changed?.Invoke(); }
    }
    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}

[Collection("Settings file")]
public sealed class AbyssRankOverlayRenderExploration
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void RenderSyntheticPointsTiers()
    {
        var output = TestPrerequisites.RequiredValue("SOULCREST_ABYSS_RANK_PNG_DIRECTORY");
        Directory.CreateDirectory(output);
        var previous = UiText.Language;
        try
        {
            foreach (var language in new[] { "de", "en" })
            {
                UiText.Language = language;
                foreach (var (name, snapshot) in new (string, AbyssRankOverlaySnapshot)[]
                {
                    ("regular", OverlayPreviewRenderer.AbyssRankExample()),
                    ("killfeed", OverlayPreviewRenderer.AbyssRankExample(includeKillFeed: true)),
                    ("killfeed-all", OverlayPreviewRenderer.AbyssRankExample(includeKillFeed: true) with { ShowDeaths = true }),
                    ("killfeed-assists", OverlayPreviewRenderer.AbyssRankExample(includeKillFeed: true) with { ShowKills = false }),
                    ("killfeed-off", OverlayPreviewRenderer.AbyssRankExample(includeKillFeed: true) with { KillFeedEnabled = false }),
                    ("maximum", new(AbyssRankCatalog.Default.Stages[^1].Threshold + 12_500, true, true)),
                    ("unknown", new(null, true, true)),
                    ("saved", OverlayPreviewRenderer.AbyssRankExample() with { Confirmed = false })
                })
                {
                    using var image = AbyssRankOverlayRenderer.RenderPreview(snapshot, "aion2-stylized", 1.8, language: language);
                    image.Save(Path.Combine(output, $"abyss-points-tier-{language}-{name}.png"), ImageFormat.Png);
                }
                var example = OverlayPreviewRenderer.FarmLootExample(language) with
                {
                    SessionKind = LootTrackerSessionKind.Abyss, DailyMonsterKina = null,
                    SessionAbyssPoints = 23_450, SessionAbyssPointsRate = 50_250,
                    FarmedAbyssPoints = 1_234_500, AbyssPointCap = 2_000_000,
                    AbyssFarmConfirmed = true, SessionKinaKnown = false, SessionKinaPerHour = null,
                    AbyssPointIconPath = MonsterLootIconCatalog.Default.ItemAsset(930100003)
                };
                foreach (var (name, snapshot) in new (string, LootOverlaySnapshot)[]
                {
                    ("known", example),
                    ("minute", example with { AbyssPointRatePeriod = AbyssPointRatePeriod.Minute, SessionAbyssPointsRate = 837.5 }),
                    ("unknown", example with { SessionAbyssPoints = null, SessionAbyssPointsRate = null,
                        FarmedAbyssPoints = null, AbyssFarmConfirmed = false }),
                    ("saved", example with { AbyssFarmConfirmed = false })
                })
                {
                    using var loot = LootOverlayRenderer.RenderPreview(snapshot, "aion2-stylized", 1.8, animationSeconds: 1.4);
                    loot.Save(Path.Combine(output, $"abyss-loot-{language}-{name}.png"), ImageFormat.Png);
                    if (name != "known") continue;
                    using var rank = AbyssRankOverlayRenderer.RenderPreview(OverlayPreviewRenderer.AbyssRankExample(includeKillFeed: true), "aion2-stylized", 1.8, language: language);
                    using var scene = new Bitmap(loot.Width + rank.Width + 24, Math.Max(loot.Height, rank.Height), PixelFormat.Format32bppPArgb);
                    using (var graphics = Graphics.FromImage(scene))
                    {
                        graphics.Clear(Color.Transparent);
                        graphics.DrawImageUnscaled(loot, Point.Empty);
                        graphics.DrawImageUnscaled(rank, new Point(loot.Width + 24, 0));
                    }
                    scene.Save(Path.Combine(output, $"abyss-overlays-{language}.png"), ImageFormat.Png);
                }
            }
        }
        finally { UiText.Language = previous; }
    }
}
