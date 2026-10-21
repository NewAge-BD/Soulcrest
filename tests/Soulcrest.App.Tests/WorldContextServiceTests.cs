using Soulcrest.App.Services;
using Soulcrest.Core.Instances;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

internal static class WorldContextTestCatalog
{
    internal static InstanceCatalog Create() => InstanceCatalog.FromJson("""
        {"schemaVersion":1,"maps":[
         {"id":100,"names":{"de":"Testwelt","en":"Test world"},"kind":"openWorld","category":"field","categoryNames":{"de":"Testgebiet","en":"Test field"},"soulcrestMapId":"synthetic-world","sourceRefs":["synthetic-map"]},
         {"id":200,"names":{"de":"Testinstanz","en":"Test instance"},"kind":"instance","category":"shared","sourceRefs":["synthetic-map"]},
         {"id":300,"names":{"de":null,"en":null},"kind":"unknown","category":null,"sourceRefs":["synthetic-map"]}],
         "dungeons":[
          {"id":1000,"mapIds":[200],"names":{"de":"Testkammer","en":"Test chamber"},"category":"repeatable","categoryNames":{"de":"Wiederholbar","en":"Repeatable"},"mode":"normal","modeNames":{"de":"Normal","en":"Normal"},"sourceRefs":["synthetic-dungeon"]},
          {"id":1001,"mapIds":[200],"names":{"de":null,"en":null},"category":"repeatable","mode":"hard","sourceRefs":["synthetic-dungeon"]},
          {"id":1002,"mapIds":[100],"names":{"de":null,"en":"Layer chamber"},"category":"layer","mode":null,"instanceLayer":true,"active":true,"sourceRefs":["synthetic-dungeon"]},
          {"id":1003,"mapIds":[100],"names":{"de":null,"en":null},"category":"unconfirmed-layer","mode":null,"instanceLayer":null,"sourceRefs":["synthetic-dungeon"]},
          {"id":1004,"mapIds":[200],"names":{"de":null,"en":"Inactive fixture"},"category":"test","mode":"disabled","active":false,"sourceRefs":["synthetic-dungeon"]}]}
        """);
}

public sealed class WorldContextServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 1, 0, 0, TimeSpan.Zero);
    private static WorldContextService Create() => new(WorldContextTestCatalog.Create());

    [Fact]
    public void ConfirmedOwnIsIndependentOfMapTrackingAndNeverInfersDungeonModeFromCandidates()
    {
        var service = Create();
        Assert.Equal(WorldContextStatus.Unknown, service.Snapshot.Status);
        service.ObserveOwn(200, 1, At);
        Assert.Equal(WorldContextStatus.Confirmed, service.Snapshot.Status);
        Assert.Equal(WorldContextKind.Instance, service.Snapshot.Kind);
        Assert.Equal("Test instance", service.Snapshot.NameEn);
        Assert.Null(service.Snapshot.DungeonId);
        Assert.Null(service.Snapshot.Mode);
        var snapshot = service.Snapshot;
        Assert.Same(snapshot, service.Snapshot); // No periodic Own or timer/TTL is required.
    }

    [Fact]
    public void UnknownDefinitionStillRetainsTheConfirmedRawMapButNeverInventsATypeOrName()
    {
        var service = Create(); service.ObserveOwn(999, 1, At);
        Assert.Equal(WorldContextStatus.Confirmed, service.Snapshot.Status);
        Assert.Equal(999, service.Snapshot.MapDataId);
        Assert.Equal(WorldContextKind.Unknown, service.Snapshot.Kind);
        Assert.Null(service.Snapshot.NameDe);
        Assert.Null(service.Snapshot.Mode);
        Assert.Null(service.Snapshot.SoulcrestMapId);
        service.ObserveOwn(null, 2, At);
        Assert.Equal(WorldContextStatus.Unknown, service.Snapshot.Status);
        Assert.Null(service.Snapshot.MapDataId);
    }

    [Fact]
    public void OnlyExplicitMapBridgeIsReturnedAndEachOwnCreatesANewGenerationEvenOnTheSameMap()
    {
        var service = Create(); service.ObserveOwn(100, 1, At);
        var before = service.Snapshot;
        Assert.Equal("synthetic-world", before.SoulcrestMapId);
        service.ObserveOwn(100, 2, At);
        Assert.True(service.Snapshot.Generation > before.Generation);
        Assert.NotSame(before, service.Snapshot);
    }

    [Fact]
    public void StateBeforeOwnDuringTransitionOrAfterStaleCannotConfirmALocation()
    {
        var service = Create(); var initial = service.Snapshot;
        service.ObserveDungeon(new(1000, DungeonRunState.Process, 0, 0), 0, At);
        Assert.Same(initial, service.Snapshot);
        foreach (var status in new[] { WorldContextStatus.Transitioning, WorldContextStatus.Stale })
        {
            service.Invalidate(status, 1, "synthetic-transition", At);
            var before = service.Snapshot;
            service.ObserveDungeon(new(1000, DungeonRunState.Process, 0, 0), 1, At);
            Assert.Same(before, service.Snapshot);
        }
    }

    [Fact]
    public void OnlyMatchingCurrentOwnGenerationAndNonRegressingObservationCanRefineDungeon()
    {
        var service = Create(); service.ObserveOwn(200, 3, At); var before = service.Snapshot;
        service.ObserveDungeon(new(1000, DungeonRunState.Process, 0, 0), 2, At);
        service.ObserveDungeon(new(1002, DungeonRunState.Process, 0, 0), 3, At);
        service.ObserveDungeon(new(1000, DungeonRunState.Process, 0, 0), 3, At.AddSeconds(-1));
        service.ObserveDungeon(new(1004, DungeonRunState.Process, 0, 0), 3, At);
        Assert.Same(before, service.Snapshot);
        service.ObserveDungeon(new(1001, DungeonRunState.Process, 0, 0), 3, At.AddSeconds(1));
        Assert.Equal(1001, service.Snapshot.DungeonId);
        Assert.Equal("hard", service.Snapshot.Mode);
        Assert.Equal(3, service.Snapshot.BindingGeneration);
        Assert.Equal(WorldContextKind.Instance, service.Snapshot.Kind);
    }

    [Fact]
    public void NativeLayerFlagPlusOwnStateCanRefineAnOpenWorldMapWithoutInferringFromBareJoins()
    {
        var service = Create(); service.ObserveOwn(100, 1, At); var before = service.Snapshot;
        Assert.Equal(WorldContextKind.OpenWorld, before.Kind);
        service.ObserveDungeon(new(1003, DungeonRunState.Process, 0, 0), 1, At);
        Assert.Same(before, service.Snapshot);
        service.ObserveDungeon(new(1002, DungeonRunState.Process, 0, 0), 1, At);
        Assert.Equal(WorldContextKind.Instance, service.Snapshot.Kind);
        Assert.Equal(100, service.Snapshot.MapDataId);
        Assert.Equal(1002, service.Snapshot.DungeonId);
        Assert.True(service.Snapshot.InstanceLayer);
        Assert.Equal("layer", service.Snapshot.Category);
    }

    [Fact]
    public void NoneAndEndDoNotOpenALayerButCannotClearAnAlreadyConfirmedLayer()
    {
        var service = Create(); service.ObserveOwn(100, 1, At); var open = service.Snapshot;
        foreach (var state in new[] { DungeonRunState.None, DungeonRunState.End })
            service.ObserveDungeon(new(1002, state, 0, 0), 1, At);
        Assert.Same(open, service.Snapshot);
        service.ObserveDungeon(new(1002, DungeonRunState.Process, 0, 0), 1, At);
        service.ObserveDungeon(new(1002, DungeonRunState.End, 0, 0), 1, At);
        Assert.Equal(WorldContextKind.Instance, service.Snapshot.Kind);
        Assert.Equal(1002, service.Snapshot.DungeonId);
    }

    [Fact]
    public void NativeLabelsAreCarriedWithoutGuessingCodeNamesAndClearedOnReset()
    {
        var service = Create(); service.ObserveOwn(100, 1, At);
        Assert.Equal("Testgebiet", service.Snapshot.CategoryNameDe);
        service.ObserveOwn(200, 2, At);
        service.ObserveDungeon(new(1000, DungeonRunState.Process, 0, 0), 2, At);
        Assert.Equal("Repeatable", service.Snapshot.CategoryNameEn);
        Assert.Equal("Normal", service.Snapshot.ModeNameDe);
        service.ObserveDungeon(new(1001, DungeonRunState.Process, 0, 0), 2, At);
        Assert.Null(service.Snapshot.CategoryNameDe);
        Assert.Null(service.Snapshot.ModeNameEn);
        Assert.Equal("Test instance", service.Snapshot.NameEn);
        service.Invalidate(WorldContextStatus.Stale, 3, "synthetic-gap", At);
        Assert.Null(service.Snapshot.ModeNameDe);
        Assert.Null(service.Snapshot.CategoryNameEn);
    }

    [Theory]
    [InlineData(DungeonRunState.None)] [InlineData(DungeonRunState.Complete)] [InlineData(DungeonRunState.End)]
    public void StateOrDeadlinesNeverInventAWorldExit(DungeonRunState state)
    {
        var service = Create(); service.ObserveOwn(200, 1, At);
        service.ObserveDungeon(new(1000, state, 1, 999999), 1, At);
        Assert.Equal(WorldContextStatus.Confirmed, service.Snapshot.Status);
        Assert.Equal(WorldContextKind.Instance, service.Snapshot.Kind);
        Assert.Equal(200, service.Snapshot.MapDataId);
    }

    [Fact]
    public void InvalidationSynchronouslyClearsAllAuthorityAndOldCallbacksCannotRestoreIt()
    {
        var service = Create(); service.ObserveOwn(100, 1, At);
        service.Changed += () => Assert.Null(service.Snapshot.MapDataId);
        service.Invalidate(WorldContextStatus.Stale, 2, "synthetic-gap", At);
        var stale = service.Snapshot;
        service.ObserveOwn(100, 1, At);
        service.ObserveDungeon(new(1002, DungeonRunState.Process, 0, 0), 1, At);
        Assert.Same(stale, service.Snapshot);
        Assert.Null(stale.SoulcrestMapId);
    }

    [Fact]
    public void ReentrantNewOwnCannotBeOverwrittenByTheOlderNotification()
    {
        var service = Create();
        service.Changed += () =>
        {
            if (service.Snapshot.MapDataId == 100) service.ObserveOwn(200, 2, At);
        };
        service.ObserveOwn(100, 1, At);
        Assert.Equal(200, service.Snapshot.MapDataId);
    }
}
