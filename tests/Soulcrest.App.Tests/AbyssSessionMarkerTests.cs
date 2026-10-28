using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic explicit outcomes and visual fixes; no application, game or capture access.</summary>
public sealed class AbyssSessionMarkerTests
{
    [Fact]
    public async Task OwnKillAndDeathAreDurableWithoutFeedAndNeverBecomeIncomeOrOpponentData()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Settings.Current.AbyssKillFeedEnabled = false;
        f.Settings.Current.AbyssKillFeedShowKills = false; f.Settings.Current.AbyssKillFeedShowDeaths = false;
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Position = f.Fix(100, 200);
        f.Combat(1, NetworkAbyssCombatKind.Kill); f.Combat(1, NetworkAbyssCombatKind.Kill);
        f.Combat(2, NetworkAbyssCombatKind.Assist); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Position = null; f.Combat(3, NetworkAbyssCombatKind.Death); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(new[] { LootTrackerCombatKind.Kill, LootTrackerCombatKind.Death }, session.CombatEvents.Select(item => item.Kind));
        Assert.Equal(100, session.CombatEvents[0].Position!.X); Assert.Null(session.CombatEvents[1].Position);
        Assert.Equal(TimeSpan.FromSeconds(2), session.CombatEvents[1].Elapsed);
        Assert.Null(session.AbyssPoints); Assert.Null(session.RankPointsGained); Assert.Null(session.RankPointsLost);
        Assert.Null(session.KinaUnbound); Assert.Empty(session.MinuteIncome); Assert.Empty(session.Loot);
        var read = new UnifiedLootSessionStore(f.SessionRoot).Read(); Assert.Empty(read.Issues);
        Assert.Equal(session.CombatEvents.ToArray(), Assert.Single(read.Sessions).CombatEvents.ToArray());
        var journal = string.Join("", f.Journal);
        Assert.DoesNotContain("Synthetic opponent", journal); Assert.DoesNotContain("OpponentName", journal);
        Assert.DoesNotContain("RankPointDelta", journal); Assert.DoesNotContain("CharacterId", journal);
        Assert.Equal(3, f.Journal.Count);
    }

    [Theory]
    [InlineData("stale-position")]
    [InlineData("future-position")]
    [InlineData("stale-detection")]
    [InlineData("future-detection")]
    [InlineData("binding")]
    [InlineData("world")]
    [InlineData("map-generation")]
    [InlineData("other-map")]
    [InlineData("nan")]
    public async Task MissingPositionAuthorityStillKeepsTheTemporalCombatOutcome(string invalid)
    {
        await using var f = new Fixture(); await f.Ready(); f.Clock.Advance(TimeSpan.FromSeconds(6));
        var fix = f.Fix(100, 200);
        f.Position = invalid switch
        {
            "stale-position" => fix with { Position = fix.Position with { At = f.Clock.GetUtcNow().AddSeconds(-2.001) } },
            "future-position" => fix with { Position = fix.Position with { At = f.Clock.GetUtcNow().AddTicks(1) } },
            "stale-detection" => fix with { DetectedAt = f.Clock.GetUtcNow().AddSeconds(-5.001) },
            "future-detection" => fix with { DetectedAt = f.Clock.GetUtcNow().AddTicks(1) },
            "binding" => fix with { BindingGeneration = 99 },
            "world" => fix with { WorldGeneration = 99 },
            "map-generation" => fix with { MapGeneration = 99 },
            "other-map" => fix with { Position = fix.Position with { MapId = "other-map" } },
            _ => fix with { Position = fix.Position with { X = double.NaN } }
        };
        f.Combat(1, NetworkAbyssCombatKind.Death); await f.Ready();
        var marker = Assert.Single(Assert.Single(f.Service.Snapshot.Sessions).CombatEvents);
        Assert.Null(marker.Position); Assert.Equal(f.Clock.GetUtcNow(), marker.At);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("source-stopped")]
    [InlineData("disabled")]
    [InlineData("network-disabled")]
    [InlineData("foreign")]
    [InlineData("unbound")]
    [InlineData("outside")]
    [InlineData("stale-world")]
    [InlineData("transition")]
    [InlineData("open-world")]
    [InlineData("wrong-binding")]
    [InlineData("future-world")]
    [InlineData("future")]
    [InlineData("default-time")]
    [InlineData("before-own")]
    [InlineData("zero-sequence")]
    public async Task OnlyExplicitCurrentOwnSupportedAbyssOutcomesCanBecomeMarkers(string invalid)
    {
        await using var f = new Fixture(); await f.Ready(); var outcome = f.Outcome(1, NetworkAbyssCombatKind.Kill);
        switch (invalid)
        {
            case "unsupported": f.Source.CombatCapabilities = NetworkAbyssCombatCapabilities.None; break;
            case "source-stopped": f.Source.Running = false; break;
            case "disabled": f.Settings.Current.AbyssTrackingEnabled = false; break;
            case "network-disabled": f.Settings.Current.LootTrackingEnabled = false; break;
            case "foreign": outcome = outcome with { Character = outcome.Character with { } }; break;
            case "unbound": f.Bound = false; break;
            case "outside": f.Category = "Party"; break;
            case "stale-world": f.WorldStatus = WorldContextStatus.Stale; break;
            case "transition": f.WorldStatus = WorldContextStatus.Transitioning; break;
            case "open-world": f.WorldKind = WorldContextKind.OpenWorld; break;
            case "wrong-binding": f.BindingOffset = 1; break;
            case "future-world": f.WorldOffset = TimeSpan.FromSeconds(1); break;
            case "future": outcome = outcome with { At = f.Clock.GetUtcNow().AddTicks(1) }; break;
            case "default-time": outcome = outcome with { At = default }; break;
            case "before-own": outcome = outcome with { At = f.Clock.GetUtcNow().AddTicks(-1) }; break;
            case "zero-sequence": outcome = outcome with { Sequence = 0 }; break;
        }
        f.Source.Emit(outcome); await f.Ready();
        Assert.All(f.Service.Snapshot.Sessions, session => Assert.Empty(session.CombatEvents));
    }

    [Fact]
    public async Task PositionIsFrozenBeforeBindingCanObserveAnotherVisualFix()
    {
        await using var f = new Fixture(); await f.Ready(); f.Position = f.Fix(100, 200);
        var capturedAt = f.Clock.GetUtcNow();
        f.WorldObservedAt = capturedAt;
        f.BeforeBind = () => { f.BeforeBind = null; f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Position = f.Fix(900, 950); };
        f.Combat(1, NetworkAbyssCombatKind.Kill); await f.Ready();
        var combat = Assert.Single(Assert.Single(f.Service.Snapshot.Sessions).CombatEvents);
        var position = combat.Position!;
        Assert.Equal(100, position.X); Assert.Equal(200, position.Y);
        Assert.Equal(capturedAt, combat.At); Assert.Equal(TimeSpan.Zero, combat.Elapsed);
        f.BeforeBind = () => { f.BeforeBind = null; f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Position = f.Fix(900, 950); };
        f.Position = f.Fix(120, 220); f.Gather(1, 610610013, 2); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        var visit = Assert.Single(session.GatherVisits);
        Assert.Equal(120, visit.OriginalX); Assert.Equal(220, visit.OriginalY);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(session.TimelineEvents,
            row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess).Elapsed);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("transition")]
    [InlineData("abyss-map")]
    [InlineData("own")]
    public async Task AnAlreadyQueuedOutcomeCannotCrossANewerWorldOrOwnBoundary(string boundary)
    {
        await using var f = new Fixture(); await f.Ready();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.BeforeAppend = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        f.Source.Drop(1); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            f.Combat(1, NetworkAbyssCombatKind.Kill); f.WorldGeneration++;
            if (boundary == "outside") f.Category = "Party";
            if (boundary == "transition") f.WorldStatus = WorldContextStatus.Transitioning;
            if (boundary == "own") f.Source.ChangeOwn(null); else f.Source.Notify();
        }
        finally { release.Set(); }
        await f.Ready(); Assert.All(f.Service.Snapshot.Sessions, session => Assert.Empty(session.CombatEvents));
    }

    [Fact]
    public async Task NewOwnGenerationRestartsCombatSequenceAndDeletedResidenceStaysDeleted()
    {
        await using var f = new Fixture(); await f.Ready(); f.Combat(10, NetworkAbyssCombatKind.Kill); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.WorldStatus = WorldContextStatus.Transitioning; f.Source.ChangeOwn(null); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.WorldStatus = WorldContextStatus.Confirmed;
        f.Source.ChangeOwn(new(new(1, 1, 2, "Synthetic character"), 2) { ObservedAt = f.Clock.GetUtcNow() });
        await f.Ready(); f.Combat(1, NetworkAbyssCombatKind.Death); await f.Ready();
        Assert.Equal(id, Assert.Single(f.Service.Snapshot.Sessions).Id);
        Assert.Equal(2, Assert.Single(f.Service.Snapshot.Sessions).CombatEvents.Length);
        await f.Service.DeleteSessionAsync(id); f.Combat(2, NetworkAbyssCombatKind.Kill); f.Source.Notify(); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions);
    }

    [Fact]
    public async Task FreshOdyleHarvestUsesExistingFifteenMeterResolverAndNeverBuildsAFarmRoute()
    {
        var resolver = new LifeskillGatherPointResolver(new ResourceNodeCatalog([
            new("odyle-node", "test-map", 123, "resource-od", "Odyle", "Odyle", 110, 200, 110, 200)]), _ => 1, _ => 8192);
        await using var f = new Fixture(resolver); await f.Ready(); f.Clock.Advance(TimeSpan.FromSeconds(6));
        f.Position = f.Fix(100, 200) with { Position = f.Fix(100, 200).Position with { At = f.Clock.GetUtcNow().AddSeconds(-2) }, DetectedAt = f.Clock.GetUtcNow().AddSeconds(-5) };
        f.Gather(1, 610610013, 3); f.Gather(1, 610610013, 3); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions); var visit = Assert.Single(session.GatherVisits);
        Assert.Equal(100, visit.OriginalX); Assert.Equal(110, visit.X); Assert.Equal("odyle-node", visit.CatalogNodeId);
        Assert.Equal(123, visit.GatherableId); Assert.Equal(3, session.ItemCount);
        Assert.Equal(0, session.FarmRouteVersion); Assert.Empty(session.FarmRoutePoints);
        // Catalogue snapping belongs only to the marker. The walked path retains the actual visual fix.
        Assert.Equal(100, session.RouteSegments.SelectMany(segment => segment.Points).Last().X);
        Assert.Equal(200, session.RouteSegments.SelectMany(segment => segment.Points).Last().Y);
        Assert.Equal(visit, Assert.Single(Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions).GatherVisits));
        var journal = string.Join("", f.Journal); Assert.DoesNotContain("999123999", journal); Assert.DoesNotContain("999456999", journal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("wrong-world")]
    [InlineData("outside-map")]
    public async Task MissingHarvestPlacementKeepsLootWithoutInventingVisit(string invalid)
    {
        await using var f = new Fixture(); await f.Ready(); f.Clock.Advance(TimeSpan.FromSeconds(6)); f.Position = f.Fix(100, 200);
        if (invalid == "missing") f.Position = null;
        if (invalid == "stale") f.Position = f.Position! with { Position = f.Position.Position with { At = f.Clock.GetUtcNow().AddSeconds(-3) } };
        if (invalid == "wrong-world") f.Position = f.Position! with { WorldGeneration = 99 };
        if (invalid == "outside-map") f.Position = f.Fix(9999, 9999);
        f.Gather(1, 610610013, 2); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions); Assert.Equal(2, session.ItemCount); Assert.Empty(session.GatherVisits);
        Assert.False(f.Service.StatusIsError);
    }

    [Fact]
    public async Task AQualifiedNonOdyleAbyssHarvestRetainsItsActualGatheringVisitWithoutOdyleClassification()
    {
        await using var f = new Fixture(); await f.Ready(); f.Clock.Advance(TimeSpan.FromSeconds(6)); f.Position = f.Fix(100, 200);
        f.Gather(1, 210350009, 2); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(2, session.ItemCount);
        var visit = Assert.Single(session.GatherVisits);
        Assert.Equal((100d, 200d), (visit.OriginalX, visit.OriginalY));
        var marker = Assert.Single(LootTrackerSessionMarkers.All(session));
        Assert.Equal(LootTrackerSessionMarkerKind.Gathering, marker.Kind);
        var reward = Assert.Single(marker.Items);
        Assert.Equal("Item:210350009", reward.Id); Assert.Equal(2, reward.Quantity);
        Assert.False(f.Service.StatusIsError);
    }

    [Fact]
    public async Task FailedMarkerAppendEmitsNoPartialDurableMarker()
    {
        await using var f = new Fixture(); await f.Ready(); f.FailAppend = true;
        f.Combat(1, NetworkAbyssCombatKind.Death); await f.Ready(); Assert.True(f.Service.StatusIsError);
        Assert.Empty(Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions).CombatEvents);
    }

    [Fact]
    public async Task AnonymousOpponentClassAndActualGradeAreFrozenWithoutNamesNativeIdsOrPointAttribution()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.Emit(f.Outcome(1, NetworkAbyssCombatKind.Kill)
            with { OpponentClassId = "Assassin", OpponentAbyssRankGrade = 5, AbyssPointGain = 123 });
        f.Source.Emit(f.Outcome(2, NetworkAbyssCombatKind.Death)
            with { OpponentClassId = "Synthetic opponent", OpponentAbyssRankGrade = 99 });
        await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal("Assassin", session.CombatEvents[0].OpponentClassId); Assert.Equal(5, session.CombatEvents[0].OpponentAbyssRankGrade);
        Assert.Null(session.CombatEvents[1].OpponentClassId); Assert.Null(session.CombatEvents[1].OpponentAbyssRankGrade);
        Assert.DoesNotContain(session.TimelineEvents, row => row.Kind != LootTrackerTimelineEventKind.Availability);
        var read = new UnifiedLootSessionStore(f.SessionRoot).Read(); Assert.Empty(read.Issues);
        Assert.Equal(session.CombatEvents.ToArray(), Assert.Single(read.Sessions).CombatEvents.ToArray());
        var journal = string.Join("", f.Journal);
        Assert.DoesNotContain("Synthetic opponent", journal); Assert.DoesNotContain("OpponentName", journal);
        Assert.DoesNotContain("RankPointDelta", journal); Assert.DoesNotContain("CharacterId", journal);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-abyss-markers-" + Guid.NewGuid().ToString("N"));
        internal string SessionRoot => Path.Combine(Root, "sessions");
        internal SettingsService Settings { get; } = new();
        internal ManualTime Clock { get; } = new();
        internal FakeSource Source { get; }
        internal UnifiedLootTrackerService Service { get; }
        internal List<string> Journal { get; } = [];
        internal bool Bound = true, FailAppend;
        internal string Category = "Abyss";
        internal WorldContextKind WorldKind = WorldContextKind.Instance;
        internal WorldContextStatus WorldStatus = WorldContextStatus.Confirmed;
        internal long BindingOffset, WorldGeneration = 1;
        internal TimeSpan WorldOffset;
        internal DateTimeOffset? WorldObservedAt;
        internal Action? BeforeBind, BeforeAppend;
        internal RecorderPositionSnapshot? Position;
        internal Fixture(LifeskillGatherPointResolver? resolver = null)
        {
            Source = new(Clock); Settings.Current.AbyssTrackingEnabled = true; Settings.Current.LootTrackingEnabled = true;
            Service = new(Settings, Source, Source, Source, Source,
                _ => { BeforeBind?.Invoke(); return Bound ? new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Synthetic character") : null; },
                () => new(WorldStatus, WorldKind, 10, null, Category, null, "Abyss", "Abyss", WorldGeneration,
                    (WorldObservedAt ?? Clock.GetUtcNow()) + WorldOffset, "synthetic") { BindingGeneration = Source.OwnCharacter?.Generation + BindingOffset, SoulcrestMapId = "test-map" },
                new(SessionRoot, (path, text) => { Interlocked.Exchange(ref BeforeAppend, null)?.Invoke(); if (FailAppend) throw new IOException("Synthetic marker append failure."); File.AppendAllText(path, text); Journal.Add(text); }, JsonFile.Save),
                Clock,
                () => Position, mapName: _ => "Test map", pointResolver: resolver,
                dailyStore: new(Path.Combine(Root, "daily.json")));
        }
        internal RecorderPositionSnapshot Fix(double x, double y) => new(new("test-map", x, y, Clock.GetUtcNow()) { ContextGeneration = 1 },
            1, WorldGeneration, Source.OwnCharacter?.Generation, Clock.GetUtcNow(), 10, 0);
        internal NetworkAbyssCombatEvent Outcome(long sequence, NetworkAbyssCombatKind kind) =>
            new(Clock.GetUtcNow(), sequence, Source.OwnCharacter!, kind, "Synthetic opponent", 999999);
        internal void Combat(long sequence, NetworkAbyssCombatKind kind) => Source.Emit(Outcome(sequence, kind));
        internal void Gather(long sequence, int item, long quantity) => Source.Emit(new NetworkGathering(Clock.GetUtcNow(), sequence,
            Source.OwnCharacter!, 999123999, [new(item, quantity)]) { InInstance = true, Object = new(123, 999456999, 999456999) });
        internal Task Ready() => Service.DrainAsync();
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class FakeSource(ManualTime clock) : INetworkAbyssSource, INetworkFarmSource, INetworkKinaSource, INetworkGatheringSource, INetworkAbyssCombatSource
    {
        public bool Running { get; set; } = true;
        public bool GatheringVerified => true;
        public string GatheringStatus => "synthetic";
        public NetworkAbyssCombatCapabilities CombatCapabilities { get; set; } = new(true, true, true);
        public OwnCharacterContext? OwnCharacter { get; private set; } = new(new(1, 1, 1, "Synthetic character"), 1) { ObservedAt = clock.GetUtcNow() };
        public NetworkAbyssSnapshot AbyssSnapshot => NetworkAbyssSnapshot.Empty;
        public NetworkKinaSnapshot KinaSnapshot => new(null, null, null, false, "synthetic");
        public NetworkDailyMonsterKinaSnapshot DailyMonsterKinaSnapshot => new(null, null, null, 0);
        public event Action? Changed;
        public event Action? OwnCharacterChanged;
        public event Action<NetworkAbyssPointGain>? AbyssPointsGained { add { } remove { } }
        public event Action<NetworkAbyssRankChange>? RankPointsChanged { add { } remove { } }
        public event Action<NetworkKinaGain>? KinaGained { add { } remove { } }
        public event Action<NetworkKinaGain>? MonsterKinaGained { add { } remove { } }
        public event Action<NetworkFarmDrop>? FarmDropGained;
        public event Action<NetworkGathering>? GatheringCompleted;
        public event Action<NetworkAbyssCombatEvent>? CombatEvent;
        internal void Emit(NetworkAbyssCombatEvent outcome) => CombatEvent?.Invoke(outcome);
        internal void Emit(NetworkGathering harvest) => GatheringCompleted?.Invoke(harvest);
        internal void Notify() => Changed?.Invoke();
        internal void ChangeOwn(OwnCharacterContext? own) { OwnCharacter = own; OwnCharacterChanged?.Invoke(); Changed?.Invoke(); }
        internal void Drop(long sequence) => FarmDropGained?.Invoke(new(clock.GetUtcNow(), sequence, OwnCharacter!, 210350009, 1, NetworkFarmDropKind.Item, true));
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Timer();
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
