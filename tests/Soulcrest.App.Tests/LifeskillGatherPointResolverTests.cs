using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LifeskillGatherPointResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static ResourceNodeDefinition Node(string id, double x, double y = 100, int resource = 123,
        string map = "test-map", string? german = "Testressource") =>
        new(id, map, resource, "resource-ore", "Test resource", german, x, y, x, y);

    private static LifeskillGatherPointResolver Resolver(IEnumerable<ResourceNodeDefinition> nodes,
        double? metersPerPixel = 1, int? size = 8192) => new(new(nodes), _ => metersPerPixel, _ => size);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuccessesAndFailuresFreezeOriginalAndNearestCompatibleCataloguePosition(bool succeeded)
    {
        var resolver = Resolver([Node("farther", 113), Node("nearest", 110),
            Node("wrong-resource", 100, resource: 456), Node("wrong-map", 100, map: "another-map")]);
        var visit = resolver.Resolve("visit", 3, new("test-map", 100, 100, Now), 123, Now, succeeded,
            "Test map", "Fallback resource");
        Assert.Equal("nearest", visit.CatalogNodeId);
        Assert.Equal(100, visit.OriginalX);
        Assert.Equal(100, visit.OriginalY);
        Assert.Equal(110, visit.X);
        Assert.Equal(100, visit.Y);
        Assert.Equal(123, visit.GatherableId);
        Assert.Equal(8192, visit.MapSize);
        Assert.Equal(succeeded, visit.Succeeded);
        Assert.Equal(3, visit.Section);
        Assert.Equal(Now, visit.At);
    }

    [Theory]
    [InlineData(15, 1, true)]
    [InlineData(15.0001, 1, false)]
    [InlineData(7.5, 2, true)]
    [InlineData(7.5001, 2, false)]
    public void DistanceUsesCalibratedGameMetersAndIncludesExactlyFifteenMeters(double offset, double scale, bool snapped)
    {
        var visit = Resolver([Node("node", 100 + offset)], scale).Resolve("visit", 0,
            new("test-map", 100, 100, Now), 123, Now, true, "Map", "Fallback");
        Assert.Equal(snapped ? "node" : null, visit.CatalogNodeId);
        Assert.Equal(snapped ? 100 + offset : 100, visit.X);
    }

    [Fact]
    public void ExactDistanceTieKeepsOriginalPositionButCloserCandidateResolvesEarlierTie()
    {
        var nodes = new[] { Node("left", 90), Node("right", 110) };
        var tied = Resolver(nodes).Resolve("tied", 0, new("test-map", 100, 100, Now), 123, Now, true, "Map", "Fallback");
        Assert.Null(tied.CatalogNodeId);
        Assert.Equal(100, tied.X);
        Assert.Equal("Fallback", tied.Name);
        var closer = Resolver(nodes.Append(Node("closer", 105))).Resolve("closer", 0,
            new("test-map", 100, 100, Now), 123, Now, true, "Map", "Fallback");
        Assert.Equal("closer", closer.CatalogNodeId);
    }

    [Theory]
    [InlineData(null, 8192)]
    [InlineData(0d, 8192)]
    [InlineData(-1d, 8192)]
    [InlineData(double.NaN, 8192)]
    [InlineData(double.PositiveInfinity, 8192)]
    [InlineData(1d, null)]
    [InlineData(1d, 0)]
    public void MissingOrInvalidCalibrationKeepsOriginalPosition(double? scale, int? size)
    {
        var visit = Resolver([Node("node", 100)], scale, size).Resolve("visit", 0,
            new("test-map", 100, 100, Now), 123, Now, true, "Map", "Fallback");
        Assert.Null(visit.CatalogNodeId);
        Assert.Equal(100, visit.X);
        Assert.Equal(size is > 0 ? size.Value : 8192, visit.MapSize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(456)]
    public void ResourceTypeMustBePresentPositiveAndExactlyMatch(int? resource)
    {
        var visit = Resolver([Node("node", 100)]).Resolve("visit", 0,
            new("test-map", 100, 100, Now), resource, Now, false, "Map", "Fallback");
        Assert.Null(visit.CatalogNodeId);
        Assert.Equal(resource is > 0 ? resource : null, visit.GatherableId);
    }

    [Fact]
    public void EmptyCatalogueAndOtherMapNeverSnap()
    {
        foreach (var catalog in new[] { ResourceNodeCatalog.Empty, new ResourceNodeCatalog([Node("node", 100, map: "other")]) })
        {
            var visit = new LifeskillGatherPointResolver(catalog, _ => 1, _ => 4096).Resolve("visit", 0,
                new("test-map", 100, 100, Now), 123, Now, true, "Map", "Fallback");
            Assert.Null(visit.CatalogNodeId);
            Assert.Equal(4096, visit.MapSize);
        }
    }

    [Fact]
    public void ANodeOutsideMapBoundsCannotReplaceAValidGatherPosition()
    {
        var visit = Resolver([Node("outside", 105)], size: 100).Resolve("visit", 0,
            new("test-map", 100, 100, Now), 123, Now, true, "Map", "Fallback");
        Assert.Null(visit.CatalogNodeId);
        Assert.Equal(100, visit.X);
        Assert.Equal(100, visit.Y);
    }

    [Theory]
    [InlineData("de", "Testressource")]
    [InlineData("en", "Test resource")]
    public void ChosenNodeUsesCurrentUiLanguageWithEnglishFallback(string language, string expected)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var visit = Resolver([Node("node", 100)]).Resolve("visit", 0,
                new("test-map", 100, 100, Now), 123, Now, true, "Map", "Fallback");
            Assert.Equal(expected, visit.Name);
            var withoutGerman = Resolver([Node("node", 100, german: null)]).Resolve("visit2", 0,
                new("test-map", 100, 100, Now), 123, Now, true, "Map", "Fallback");
            Assert.Equal("Test resource", withoutGerman.Name);
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    [InlineData(-1, 100)]
    public void InvalidPositionIsRejectedBeforeRouteConstruction(double x, double y) =>
        Assert.Throws<ArgumentException>(() => Resolver([]).Resolve("visit", 0,
            new("test-map", x, y, Now), null, Now, false, "Map", "Fallback"));
}
