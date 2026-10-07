using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Soulcrest.Ocr;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// Scanning the Kibelisk tab of the world map (user request 2026-10-06). Fixtures: the user's Altgard
/// list, English client, cut to the scan mask of a 2560×1440 window; "incomplete" is the second
/// character, whose list ends at 24 of 61.
/// </summary>
public sealed class KibeliskListTests
{
    private static readonly ExplorationPlace[] Altgard =
    [
        Place("Safe Haven"), Place("Safe Haven Cliff"), Place("Temporary Investigation Base"), Place("Mire Campsite"),
        Place("Eastern Gribade Highland Tail Island"), Place("Eastern Gribade Highland Cliff"),
        Place("Shulak Street Stall"), Place("Steel Hammer Temporary Trading Post"), Place("Idun's Lake"),
        Place("Guide's Dwelling"),
    ];

    private static ExplorationPlace Place(string name, string suffix = "") => new($"altgard|kibelisk|{name}{suffix}", "altgard", "kibelisk", 0, name, null);

    private static OcrLine Line(double y, string text) => new(text, 48, y, 300, 19);

    private static KibeliskPage Page(params (int Number, string Name, string Status)[] rows) =>
        KibeliskList.Read(rows.SelectMany((r, i) => new[] { Line(i * 86, $"{r.Number}. {r.Name}"), Line(i * 86 + 30, r.Status) }).ToArray(), 1239);

    [Fact]
    public void RowsCarryNumberNameCutAndStatus()
    {
        var page = Page((4, "Temporary Investigation...", "Bind Complete"), (9, "Mire Campsite", "Binding Incomplete"));
        Assert.Equal([new KibeliskRow(4, "Temporary Investigation", true, true), new KibeliskRow(9, "Mire Campsite", false, false)], page.Rows);
        Assert.True(page.End); // lots of empty space below row 9
        Assert.Equal(["altgard|kibelisk|Temporary Investigation Base"], KibeliskList.Candidates(page.Rows[0], Altgard).Select(p => p.Id));
    }

    [Fact]
    public void ACutNameSharedByTwoIsSettledWhenBothRowsAgree()
    {
        var session = new KibeliskSession(Altgard);
        var first = Page((52, "Eastern Gribade Highla...", "Bind Complete"));
        session.Observe(first);
        session.Observe(first);
        Assert.Contains("52. Eastern Gribade Highla", session.Result().Open); // only one of the two rows seen
        var both = Page((52, "Eastern Gribade Highla...", "Bind Complete"), (53, "Eastern Gribade Highla...", "Bind Complete"));
        session.Observe(both);
        session.Observe(both);
        Assert.Equal(["altgard|kibelisk|Eastern Gribade Highland Cliff", "altgard|kibelisk|Eastern Gribade Highland Tail Island"],
            session.Result().Bound.Order());
    }

    [Fact]
    public void OneLostLetterStillFindsTheName()
    {
        // Windows OCR read "54. 'dun's Lake" on the full Altgard list (2026-10-06).
        var row = Page((54, "'dun's Lake", "Bind Complete")).Rows[0];
        Assert.Equal(["altgard|kibelisk|Idun's Lake"], KibeliskList.Candidates(row, Altgard).Select(p => p.Id));
        Assert.Empty(KibeliskList.Candidates(Page((9, "Safe Have Clif", "Bind Complete")).Rows[0], Altgard)); // two letters off
    }

    [Fact]
    public void ShulakStreetStallAndTheTradingPostAreTwoPlaces()
    {
        // The game's "42. Shulak Street Stall" is one of the two "Steel Hammer Temporary Trading Post" of the
        // map data; the import names it after the village label next to it (user screenshots 2026-10-06).
        var session = new KibeliskSession(Altgard);
        var page = Page((42, "Shulak Street Stall", "Bind Complete"), (51, "Steel Hammer Temporar...", "Binding Incomplete"));
        session.Observe(page);
        session.Observe(page);
        var (bound, unbound, unknown, open) = session.Result();
        Assert.Equal(["altgard|kibelisk|Shulak Street Stall"], bound);
        Assert.Contains("altgard|kibelisk|Steel Hammer Temporary Trading Post", unbound);
        Assert.Empty(unknown);
        Assert.Empty(open);
    }

    [Fact]
    public void TheListEndUnchecksEverythingNotListed()
    {
        var session = new KibeliskSession(Altgard);
        var page = Page((1, "Safe Haven", "Bind Complete"), (2, "Safe Haven Cliff", "Binding Incomplete"));
        session.Observe(page);
        Assert.False(session.Ended); // needs the same picture twice
        session.Observe(page);
        Assert.True(session.Ended);
        var (bound, unbound, unknown, open) = session.Result();
        Assert.Equal(["altgard|kibelisk|Safe Haven"], bound);
        Assert.Contains("altgard|kibelisk|Safe Haven Cliff", unbound);
        Assert.Contains("altgard|kibelisk|Guide's Dwelling", unbound); // not discovered by this character
        Assert.True(session.Complete);
    }

    [Fact]
    public void AGapInTheNumbersKeepsTheListOpen()
    {
        var session = new KibeliskSession(Altgard);
        var page = Page((1, "Safe Haven", "Bind Complete"), (3, "Mire Campsite", "Bind Complete"));
        session.Observe(page);
        session.Observe(page);
        Assert.False(session.Ended); // number 2 was never read: nothing is unchecked for missing
        Assert.DoesNotContain("altgard|kibelisk|Guide's Dwelling", session.Result().Unbound);
    }

    /// <summary>
    /// The whole Altgard list of the first character, 61 of 61 bound, as five screenshots of the list
    /// (user 2026-10-06): every row is read and every Kibelisk of the map data is settled.
    /// </summary>
    [Fact]
    public async Task TheFullAltgardListSettlesAllSixtyOne()
    {
        if (!WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
        {
            Assert.False(Environment.GetEnvironmentVariable("SOULCREST_REQUIRE_WINDOWS_OCR") == "1", "Windows OCR en-US fehlt.");
            return;
        }
        var places = new ExplorationService(new ProgressService()).Places.Where(p => p.Map == "altgard" && p.Kind == "kibelisk").ToArray();
        if (places.Length == 0)
            return; // no map data next to the tests
        var reader = WindowsOcrLineReader.Create("auto", 1);
        var session = new KibeliskSession(places);
        for (var i = 1; i <= 5; i++)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", "kibelisk-list", $"en-2026-10-06-full-{i}.png");
            using var image = new Bitmap(path);
            var page = KibeliskList.Read(await reader.ReadAsync(image), image.Height);
            session.Observe(page);
            session.Observe(page);
        }
        var (bound, unbound, unknown, open) = session.Result();
        Assert.Empty(open);
        Assert.Empty(unknown);
        Assert.Empty(unbound);
        Assert.Equal(61, bound.Count);
        Assert.Contains("altgard|kibelisk|Shulak Street Stall", bound);
        Assert.True(session.Complete);
    }

    [Theory]
    [InlineData("en-2026-10-06-top", 11, 11, 0)]          // row 12 cut by the mask edge: left out
    [InlineData("en-2026-10-06-end", 14, 14, 0)]
    [InlineData("en-2026-10-06-incomplete-top", 13, 11, 2)] // 2 and 9 not bound
    [InlineData("en-2026-10-06-incomplete-mid", 14, 7, 7)]
    public async Task FixturesReadWithWindowsOcr(string name, int rows, int bound, int unbound)
    {
        if (!WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
        {
            Assert.False(Environment.GetEnvironmentVariable("SOULCREST_REQUIRE_WINDOWS_OCR") == "1", "Windows OCR en-US fehlt.");
            return;
        }
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", "kibelisk-list", name + ".png");
        using var image = new Bitmap(path);
        var lines = await WindowsOcrLineReader.Create("auto", 1).ReadAsync(image);
        var page = KibeliskList.Read(lines, image.Height);
        Assert.Equal(rows, page.Rows.Count);
        Assert.Equal(bound, page.Rows.Count(r => r.Bound));
        Assert.Equal(unbound, page.Rows.Count(r => !r.Bound));
        Assert.Equal(Enumerable.Range(page.Rows[0].Number, rows), page.Rows.Select(r => r.Number)); // no row skipped

        // Against the real Altgard data: every row has a candidate.
        var places = new ExplorationService(new ProgressService()).Places.Where(p => p.Map == "altgard" && p.Kind == "kibelisk").ToArray();
        if (places.Length > 0)
            Assert.All(page.Rows, r => Assert.NotEmpty(KibeliskList.Candidates(r, places)));
    }
}
