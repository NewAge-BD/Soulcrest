using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationScanMapSelectorTests
{
    private static readonly ExplorationLayout Layout = ExplorationLayout.ForWindow(new Size(2559, 1439));
    private const string Reshanta = "chaotic-middle-reshanta";
    private static ExplorationScanMapSelector Selector() => new(new Dictionary<string, IReadOnlyList<string>>
    {
        ["altgard"] = ["Altgard"],
        ["ishalgen"] = ["Ishalgen"],
        [Reshanta] = ["Chaotische Mittlere Ebene von Reshanta", "Chaotic Middle Reshanta"],
    });
    private static OcrLine Title(string text = "Karte: Altgard", double y = 30) => new(text, 40, y, 400, 30);

    [Theory]
    [InlineData("Karte: Altgard", "altgard")]
    [InlineData("Map: Altgard", "altgard")]
    [InlineData("Karte: Ishalgen", "ishalgen")]
    [InlineData("Map: Ishalgen", "ishalgen")]
    [InlineData("Karte: Chaotische Mittlere Ebene von Reshanta", Reshanta)]
    [InlineData("Map: Chaotic Middle Reshanta", Reshanta)]
    public void ExactGermanAndEnglishTitlesIdentifyKnownScannableMaps(string text, string expected)
    {
        var selector = Selector();
        Assert.Equal(expected, selector.Read(Layout, [Title(text)], []));
        Assert.Equal(expected, selector.Read(Layout, [], [Title(text)]));
    }

    [Fact]
    public void DifferentLanguageAliasesAgreeOnTheSameMapAcrossPasses() =>
        Assert.Equal(Reshanta, Selector().Read(Layout,
            [Title("Map: Chaotic Middle Reshanta")], [Title("Karte: Chaotische Mittlere Ebene von Reshanta")]));

    [Fact]
    public void AValidatedPassCanRecoverAMissingOrUnknownOtherReading()
    {
        var selector = Selector();
        Assert.Equal("altgard", selector.Read(Layout, [Title()], [Title("Map: Unknown synthetic test map")]));
        Assert.Equal("altgard", selector.Read(Layout, [new("Favoriten", 89, 170, 121, 21)], [Title()]));
    }

    [Fact]
    public void DifferentKnownMapsAcrossPassesAreRejected() =>
        Assert.Null(Selector().Read(Layout, [Title()], [Title("Karte: Ishalgen")]));

    [Theory]
    [InlineData(-1, 30, 400, 30)]
    [InlineData(200, 30, 400, 30)]
    [InlineData(40, -1, 400, 30)]
    [InlineData(40, 120, 400, 30)]
    [InlineData(40, 100, 400, 30)]
    public void AMapNameOutsideTheExistingTitleBandCannotSelectTheMap(double x, double y, double width, double height)
    {
        var title = new OcrLine("Karte: Altgard", x, y, width, height);
        Assert.Null(Selector().Read(Layout, [title], []));
        Assert.Null(Selector().Read(Layout, [], [title]));
    }

    [Theory]
    [InlineData("Altgard")]
    [InlineData("Karte: Alt")]
    [InlineData("Map: Altgar...")]
    [InlineData("Karte: Altgard Umgebung")]
    [InlineData("Quest: Altgard")]
    [InlineData("Other Map: Altgard")]
    [InlineData("Map: Unknown synthetic test map")]
    [InlineData("")]
    public void VaguePrefixesAndOtherTextDoNotIdentifyAMap(string text) =>
        Assert.Null(Selector().Read(Layout, [Title(text)], []));

    [Fact]
    public void NoTitleInEitherPassDoesNotIdentifyAMap() =>
        Assert.Null(Selector().Read(Layout, [], []));

    [Theory]
    [InlineData("Karte: Altgard")]
    [InlineData("Karte: Ishalgen")]
    public void MultipleMatchingHeaderLinesInEitherPassAreAmbiguousEvenWithAValidOtherPass(string secondTitle)
    {
        var selector = Selector();
        var ambiguous = new[] { Title(), Title(secondTitle, 70) };
        Assert.Null(selector.Read(Layout, ambiguous, [Title()]));
        Assert.Null(selector.Read(Layout, [Title()], ambiguous));
    }

    [Fact]
    public void OneNormalizedTitleSharedByDifferentMapsIsAmbiguous()
    {
        var selector = new ExplorationScanMapSelector(new Dictionary<string, IReadOnlyList<string>>
        {
            ["synthetic-first"] = ["Altgard"],
            ["synthetic-second"] = ["Alt-Gard"],
        });
        Assert.Null(selector.Read(Layout, [Title()], []));
        Assert.Null(selector.Read(Layout, [], [Title()]));
    }

    [Fact]
    public void DuplicateAliasesForOneMapAreNotDifferentMapIdentities()
    {
        var selector = new ExplorationScanMapSelector(new Dictionary<string, IReadOnlyList<string>>
            { ["altgard"] = ["Altgard", "Altgard", "Alt-Gard"] });
        Assert.Equal("altgard", selector.Read(Layout, [Title()], [Title("Map: Altgard")]));
    }

    [Fact]
    public void TwoConsecutiveStableObservationsAreRequiredAndReadAloneCannotAdvanceThem()
    {
        var selector = Selector();
        for (var index = 0; index < 3; index++) Assert.Equal("altgard", selector.Read(Layout, [Title()], []));
        Assert.Null(selector.Observe("altgard"));
        Assert.Equal("altgard", selector.Observe("altgard"));
        Assert.Equal("altgard", selector.Observe("altgard"));
        Assert.Null(selector.Observe("ishalgen"));
        Assert.Equal("ishalgen", selector.Observe("ishalgen"));
    }

    [Fact]
    public void UnknownOrConflictingFramesResetTheSequenceAndCanRecover()
    {
        var selector = Selector();
        Assert.Null(selector.Observe(selector.Read(Layout, [Title()], [])));
        Assert.Null(selector.Observe(selector.Read(Layout, [Title()], [Title("Karte: Ishalgen")])));
        Assert.Null(selector.Observe(selector.Read(Layout, [Title()], [])));
        Assert.Null(selector.Observe(selector.Read(Layout, [], [])));
        Assert.Null(selector.Observe(selector.Read(Layout, [], [Title()])));
        Assert.Equal("altgard", selector.Observe(selector.Read(Layout, [Title()], [])));
    }

    [Fact]
    public void ExplicitResetAfterMovementRequiresTwoNewObservations()
    {
        var selector = Selector();
        Assert.Null(selector.Observe("altgard"));
        Assert.Equal("altgard", selector.Observe("altgard"));
        selector.Reset();
        Assert.Null(selector.Observe("altgard"));
        Assert.Equal("altgard", selector.Observe("altgard"));
        Assert.Null(selector.Observe("unknown"));
        Assert.Null(selector.Observe("altgard"));
        Assert.Equal("altgard", selector.Observe("altgard"));
    }

    [Fact]
    public void SelectorsDoNotShareObservationState()
    {
        var first = Selector();
        var second = Selector();
        Assert.Null(first.Observe("altgard"));
        Assert.Null(second.Observe("altgard"));
        Assert.Equal("altgard", first.Observe("altgard"));
    }

    [Fact]
    public void ProductiveConstructionUsesOnlyMapsWithPlacesAndNeverWritesAProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-scan-map-" + Guid.NewGuid().ToString("N"), "exploration.json");
        var exploration = new ExplorationService(path,
            [new("test-place", "altgard", "dungeon", 0, "Synthetic test place", null)]);
        var selector = new ExplorationScanMapSelector(exploration);
        Assert.Equal("altgard", selector.Read(Layout, [Title()], []));
        Assert.Null(selector.Read(Layout, [Title("Karte: Ishalgen")], []));
        Assert.Null(selector.Observe("ishalgen"));
        Assert.Null(selector.Observe("altgard"));
        Assert.Equal("altgard", selector.Observe("altgard"));
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
}
