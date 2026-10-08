using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Soulcrest.Ocr;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationCompletionTests
{
    private static readonly ExplorationLayout Layout = ExplorationLayout.ForWindow(new Size(2559, 1439));
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "exploration-list", name + ".png");
    private static ExplorationPlace Place(string id, string kind = "dungeon", string map = "altgard") => new(id, map, kind, 0, "Hidden Vein Cave Entrance", "Eingang zur Versteckten Erzaderhöhle");
    private static OcrLine[] Lines(string heading = "Versiegelter Dungeon", string percent = "100 %", string map = "Karte: Altgard") =>
        [new(map, 85, 30, 270, 40), new(heading, 120, 220, 310, 30), new(percent, 365, 270, 65, 25)];
    private static Bitmap EmptyCapture()
    {
        var bitmap = new Bitmap(Layout.CaptureBounds.Width, Layout.CaptureBounds.Height);
        using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.FromArgb(65, 65, 65));
        return bitmap;
    }

    [Theory]
    [InlineData("en-top", 14, false)]
    [InlineData("en-end", 9, true)]
    public async Task OriginalDungeonScreenshotsRequireNameAndCompletionSymbol(string fixture, int count, bool end)
    {
        using var bitmap = new Bitmap(Fixture(fixture));
        var lines = await WindowsOcrLineReader.Create("auto", 1).ReadAsync(bitmap);
        var places = new ExplorationService(new ProgressService()).Places;
        var page = ExplorationList.Read(lines, places, bitmap, Layout, "altgard");
        Assert.Equal("dungeon", page.Kind);
        Assert.False(page.Full);
        Assert.True(page.Ids.Count == count, $"Expected {count}, got {page.Ids.Count}. OCR: {string.Join("; ", lines.Select(l => l.Text))}. Symbols: {string.Join("; ", ExplorationCompletionSymbol.Find(bitmap, Layout))}. Rows: {string.Join("; ", page.Rows!.Select(r => $"{r.Line.Text}={r.Complete}/{r.Id}"))}");
        Assert.Equal(end, page.End);
        var confirmation = new ExplorationScanService.Confirmation();
        Assert.Empty(confirmation.Observe(page));
        Assert.Equal(count, confirmation.Observe(page).Length);
    }

    [Theory]
    [InlineData("Versiegelter Dungeon", "dungeon")]
    [InlineData("Sealed Dungeon", "dungeon")]
    [InlineData("Garnison", "stronghold")]
    [InlineData("Stronghold", "stronghold")]
    public void HundredPercentCompletesOnlyItsCategoryMapAndCapturedCharacterAfterTwoFrames(string heading, string kind)
    {
        using var bitmap = EmptyCapture();
        ExplorationPlace[] places = [Place("d1"), Place("d2"), Place("s1", "stronghold"), Place("k1", "kibelisk"), Place("other-map", kind, "verteron")];
        var page = ExplorationList.Read(Lines(heading), places, bitmap, Layout, "altgard");
        Assert.True(page.Full); Assert.Empty(page.Rows!); Assert.Empty(page.Unmatched);
        var confirmation = new ExplorationScanService.Confirmation();
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-completion-" + Guid.NewGuid(), "exploration.json");
        try
        {
            var service = new ExplorationService(path, places);
            var character = service.ActiveId;
            service.SetDone(character, confirmation.Observe(page));
            Assert.Empty(service.Completed); Assert.False(confirmation.Finished);
            service.Add("Second");
            service.SetDone(character, confirmation.Observe(page));
            Assert.True(confirmation.FullConfirmed); Assert.True(confirmation.Finished);
            Assert.Empty(service.Completed);
            service.Select(character);
            Assert.Equal(places.Where(p => p.Map == "altgard" && p.Kind == kind).Select(p => p.Id).Order(), service.Completed.Order());
            var reloaded = new ExplorationService(path, places);
            Assert.Equal(service.Completed.Order(), reloaded.Completed.Order());
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData("Versiegelter Dungeon", "de-DE", "dungeon")]
    [InlineData("Sealed Dungeon", "en-US", "dungeon")]
    [InlineData("Garnison", "de-DE", "stronghold")]
    [InlineData("Stronghold", "en-US", "stronghold")]
    public async Task WindowsOcrReadsHundredPercentWithItsGermanOrEnglishCategory(string heading, string language, string kind)
    {
        if (!WindowsOcrLineReader.AvailableLanguages().Contains(language))
        {
            // Without that language pack (e.g. German on the GitHub build) skipped, as all OCR tests; required locally.
            Assert.False(Environment.GetEnvironmentVariable("SOULCREST_REQUIRE_WINDOWS_OCR") == "1", $"Windows OCR {language} fehlt.");
            return;
        }
        // Synthetic typography exercises OCR and the geometric association; no DE screenshot is claimed.
        using var bitmap = EmptyCapture();
        using (var g = Graphics.FromImage(bitmap))
        {
            using var font = new Font("Segoe UI", 27, FontStyle.Bold, GraphicsUnit.Pixel);
            using var title = new Font("Segoe UI", 37, GraphicsUnit.Pixel);
            g.DrawString(language == "de-DE" ? "Karte: Altgard" : "Map: Altgard", title, Brushes.White, 85, 30);
            g.DrawString(heading, font, Brushes.White, 120, 220);
            g.DrawString("100 %", font, Brushes.White, 365, 270);
        }
        var lines = await WindowsOcrLineReader.Create(language, 1).ReadAsync(bitmap);
        var page = ExplorationList.Read(lines, [Place("d1"), Place("s1", "stronghold")], bitmap, Layout, "altgard");
        Assert.True(page.Full, string.Join("; ", lines.Select(l => l.Text)));
        Assert.Equal(kind, page.Kind);
        Assert.Equal([kind == "dungeon" ? "d1" : "s1"], page.Ids);
    }

    [Theory]
    [InlineData("99 %")]
    [InlineData("10 %")]
    [InlineData("100")]
    [InlineData("1000 %")]
    [InlineData("100 % gesammelt")]
    [InlineData("I0 %")]
    [InlineData("1O1 %")]
    [InlineData("I99 %")]
    public void OtherOrIncompletePercentagesCannotCompleteAnEmptyList(string percent)
    {
        using var bitmap = EmptyCapture();
        var page = ExplorationList.Read(Lines(percent: percent), [Place("a")], bitmap, Layout, "altgard");
        Assert.False(page.Full); Assert.Empty(page.Ids);
    }

    [Theory]
    [InlineData("IOO %")]
    [InlineData("1OO %")]
    [InlineData("l00%")]
    public void HundredPercentGlyphConfusionsAreCorrectedOnlyInItsPercentageField(string percentage)
    {
        using var bitmap = EmptyCapture();
        var places = new[] { Place("a") };
        var page = ExplorationList.Read(Lines(percent: percentage), places, bitmap, Layout, "altgard");
        Assert.True(page.Full);
        var confirmation = new ExplorationScanService.Confirmation();
        Assert.Empty(confirmation.Observe(page));
        Assert.Equal(["a"], confirmation.Observe(page));
        var misplaced = Lines(percent: percentage).Select((l, i) => i == 2 ? l with { Y = 500 } : l).ToArray();
        Assert.False(ExplorationList.Read(misplaced, places, bitmap, Layout, "altgard").Full);
        Assert.False(ExplorationList.Read(Lines(heading: "Kibelisk", percent: percentage), places, bitmap, Layout, "altgard").Full);
    }

    [Fact]
    public void PercentNeedsOwnHeadingAndMapAndCannotComeFromAnotherScreenRegion()
    {
        using var bitmap = EmptyCapture();
        var places = new[] { Place("a"), Place("s", "stronghold") };
        foreach (var lines in new[] {
            Lines(map: "Karte: Verteron"), Lines(map: "Pet-Kenntnis"), Lines(heading: "Kibelisk"),
            Lines(heading: "Chat"), Lines()[..2],
            Lines().Select((l, i) => i == 2 ? l with { Y = 700 } : l).ToArray(),
            Lines().Select((l, i) => i == 2 ? l with { X = 510 } : l).ToArray(),
            Lines().Append(new OcrLine("Garnison", 100, 220, 200, 30)).ToArray(),
            Lines().Append(new OcrLine("99 %", 365, 300, 60, 25)).ToArray() })
        {
            var page = ExplorationList.Read(lines, places, bitmap, Layout, "altgard");
            Assert.False(page.Full); Assert.Empty(page.Ids);
        }
        Assert.Null(ExplorationList.Read(Lines(heading: "Garnison"), [Place("a")], bitmap, Layout, "altgard").Kind);
    }

    [Fact]
    public void BothClientLanguagesOfAMapTitleAreAccepted()
    {
        // Review 2026-10-08: the manifest names Reshanta in German, the English client shows the data.js label.
        var service = new ExplorationService(new ProgressService());
        var titles = service.MapTitles("chaotic-middle-reshanta");
        if (titles.Count < 2)
            return; // map data package not built here
        Assert.True(Layout.ShowsMap(Lines(map: "Map: Chaotic Middle Reshanta"), titles));
        Assert.True(Layout.ShowsMap(Lines(map: "Karte: Chaotische Mittlere Ebene von Reshanta"), titles));
        Assert.False(Layout.ShowsMap(Lines(map: "Map: Altgard"), titles));
    }

    [Fact]
    public void MapTitleUsesCatalogLabelInsteadOfInternalId()
    {
        var service = new ExplorationService(new ProgressService());
        Assert.Equal("Eltnen", service.MapTitle("elthen"));
        Assert.True(Layout.ShowsMap(Lines(map: "Map: Eltnen"), service.MapTitle("elthen")));
        Assert.False(Layout.ShowsMap(Lines(map: "Map: Altgard"), service.MapTitle("elthen")));
    }

    [Fact]
    public void CategorySwitchMissingFrameAndPercentageChangeResetFullConfirmation()
    {
        var c = new ExplorationScanService.Confirmation();
        ExplorationScanService.Page dungeon = new(["d1", "d2"], [], false, "dungeon", true, []);
        ExplorationScanService.Page stronghold = new(["s1"], [], false, "stronghold", true, []);
        Assert.Empty(c.Observe(dungeon));
        Assert.Empty(c.Observe(stronghold)); Assert.False(c.Finished);
        Assert.Empty(c.Observe(new([], [], false)));
        Assert.Empty(c.Observe(stronghold));
        Assert.Equal(["s1"], c.Observe(stronghold));
        Assert.Empty(c.Observe(stronghold with { Full = false })); Assert.False(c.Finished);
        Assert.Empty(c.Observe(stronghold)); Assert.False(c.Finished);
        Assert.Equal(["s1"], c.Observe(stronghold));
    }

    [Theory]
    [InlineData(.65)]
    [InlineData(.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void UserSymbolIsRecognizedAtDifferentScales(double scale)
    {
        using var bitmap = EmptyCapture();
        using var symbol = new Bitmap(Fixture("complete-symbol"));
        var bounds = new Rectangle(385, 370, (int)(symbol.Width * scale), (int)(symbol.Height * scale));
        using (var g = Graphics.FromImage(bitmap)) g.DrawImage(symbol, bounds);
        var hit = Assert.Single(ExplorationCompletionSymbol.Find(bitmap, Layout));
        Assert.InRange(Math.Abs(ExplorationCompletionSymbol.CenterY(hit) - ExplorationCompletionSymbol.CenterY(bounds)), 0, 5);
    }

    [Fact]
    public void GoldCircleStarWhiteCheckAndWrongColumnAreNotCompletionSymbols()
    {
        using var bitmap = EmptyCapture();
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var gold = new Pen(Color.FromArgb(230, 208, 125), 2);
            g.DrawEllipse(gold, 385, 380, 29, 29);
            PointF[] star = [new(400, 480), new(404, 490), new(415, 490), new(406, 497), new(410, 508), new(400, 501), new(390, 508), new(394, 497), new(385, 490), new(396, 490)];
            using var brush = new SolidBrush(gold.Color); g.FillPolygon(brush, star);
            g.DrawEllipse(Pens.White, 385, 580, 29, 29);
            g.DrawLines(Pens.White, [new Point(391, 592), new Point(399, 600), new Point(419, 577)]);
            using var symbol = new Bitmap(Fixture("complete-symbol"));
            g.DrawImageUnscaled(symbol, 100, 680);
        }
        Assert.Empty(ExplorationCompletionSymbol.Find(bitmap, Layout));
    }

    [Fact]
    public async Task MissingCheckDoesNotBorrowNextRowsCheckOrClearExistingCompletion()
    {
        using var bitmap = new Bitmap(Fixture("en-top"));
        var lines = await WindowsOcrLineReader.Create("en-US", 1).ReadAsync(bitmap);
        var places = new ExplorationService(new ProgressService()).Places;
        var original = ExplorationList.Read(lines, places, bitmap, Layout, "altgard");
        var first = original.Rows!.First(); Assert.True(first.Complete); Assert.NotNull(first.Id);
        using (var g = Graphics.FromImage(bitmap))
            g.FillRectangle(Brushes.DarkSlateGray, new Rectangle(365, (int)first.Line.Y - 15, 65, (int)first.Line.Height + 30));
        var without = ExplorationList.Read(lines, places, bitmap, Layout, "altgard");
        Assert.DoesNotContain(first.Id, without.Ids);
        Assert.Equal(original.Ids.Count - 1, without.Ids.Count);
        var confirmation = new ExplorationScanService.Confirmation();
        confirmation.Observe(original);
        Assert.DoesNotContain(first.Id, confirmation.Observe(without));
        Assert.Equal(without.Ids.Order(), confirmation.Observe(without).Order());
    }

    [Fact]
    public void UnreadableOrMissingSymbolDoesNotResetPreviouslyCompletedPlaces()
    {
        using var bitmap = EmptyCapture();
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-preserve-completion-" + Guid.NewGuid(), "exploration.json");
        try
        {
            var service = new ExplorationService(path, [Place("a")]);
            service.SetDone(service.ActiveId, ["a"]);
            var lines = Lines(percent: "31 %").Append(new OcrLine("Hidden Vein Cave", 40, 400, 270, 25)).ToArray();
            var page = ExplorationList.Read(lines, service.Places, bitmap, Layout, "altgard");
            Assert.False(Assert.Single(page.Rows!).Complete);
            var confirmation = new ExplorationScanService.Confirmation();
            service.SetDone(service.ActiveId, confirmation.Observe(page));
            service.SetDone(service.ActiveId, confirmation.Observe(page));
            Assert.Equal(["a"], service.Completed);
            Assert.Equal(["a"], new ExplorationService(path, service.Places).Completed);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void NameAndSymbolStayAssociatedAcrossWindowResolutions(int width, int height)
    {
        var layout = ExplorationLayout.ForWindow(new Size(width, height));
        var scale = height / 1439d;
        using var bitmap = new Bitmap(layout.CaptureBounds.Width, layout.CaptureBounds.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(65, 65, 65));
            using var symbol = new Bitmap(Fixture("complete-symbol"));
            g.DrawImage(symbol, new Rectangle((int)(385 * scale), (int)(390 * scale), (int)(symbol.Width * scale), (int)(symbol.Height * scale)));
        }
        var lines = Lines(percent: "31 %").Append(new OcrLine("Hidden Vein Cave", 40, 400, 270, 25))
            .Select(l => l with { X = l.X * scale, Y = l.Y * scale, Width = l.Width * scale, Height = l.Height * scale }).ToArray();
        var page = ExplorationList.Read(lines, [Place("a")], bitmap, layout, "altgard");
        Assert.Equal(["a"], page.Ids);
    }

    [Fact]
    public void ForeignTextOutsideTheListDoesNotEnterUnmatchedResults()
    {
        using var bitmap = EmptyCapture();
        var lines = Lines(percent: "31 %").Concat(new OcrLine[] {
            new("Player chat text", 30, 120, 250, 20), new("World label", 600, 400, 200, 20),
            new("Unklarer Listenname", 40, 400, 250, 20), new("Unentdeckt", 250, 500, 160, 20),
            new("Further world label", 40, 600, 250, 20) }).ToArray();
        var page = ExplorationList.Read(lines, [Place("a")], bitmap, Layout, "altgard");
        Assert.Equal(["Unklarer Listenname"], page.Unmatched);
        Assert.True(page.End); Assert.Empty(page.Ids);
        Assert.Empty(ExplorationList.Read(lines.Where(l => l.Y != 220).ToArray(), [Place("a")], bitmap, Layout, "altgard").Unmatched);
    }

    [Theory]
    [InlineData("Waffenkammer der Zerstör...")]
    [InlineData("Versteck der Odiumplünde...")]
    [InlineData("Verlassenes Gefängnis der...")]
    [InlineData("Unterschlupf des Verschlin...")]
    [InlineData("Blutgetränkte Beichtkamm...")]
    [InlineData("Ruinenstätte des Hoheprie...")]
    [InlineData("Gefängnis der Vergessenh...")]
    [InlineData("Ruinen der Uralten Stadt R...")]
    [InlineData("Kumbahums Höhle")]
    [InlineData("Homishs Gefangenenlager")]
    [InlineData("Provisorium der Klingenleg...")]
    [InlineData("Lagerplatz von Detlefs Ban...")]
    [InlineData("Lagerplatz der Waldplünde...")]
    [InlineData("Außenposten der Flauke Le...")]
    [InlineData("Außenposten des Barahta-...")]
    [InlineData("Besatzungsgebiet der Rub...")]
    [InlineData("Ruinen der Verdorbenen W...")]
    public void ReportedGermanTruncationsAndEntranceFormsResolveUniquely(string name)
    {
        var places = new ExplorationService(new ProgressService()).Places.Where(p => p.Map == "altgard" && p.Kind is "dungeon" or "stronghold").ToArray();
        Assert.NotNull(ExplorationService.Match(name, places));
    }

    [Fact]
    public void AmbiguousOrShortPrefixesStayUnmatched()
    {
        var a = Place("a") with { De = "Eingang zur Waffenkammer der Zerstörungsarchonen" };
        var b = a with { Id = "b", De = "Eingang zur Waffenkammer der Zerstörungslegion" };
        Assert.Null(ExplorationService.Match("Waffenkammer der Zerstör…", [a, b]));
        Assert.Null(ExplorationService.Match("Waffen...", [a]));
        Assert.Null(ExplorationService.Match("Valkas Nes...", [Place("short") with { En = "Valka's Nest Entrance", De = null }]));
    }
}
