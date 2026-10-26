using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ResourceNodeCatalogTests
{
    private static ResourceNodeDefinition Node(string id = "resource:example", string map = "synthetic") =>
        new(id, map, 1550032, "resource-herb", "Example herb", "Beispielkraut", 100, 200, -20, 30);

    [Fact]
    public void ExactNodeIdentityAndMapIndexesDoNotGuessFromNamesOrPositions()
    {
        var first = Node();
        var second = Node("resource:other", "other");
        var catalog = new ResourceNodeCatalog([first, second]);
        Assert.True(catalog.HasData);
        Assert.Same(first, catalog.Find(first.Id));
        Assert.Equal([first], catalog.ForMap("synthetic"));
        Assert.Equal([second], catalog.ForMap("other"));
        Assert.Null(catalog.Find("Example herb"));
        Assert.Empty(catalog.ForMap("missing"));
        Assert.False(ResourceNodeCatalog.Empty.HasData);
    }

    [Fact]
    public void SourceNativeCoordinatesMayBeNegativeWhileLogicalMapCoordinatesCannot()
    {
        Assert.Equal(-20, Assert.Single(new ResourceNodeCatalog([Node()]).Nodes).NativeX);
        Assert.Throws<InvalidDataException>(() => new ResourceNodeCatalog([Node() with { X = -1 }]));
        Assert.Throws<InvalidDataException>(() => new ResourceNodeCatalog([Node() with { Y = double.NaN }]));
        Assert.Throws<InvalidDataException>(() => new ResourceNodeCatalog([Node() with { NativeX = double.PositiveInfinity }]));
        Assert.Throws<InvalidDataException>(() => new ResourceNodeCatalog([Node(), Node()]));
    }

    [Fact]
    public void SidecarSchemaLoadsExactFieldsAndMissingOrDamagedPackageStaysUnavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-resource-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(ResourceNodeCatalog.Load(directory).HasData);
            var file = Path.Combine(directory, "resource-nodes.json");
            File.WriteAllText(file, """
                {"schemaVersion":1,"nodes":[{"id":"resource:example","mapId":"synthetic","gatherableId":1550032,
                "typeId":"resource-herb","nameEn":"Example herb","nameDe":"Beispielkraut",
                "x":100,"y":200,"nativeX":-20,"nativeY":30}]}
                """);
            Assert.Equal(Node(), Assert.Single(ResourceNodeCatalog.Load(directory).Nodes));
            File.WriteAllText(file, "{\"schemaVersion\":2,\"nodes\":[]}");
            Assert.False(ResourceNodeCatalog.Load(directory).HasData);
            File.WriteAllText(file, "{\"schemaVersion\":1,\"nodes\":[null]}");
            Assert.False(ResourceNodeCatalog.Load(directory).HasData);
            File.WriteAllText(file, "incomplete");
            Assert.False(ResourceNodeCatalog.Load(directory).HasData);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
