using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossNavigationRenderingTests
{
    private static MapPlacement Placement(bool world = false) => new("map", new MapFix(1, 0, 0, 0, 1, 0, 0, 0, 0, false),
        1, new Rectangle(0, 0, 700, 300), new(40, 220), WorldMap: world, PlayerWorld: new(40, 220));

    private static MapTarget Target(int id, double x, double y, string role, string? after = null, bool cached = false) =>
        new($"boss:1:{id}", "map", x, y, "Boss " + id, "Boss Rush Mode", null, After: after,
            Color: cached || role == "spawned" ? "#4ade80" : "#ef4444", BossSpawnId: id, BossRole: role, BossProvisional: cached);

    private static BossRushEntry Row(int id, bool spawned = false, int seconds = 60, bool cached = false, string map = "map", double? spawnElapsed = null) =>
        new(new(1, map, new(id, id, "Boss " + id, null, 250, 100)), spawned,
            TimeSpan.FromSeconds(seconds), TimeSpan.Zero, cached,
            SpawnElapsed: spawnElapsed is { } elapsed ? TimeSpan.FromSeconds(elapsed) : null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpawnedBossesAndNextSpawnStartAtThePlayerWhileThePreviewFollowsTheNextSpawn(bool world)
    {
        var first = Target(1, 120, 60, "spawned");
        var second = Target(2, 260, 110, "spawned");
        var next = Target(3, 300, 220, "next");
        var preview = Target(4, 300, 60, "preview", next.Id);
        using var image = RouteOverlayForm.Draw(Placement(world), [(first, -2), (second, -3), (next, -4), (preview, -5)]);
        AssertColor(image.GetPixel(80, 140), ColorTranslator.FromHtml("#4ade80"), 255);
        AssertColor(image.GetPixel(150, 165), ColorTranslator.FromHtml("#4ade80"), 255);
        AssertColor(image.GetPixel(160, 220), ColorTranslator.FromHtml("#ef4444"), 255);
        Assert.Contains(In(new(298, 100, 5, 70), image), pixel => pixel.A is >= 104 and <= 106 && pixel.R > 200 && pixel.G < 100);
    }

    [Fact]
    public void MissingBossPredecessorNeverCreatesAFallbackLineFromThePlayer()
    {
        var preview = Target(4, 280, 50, "preview", "boss:1:missing");
        using var image = RouteOverlayForm.Draw(Placement(), [(preview, -2)]);
        Assert.All(In(new(155, 130, 10, 10), image), pixel => Assert.Equal(0, pixel.A));
        Assert.Contains(In(new(291, 45, 7, 10), image), pixel => pixel.A > 0);
        // Ordinary hand-marked routes retain their existing missing-predecessor fallback.
        var ordinary = preview with { BossSpawnId = null, BossRole = null };
        using var fallback = RouteOverlayForm.Draw(Placement(), [(ordinary, 0)]);
        Assert.Contains(In(new(155, 130, 10, 10), fallback), pixel => pixel.A > 0);
    }

    [Fact]
    public void ForeignWorldmapKeepsBossMarkersAndCountersWithoutPlayerLines()
    {
        var target = Target(1, 250, 100, "spawned");
        using var image = RouteOverlayForm.Draw(Placement(true) with { PlayerWorld = null }, [(target, -2)],
            bossCounters: new Dictionary<int, string> { [1] = "Spawned" });
        Assert.All(In(new(140, 155, 10, 10), image), pixel => Assert.Equal(0, pixel.A));
        Assert.Contains(In(new(270, 110, 140, 24), image), pixel => pixel.A > 0);
    }

    [Theory]
    [InlineData("spawned")]
    [InlineData("next")]
    [InlineData("preview")]
    public void EveryProvisionalRoleUsesFadedMarkerOpacity(string role)
    {
        var target = Target(1, 250, 100, role, cached: true);
        using var image = RouteOverlayForm.Draw(Placement(), [(target, -2)]);
        Assert.Contains(In(new(140, 155, 10, 10), image), pixel => pixel.A is >= 104 and <= 106 && pixel.G > pixel.R);
        // The black A50 outline under the A105 ring raises composited pixel alpha to 134.
        // Recover the green layer's coverage; an unfaded ring reaches 255, even with antialiasing.
        var ringAlpha = In(new(263, 90, 5, 20), image).Where(pixel => pixel.G > pixel.R)
            .Select(pixel => (int)Math.Round(pixel.A * pixel.G / 222d)).DefaultIfEmpty(0).Max();
        Assert.InRange(ringAlpha, 1, 106);
    }

    [Theory]
    [InlineData("de", true, 0, false, "Gespawnt")]
    [InlineData("en", true, 0, false, "Spawned")]
    [InlineData("de", false, 61, false, "00:01:01")]
    [InlineData("en", false, 0, false, "Waiting for confirmation")]
    [InlineData("de", false, 0, false, "Warte auf Bestätigung")]
    [InlineData("de", true, 0, true, "Unbestätigt")]
    [InlineData("en", false, 61, true, "Unconfirmed · 00:01:01")]
    [InlineData("de", false, 0, true, "Unbestätigt · Warte auf Bestätigung")]
    public void CountersPreserveConfirmedAndProvisionalStates(string language, bool spawned, int seconds, bool cached, string expected)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            Assert.Equal(expected, RouteOverlayForm.BossCounter(Row(1, spawned, seconds, cached)));
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData("de", 0, false, "Gespawnt · 00:00")]
    [InlineData("de", 25.999, false, "Gespawnt · 00:25")]
    [InlineData("en", 25.999, false, "Spawned · 00:25")]
    [InlineData("de", 59.999, false, "Gespawnt · 00:59")]
    [InlineData("de", 60, false, "Gespawnt · 01:00")]
    [InlineData("de", 125, false, "Gespawnt · 02:05")]
    [InlineData("en", 3600, false, "Spawned · 60:00")]
    [InlineData("de", 25.999, true, "Unbestätigt · 00:25")]
    [InlineData("en", 60, true, "Unconfirmed · 01:00")]
    [InlineData("de", 3600, true, "Unbestätigt · 60:00")]
    [InlineData("de", -1, false, "Gespawnt")]
    [InlineData("en", -1, true, "Unconfirmed")]
    public void SpawnedCountersShowElapsedServerTimeWithFlooredSeconds(string language, double elapsedSeconds, bool cached, string expected)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var row = Row(1, spawned: true, seconds: 0, cached: cached, spawnElapsed: elapsedSeconds)
                with { Age = TimeSpan.FromMinutes(9) };
            Assert.Equal(expected, RouteOverlayForm.BossCounter(row));
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimerTicksReusePreparedTargetsAndDistanceStringsForAStationaryPlayer(bool spawned)
    {
        var target = Target(1, 250, 100, spawned ? "spawned" : "next");
        var prepared = new RouteOverlayForm.PreparedOverlay("map", [(target, -2)], new HashSet<string> { target.Id },
            [], [], [], null, 1, new Dictionary<string, Bitmap?>(), [], []);
        var cache = new PreparationCache<string, RouteOverlayForm.PreparedOverlay>();
        var distances = new RouteOverlayForm.TargetDistanceCache();
        var position = new PlayerPosition("map", 40, 220, DateTimeOffset.UnixEpoch);
        var firstPreparation = cache.Get("map", () => prepared);
        var firstTargets = distances.Get(firstPreparation, position, "en");
        var firstRow = Row(1, spawned, seconds: 61, spawnElapsed: spawned ? 25.999 : null);
        var nextRow = Row(1, spawned, seconds: 60, spawnElapsed: spawned ? 26 : null);
        var firstCounters = RouteOverlayForm.BossCounters("map", [firstRow, Row(1, seconds: 300, map: "other")]);
        using var first = RouteOverlayForm.Draw(Placement(), firstTargets, bossCounters: firstCounters);
        var nextPreparation = cache.Get("map", () => throw new InvalidOperationException("A timer tick must not rebuild map candidates."));
        var nextTargets = distances.Get(nextPreparation, position, "en");
        var nextCounters = RouteOverlayForm.BossCounters("map", [nextRow]);
        using var next = RouteOverlayForm.Draw(Placement(), nextTargets, bossCounters: nextCounters);
        Assert.Same(firstPreparation, nextPreparation);
        Assert.Same(firstTargets, nextTargets);
        Assert.Equal(spawned ? $"{UiText.T("Gespawnt")} · 00:25" : "00:01:01", firstCounters[1]);
        Assert.Equal(spawned ? $"{UiText.T("Gespawnt")} · 00:26" : "00:01:00", nextCounters[1]);
        Assert.Equal("Boss 1", prepared.Targets[0].Target.Name);
        var changedLabel = new Rectangle(269, 85, 250, 55);
        Assert.False(In(changedLabel, first).Select(pixel => pixel.ToArgb()).SequenceEqual(In(changedLabel, next).Select(pixel => pixel.ToArgb())));
        for (var y = 0; y < first.Height; y++)
        for (var x = 0; x < first.Width; x++)
            if (!changedLabel.Contains(x, y)) Assert.Equal(first.GetPixel(x, y), next.GetPixel(x, y));
    }

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void RenderBossNavigationOnLocalMapTiles()
    {
        var directory = TestPrerequisites.MapDataDirectory;
        Assert.NotNull(directory);
        var output = TestPrerequisites.RequiredValue("SOULCREST_BOSS_NAVIGATION_PREVIEW");
        Directory.CreateDirectory(output);
        using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "altgard", "bosses.json")));
        int[] ids = [111007, 111008, 111013, 111024];
        var places = ids.Select(id => catalogue.RootElement.GetProperty("bosses").EnumerateArray().Single(boss => boss.GetProperty("spawnId").GetInt32() == id))
            .Select(boss => new BossPlace(boss.GetProperty("spawnId").GetInt32(), boss.GetProperty("npcId").GetInt32(),
                boss.GetProperty("en").GetString()!, boss.GetProperty("de").GetString(), boss.GetProperty("x").GetDouble(), boss.GetProperty("y").GetDouble())).ToArray();
        var previous = UiText.Language;
        try
        {
            UiText.Language = "de";
            Render("worldmap", 1000, 660, .34, 3400, 2300, world: true, cached: false);
            Render("minimap", 620, 420, .23, 3400, 2500, world: false, cached: false);
            Render("worldmap-cached", 1000, 660, .34, 3400, 2300, world: true, cached: true);
        }
        finally { UiText.Language = previous; }

        void Render(string name, int width, int height, double scale, double cropX, double cropY, bool world, bool cached)
        {
            const double playerX = 4700, playerY = 4050;
            var fix = new MapFix(scale, 0, -cropX * scale, 0, scale, -cropY * scale, 100, 100, .3, false);
            var placement = new MapPlacement("altgard", fix, 1, new Rectangle(0, 0, width, height),
                new((playerX - cropX) * scale, (playerY - cropY) * scale), WorldMap: world, PlayerWorld: new(playerX, playerY));
            var rows = places.Select((place, index) => new BossRushEntry(new(1110, "altgard", place), index < 2,
                TimeSpan.FromSeconds(index < 2 ? 0 : index == 2 ? 107 : 312), TimeSpan.FromMinutes(cached ? 9 : 0), cached,
                SpawnElapsed: index < 2 ? TimeSpan.FromSeconds(index == 0 ? 20 : 45) : null)).ToArray();
            var targets = rows.Select((row, index) => (Target: new MapTarget($"boss:1110:{row.Place.Boss.SpawnId}", "altgard",
                row.Place.Boss.X!.Value, row.Place.Boss.Y!.Value, row.Place.Boss.Name("de"), "Boss Rush Mode", null,
                After: index == 3 ? $"boss:1110:{places[2].SpawnId}" : null,
                Color: cached || index < 2 ? "#4ade80" : "#ef4444", BossSpawnId: row.Place.Boss.SpawnId,
                BossRole: index < 2 ? "spawned" : index == 2 ? "next" : "preview", BossProvisional: cached), Index: -2 - index)).ToArray();
            using var layer = RouteOverlayForm.DrawThemed(OverlayThemeDefinition.Resolve(OverlayThemeDefinition.DefaultId), placement, targets,
                bossCounters: RouteOverlayForm.BossCounters("altgard", rows));
            using var image = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.FromArgb(29, 39, 47));
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                var tilePath = Path.Combine(directory, "altgard", "tiles512", $"3_{x}_{y}.webp");
                Assert.True(File.Exists(tilePath), $"Local map tile missing: {tilePath}");
                using var tile = OpenCvSharp.Cv2.ImRead(tilePath);
                using var stream = new MemoryStream(tile.ToBytes(".png"));
                using var terrain = Image.FromStream(stream);
                var start = placement.WorldToScreen(x * 2048, y * 2048);
                var end = placement.WorldToScreen((x + 1) * 2048, (y + 1) * 2048);
                graphics.DrawImage(terrain, new RectangleF(start.X, start.Y, end.X - start.X, end.Y - start.Y));
            }
            graphics.DrawImageUnscaled(layer, 0, 0);
            var player = placement.PlayerOnScreen!.Value;
            using var playerFill = new SolidBrush(Color.FromArgb(232, 245, 255));
            using var playerEdge = new Pen(Color.FromArgb(22, 37, 48), 2);
            PointF[] arrow = [new(player.X, player.Y - 10), new(player.X + 7, player.Y + 8), new(player.X, player.Y + 4), new(player.X - 7, player.Y + 8)];
            graphics.FillPolygon(playerFill, arrow);
            graphics.DrawPolygon(playerEdge, arrow);
            image.Save(Path.Combine(output, "boss-rush-" + name + ".png"), ImageFormat.Png);
        }
    }

    private static IEnumerable<Color> In(Rectangle area, Bitmap image) =>
        Enumerable.Range(area.Top, area.Height).SelectMany(y => Enumerable.Range(area.Left, area.Width).Select(x => image.GetPixel(x, y)));

    private static void AssertColor(Color actual, Color expected, int alpha)
    {
        Assert.InRange(actual.A, alpha - 1, alpha);
        Assert.InRange(actual.R, expected.R - 1, expected.R + 1);
        Assert.InRange(actual.G, expected.G - 1, expected.G + 1);
        Assert.InRange(actual.B, expected.B - 1, expected.B + 1);
    }
}
