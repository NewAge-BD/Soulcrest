using Soulcrest.App.Services;
using Soulcrest.Core.Instances;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// User decision 2026-10-28: public builds classify the own world from the numeric map-kind table so that
/// loot sessions, Abyss context and network map tracking work; area names come from the map package.
/// </summary>
public sealed class PublicWorldContextTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);
    private static WorldContextService Service() => new(InstanceCatalog.FromJson(File.ReadAllText(
        Path.Combine(TestPrerequisites.RepositoryRoot, "data", "instances", "map-kinds.json"))));

    [Fact]
    public void OwnOpenWorldEntryQualifiesWithoutAreaNames()
    {
        var world = Service();
        Assert.True(world.HasLocationFacts);
        world.ObserveOwn(1110, 7, At);
        var snapshot = world.Snapshot;
        Assert.Equal(WorldContextStatus.Confirmed, snapshot.Status);
        Assert.Equal(WorldContextKind.OpenWorld, snapshot.Kind);
        Assert.Equal(7, snapshot.BindingGeneration);
        Assert.Equal("altgard", snapshot.SoulcrestMapId);
        Assert.Null(snapshot.NameDe); Assert.Null(snapshot.NameEn);
        var display = WorldLocationPresentation.Build(snapshot, true, id => id == "altgard" ? "Altgard" : null);
        Assert.Equal(("Altgard", "confirmed"), (display.Name, display.State));
        Assert.Equal(UiText.T("Gebiet noch nicht zugeordnet"), WorldLocationPresentation.Build(snapshot, true).Name);
    }

    [Fact]
    public void AbyssAndOtherInstancesStaySeparateFromOpenWorld()
    {
        var world = Service();
        world.ObserveOwn(20, 1, At);
        Assert.Equal((WorldContextKind.Instance, "Abyss"), (world.Snapshot.Kind, world.Snapshot.Category));
        world.ObserveOwn(60, 2, At.AddMinutes(1));
        Assert.Equal((WorldContextKind.Instance, "Matching"), (world.Snapshot.Kind, world.Snapshot.Category));
        world.ObserveOwn(999_999_999, 3, At.AddMinutes(2));
        Assert.Equal(WorldContextKind.Unknown, world.Snapshot.Kind);
    }
}
