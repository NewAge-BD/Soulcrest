using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class FarmLocationCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-farm-labels-" + Guid.NewGuid().ToString("N"));

    public FarmLocationCatalogTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void NearestNamedRegionUsesTheSameMapPixelsAndIsAlwaysApproximate()
    {
        WriteMap("sample-map",
        [
            [0, 100, 200, "First field", "Erstes Gebiet"],
            [1, 600, 700, "Second village", "Zweites Dorf"],
            [2, 610, 710, "Resource which must not win", "Ressource"],
            [3, 610, 710, "NPC which must not win", "NPC"],
        ]);
        var catalog = FarmLocationCatalog.Load(_directory);
        Assert.Equal(new("Second village", true), catalog.Resolve(Position("sample-map", 610, 710), "en"));
        Assert.Equal(new("Zweites Dorf", true), catalog.Resolve(Position("sample-map", 610, 710), "de"));
        Assert.Equal(new("First field", true), catalog.Resolve(Position("sample-map", 100, 200), "en"));
        Assert.Null(catalog.Resolve(Position("other-map", 600, 700), "en"));
    }

    [Fact]
    public void MissingGermanUsesTheEvidencedEnglishNameWithoutInventingATranslation()
    {
        WriteMap("sample-map", [[0, 100, 200, "Synthetic field", null]]);
        Assert.Equal(new("Synthetic field", true), FarmLocationCatalog.Load(_directory).Resolve(Position("sample-map", 100, 200), "de"));
    }

    [Fact]
    public void EquidistantDifferentLabelsAreUnknownWhileDuplicateLabelCentresRemainUsable()
    {
        WriteMap("sample-map", [[0, 100, 200, "West", "Westen"], [0, 300, 200, "East", "Osten"]]);
        Assert.Null(FarmLocationCatalog.Load(_directory).Resolve(Position("sample-map", 200, 200), "en"));
        WriteMap("sample-map", [[0, 100, 200, "Field", "Gebiet"], [0, 300, 200, "Field", "Gebiet"]]);
        Assert.Equal(new("Field", true), FarmLocationCatalog.Load(_directory).Resolve(Position("sample-map", 200, 200), "en"));
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    [InlineData(4097, 100)]
    [InlineData(100, 4097)]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    public void InvalidOrOutOfMapPositionsCannotAcquireAPlace(double x, double y)
    {
        WriteMap("sample-map", [[0, 100, 200, "Field", "Gebiet"]]);
        Assert.Null(FarmLocationCatalog.Load(_directory).Resolve(Position("sample-map", x, y), "en"));
    }

    [Fact]
    public void UnsupportedCategoryEmptyLabelsAndOutOfMapMarkersAreExcluded()
    {
        WriteMap("sample-map",
        [
            [0, -1, 200, "Invalid west", null], [0, 4097, 200, "Invalid east", null],
            [0, 100, 200, "   ", null], [4, 100, 200, "Unsupported region category", null],
            [2, 100, 200, "Resource", null], [3, 100, 200, "Npc", null],
        ]);
        Assert.Null(FarmLocationCatalog.Load(_directory).Resolve(Position("sample-map", 100, 200), "en"));
    }

    [Fact]
    public void LoadedCatalogueRemainsAnImmutableSnapshotWhenCacheFilesChange()
    {
        WriteMap("sample-map", [[0, 100, 200, "Initial field", null]]);
        var catalog = FarmLocationCatalog.Load(_directory);
        WriteMap("sample-map", [[0, 100, 200, "Changed field", null]]);
        File.Delete(Path.Combine(_directory, "sample-map", "data.js"));
        Assert.Equal(new("Initial field", true), catalog.Resolve(Position("sample-map", 100, 200), "en"));
    }

    [Fact]
    public void MismatchedPayloadOrAdditionalScriptIsRejectedInsteadOfExecuted()
    {
        WriteMap("sample-map", [[0, 100, 200, "Field", null]], payloadId: "other-map");
        Assert.Null(FarmLocationCatalog.Load(_directory).Resolve(Position("sample-map", 100, 200), "en"));
        WriteMap("sample-map", [[0, 100, 200, "Field", null]]);
        File.AppendAllText(Path.Combine(_directory, "sample-map", "data.js"), "\nwindow.unrelatedCode = true;");
        Assert.Null(FarmLocationCatalog.Load(_directory).Resolve(Position("sample-map", 100, 200), "en"));
    }

    [Fact]
    public void DuplicateMapDefinitionsAndTraversalIdsDoNotResolve()
    {
        WriteMap("sample-map", [[0, 100, 200, "Field", null]]);
        File.WriteAllText(Path.Combine(_directory, "manifest.json"), """{"maps":[{"id":"sample-map","size":4096},{"id":"sample-map","size":4096},{"id":"../outside","size":4096}]}""");
        Assert.Null(FarmLocationCatalog.Load(_directory).Resolve(Position("sample-map", 100, 200), "en"));
        Assert.Null(FarmLocationCatalog.Load(_directory).Resolve(Position("../outside", 100, 200), "en"));
    }

    [Fact]
    public void MissingMalformedAndOversizedSourcesStayUnknown()
    {
        Assert.Null(FarmLocationCatalog.Load(null).Resolve(Position("sample-map", 100, 200), "en"));
        Assert.Null(FarmLocationCatalog.Load("relative/path").Resolve(Position("sample-map", 100, 200), "en"));
        Assert.Null(FarmLocationCatalog.Load(@"\\unreachable\share").Resolve(Position("sample-map", 100, 200), "en"));
        File.WriteAllText(Path.Combine(_directory, "manifest.json"), "{broken");
        Assert.Null(FarmLocationCatalog.Load(_directory).Resolve(Position("sample-map", 100, 200), "en"));
        WriteMap("sample-map", [[0, 100, 200, "Field", null]]);
        using (var stream = File.Create(Path.Combine(_directory, "sample-map", "data.js"))) stream.SetLength(16 * 1024 * 1024 + 1);
        Assert.Null(FarmLocationCatalog.Load(_directory).Resolve(Position("sample-map", 100, 200), "en"));
    }

    private void WriteMap(string id, object?[][] markers, string? payloadId = null)
    {
        File.WriteAllText(Path.Combine(_directory, "manifest.json"), JsonSerializer.Serialize(new { maps = new[] { new { id, size = 4096 } } }));
        var folder = Path.Combine(_directory, id);
        Directory.CreateDirectory(folder);
        var payload = JsonSerializer.Serialize(new
        {
            id = payloadId ?? id, size = 4096,
            categories = new[]
            {
                new { group = "Regions", name = "Hunting Ground" }, new { group = "Regions", name = "Village" },
                new { group = "Resources", name = "Hunting Ground" }, new { group = "NPCs", name = "Village" },
                new { group = "Regions", name = "Unqualified category" },
            },
            markers,
        });
        File.WriteAllText(Path.Combine(folder, "data.js"), "window.SoulcrestMaps = window.SoulcrestMaps || {};\nwindow.SoulcrestMaps[" + JsonSerializer.Serialize(id) + "] = " + payload + ";\n");
    }

    private static RecorderPositionSnapshot Position(string map, double x, double y) =>
        new(new PlayerPosition(map, x, y, DateTimeOffset.UnixEpoch), 1, 1, 1, DateTimeOffset.UnixEpoch, 30, 1);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
