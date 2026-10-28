using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic qualified gathering envelopes; no capture, process or application start.</summary>
public sealed class AbyssGatheringIntegrationTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task WholeOdyleHarvestIsOneDurableLootUpdateWithExistingNamesAndIcons(string language)
    {
        await using var f = new Fixture(language); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        var records = f.Journal.Count; f.Clock.Advance(TimeSpan.FromSeconds(5));
        f.Gather(1, new NetworkGatheringItem(610610013, 1), new NetworkGatheringItem(610610014, 2), new NetworkGatheringItem(610610015, 3), new NetworkGatheringItem(610610016, 4));
        await f.Ready(); var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(id, session.Id); Assert.Equal(10, session.ItemCount); Assert.Equal(4, session.Loot.Length);
        Assert.Equal(records + 1, f.Journal.Count); Assert.Equal(4, f.Accepted.Count);
        var timeline = Assert.Single(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess);
        Assert.Equal(TimeSpan.FromSeconds(5), timeline.Elapsed); Assert.Equal(10, timeline.Loot.Sum(row => row.Quantity));
        Assert.Equal(4, timeline.Loot.Length);
        foreach (var item in session.Loot)
        {
            Assert.True(MonsterLootItemCatalog.TryItemId(item.Id, out var itemId));
            Assert.Equal(LifeskillItemNameCatalog.Default.Name(itemId, language), item.Name);
            Assert.Equal(MonsterLootItemCatalog.Default.Name(itemId, language), item.Name);
            Assert.NotNull(MonsterLootIconCatalog.Default.DropAsset(item.Id));
            Assert.Equal(item.Quantity, Assert.Single(f.Accepted, drop => drop.LootKey == item.Id).Quantity);
        }
        var recovered = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(10, recovered.ItemCount); Assert.Equal(session.Loot.ToArray(), recovered.Loot.ToArray());
        Assert.Null(session.KinaUnbound); Assert.Null(session.AbyssPoints); Assert.Empty(session.MinuteIncome);
        var journal = string.Join("", f.Journal);
        Assert.DoesNotContain("TargetKey", journal); Assert.DoesNotContain("OriginSpawnKey", journal);
        Assert.DoesNotContain("SpawnUniqueKey", journal); Assert.DoesNotContain("999123999", journal);
        Assert.DoesNotContain("999456999", journal);
    }

    [Fact]
    public async Task GatherSequenceIsIndependentFromFarmDropsAndDuplicateRowsMergeAtomically()
    {
        await using var f = new Fixture(); await f.Ready(); f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Source.Drop(1, 610610013, 2); f.Gather(1, new NetworkGatheringItem(610610013, 3), new NetworkGatheringItem(610610013, 4)); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(9, session.ItemCount); Assert.Equal(9, Assert.Single(session.Loot).Quantity);
        Assert.Equal(new long[] { 2, 7 }, f.Accepted.Select(drop => drop.Quantity));
        f.Gather(1, new NetworkGatheringItem(610610013, 7)); f.Gather(0, new NetworkGatheringItem(610610013, 7)); f.Source.Drop(1, 610610013, 2);
        await f.Ready(); Assert.Equal(9, Assert.Single(f.Service.Snapshot.Sessions).ItemCount);
        f.Gather(2, new NetworkGatheringItem(610610014, 1)); await f.Ready();
        Assert.Equal(10, Assert.Single(f.Service.Snapshot.Sessions).ItemCount);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("empty")]
    [InlineData("default-items")]
    [InlineData("invalid-id")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("null-item")]
    public async Task IncompleteOrUnsuccessfulRewardsCannotPartiallyPersist(string invalid)
    {
        await using var f = new Fixture(); await f.Ready(); var records = f.Journal.Count;
        var gathering = f.Harvest(1, new NetworkGatheringItem(610610013, 2), new NetworkGatheringItem(610610014, 3));
        gathering = invalid switch
        {
            "failed" => gathering with { Outcome = NetworkGatheringOutcome.Failed },
            "unknown" => gathering with { Outcome = (NetworkGatheringOutcome)999 },
            "empty" => gathering with { Items = [] }, "default-items" => gathering with { Items = default },
            "invalid-id" => gathering with { Items = [new NetworkGatheringItem(610610013, 2), new(0, 3)] },
            "zero" => gathering with { Items = [new NetworkGatheringItem(610610013, 2), new NetworkGatheringItem(610610014, 0)] },
            "negative" => gathering with { Items = [new NetworkGatheringItem(610610013, 2), new NetworkGatheringItem(610610014, -1)] },
            _ => gathering with { Items = [new NetworkGatheringItem(610610013, 2), null!] }
        };
        f.Source.Emit(gathering); await f.Ready();
        Assert.Equal(records, f.Journal.Count); Assert.Empty(f.Accepted);
        Assert.Empty(Assert.Single(f.Service.Snapshot.Sessions).Loot);
        // A rejected envelope did not consume an otherwise valid completion's independent sequence.
        f.Gather(1, new NetworkGatheringItem(610610013, 1)); await f.Ready(); Assert.Single(f.Accepted);
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("unverified")]
    [InlineData("disabled")]
    [InlineData("network-disabled")]
    [InlineData("foreign")]
    [InlineData("unbound")]
    [InlineData("other-category")]
    [InlineData("open-world")]
    [InlineData("stale")]
    [InlineData("transition")]
    [InlineData("wrong-generation")]
    [InlineData("future-world")]
    [InlineData("future")]
    [InlineData("default-time")]
    [InlineData("before-own")]
    [InlineData("not-in-instance")]
    public async Task OnlyCurrentQualifiedOwnAbyssHarvestsAreAccepted(string gate)
    {
        await using var f = new Fixture(); await f.Ready(); var harvest = f.Harvest(1, new NetworkGatheringItem(610610013, 3));
        switch (gate)
        {
            case "stopped": f.Source.Running = false; break;
            case "unverified": f.Source.GatheringVerified = false; break;
            case "disabled": f.Settings.Current.AbyssTrackingEnabled = false; break;
            case "network-disabled": f.Settings.Current.LootTrackingEnabled = false; break;
            case "foreign": harvest = harvest with { Character = harvest.Character with { } }; break;
            case "unbound": f.Bound = false; break;
            case "other-category": f.Category = "Party"; break;
            case "open-world": f.WorldKind = WorldContextKind.OpenWorld; break;
            case "stale": f.WorldStatus = WorldContextStatus.Stale; break;
            case "transition": f.WorldStatus = WorldContextStatus.Transitioning; break;
            case "wrong-generation": f.BindingOffset = 1; break;
            case "future-world": f.WorldOffset = TimeSpan.FromSeconds(1); break;
            case "future": harvest = harvest with { At = f.Clock.GetUtcNow().AddSeconds(1) }; break;
            case "default-time": harvest = harvest with { At = default }; break;
            case "before-own": harvest = harvest with { At = f.Clock.GetUtcNow().AddTicks(-1) }; break;
            case "not-in-instance": harvest = harvest with { InInstance = false }; break;
        }
        f.Source.Emit(harvest); await f.Ready(); Assert.Empty(f.Accepted);
        Assert.All(f.Service.Snapshot.Sessions, session => Assert.Empty(session.Loot));
    }

    [Fact]
    public async Task GapOrOwnReplacementRejectsAlreadyQueuedOldHarvests()
    {
        await using var f = new Fixture(); await f.Ready();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.BeforeAppend = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        f.Source.Drop(1, 210350009, 1);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try { f.Gather(1, new NetworkGatheringItem(610610013, 3)); f.Source.ChangeOwn(null); }
        finally { release.Set(); }
        await f.Ready(); Assert.DoesNotContain(f.Accepted, drop => drop.LootKey == "Item:610610013");
        Assert.All(f.Service.Snapshot.Sessions, session => Assert.DoesNotContain(session.Loot, item => item.Id == "Item:610610013"));
    }

    [Theory]
    [InlineData("outside", true)] [InlineData("outside", false)]
    [InlineData("transition", true)] [InlineData("transition", false)]
    [InlineData("stale", true)] [InlineData("stale", false)]
    [InlineData("abyss-map", true)] [InlineData("abyss-map", false)]
    public async Task NewWorldBoundaryRejectsHarvestWhoseCapturedWorldWasSuperseded(string boundary, bool observationFirst)
    {
        await using var f = new Fixture(); await f.Ready();
        var initialId = Assert.Single(f.Service.Snapshot.Sessions).Id;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Task? sending = null;
        if (observationFirst)
        {
            var firstBinding = 0;
            f.BeforeBind = () =>
            {
                if (Interlocked.Exchange(ref firstBinding, 1) != 0) return;
                entered.Set(); release.Wait(TimeSpan.FromSeconds(5));
            };
            // Gathered has captured world 1, but its bound-profile copy delays enqueueing Harvest.
            sending = Task.Run(() => f.Gather(1, new NetworkGatheringItem(610610013, 3)));
        }
        else
        {
            f.BeforeAppend = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
            f.Source.Drop(1, 210350009, 1); // Keep the worker before the queued old-world Harvest.
        }
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            if (!observationFirst) f.Gather(1, new NetworkGatheringItem(610610013, 3));
            f.Clock.Advance(TimeSpan.FromSeconds(1)); f.WorldGeneration++;
            if (boundary == "outside") f.Category = "Party";
            else if (boundary == "transition") f.WorldStatus = WorldContextStatus.Transitioning;
            else if (boundary == "stale") f.WorldStatus = WorldContextStatus.Stale;
            f.Source.Notify();
            if (observationFirst) await f.Ready();
        }
        finally { release.Set(); }
        if (sending is not null) await sending.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Ready();
        Assert.DoesNotContain(f.Accepted, drop => drop.LootKey == "Item:610610013");
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(initialId, session.Id); Assert.DoesNotContain(session.Loot, item => item.Id == "Item:610610013");
        Assert.Equal(boundary is "outside" or "stale", session.Status != LootTrackerSessionStatus.Live);
    }

    [Fact]
    public async Task NewOwnGenerationCanRestartGatherSequenceWithinTheSameAbyssResidence()
    {
        await using var f = new Fixture(); await f.Ready(); f.Gather(10, new NetworkGatheringItem(610610013, 2)); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.WorldStatus = WorldContextStatus.Transitioning; f.Source.ChangeOwn(null); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(2)); f.WorldStatus = WorldContextStatus.Confirmed;
        f.Source.ChangeOwn(new(new(1, 1, 2, "Synthetic gatherer"), 2) { ObservedAt = f.Clock.GetUtcNow() });
        await f.Ready(); f.Gather(1, new NetworkGatheringItem(610610013, 3)); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(id, session.Id); Assert.Equal(5, session.ItemCount);
    }

    [Fact]
    public async Task AbyssHarvestAndOutsideLifeToggleUseOnlyTheOneAbyssParent()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Settings.Current.LifeskillTrackingEnabled = false;
        f.Gather(1, new NetworkGatheringItem(610610013, 3)); await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionKind.Abyss, session.Kind);
        Assert.Equal(3, session.ItemCount); Assert.Equal(1, session.HarvestCount);
        Assert.Equal(LootTrackerActivityOrigin.Lifeskill,
            Assert.Single(session.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess).Origin);
    }

    [Fact]
    public async Task OverflowRejectsWholeHarvestWithoutAnAcceptedItemOrPartialLoot()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Gather(1, new NetworkGatheringItem(610610013, long.MaxValue), new NetworkGatheringItem(610610014, 1)); await f.Ready();
        Assert.True(f.Service.StatusIsError); Assert.Empty(f.Accepted);
        Assert.Empty(Assert.Single(f.Service.Snapshot.Sessions).Loot);
        Assert.Empty(Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions).Loot);
    }

    [Fact]
    public async Task FailedAtomicAppendEmitsNoAcceptedDropAndPersistsNoPartialReward()
    {
        await using var f = new Fixture(); await f.Ready(); f.FailAppend = true;
        f.Gather(1, new NetworkGatheringItem(610610013, 2), new NetworkGatheringItem(610610014, 3)); await f.Ready();
        Assert.True(f.Service.StatusIsError); Assert.Empty(f.Accepted);
        var restored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Empty(restored.Loot);
        Assert.DoesNotContain(restored.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-abyss-gather-" + Guid.NewGuid().ToString("N"));
        internal string SessionRoot => Path.Combine(Root, "sessions");
        internal SettingsService Settings { get; } = new();
        internal ManualTime Clock { get; } = new();
        internal FakeSource Source { get; }
        internal UnifiedLootTrackerService Service { get; }
        internal List<string> Journal { get; } = [];
        internal List<AcceptedFarmLootDrop> Accepted { get; } = [];
        internal bool Bound = true, FailAppend;
        internal string Category = "Abyss";
        internal WorldContextKind WorldKind = WorldContextKind.Instance;
        internal WorldContextStatus WorldStatus = WorldContextStatus.Confirmed;
        internal long BindingOffset;
        internal long WorldGeneration = 1;
        internal TimeSpan WorldOffset;
        internal Action? BeforeBind, BeforeAppend;
        internal Func<RecorderPositionSnapshot?> Position = () => null;
        internal Fixture(string language = "en")
        {
            Source = new(Clock); Settings.Current.AbyssTrackingEnabled = true; Settings.Current.LootTrackingEnabled = true;
            Settings.Current.OverlayLanguage = language;
            var store = new UnifiedLootSessionStore(SessionRoot, (path, text) =>
            {
                Interlocked.Exchange(ref BeforeAppend, null)?.Invoke();
                if (FailAppend) throw new IOException("Synthetic harvest append failure.");
                File.AppendAllText(path, text); Journal.Add(text);
            }, JsonFile.Save);
            Service = new(Settings, Source, Source, Source, Source, Bind,
                () => new(WorldStatus, WorldKind, 10, null, Category, null, "Abyss", "Abyss", WorldGeneration,
                    Clock.GetUtcNow() + WorldOffset, "synthetic") { BindingGeneration = Source.OwnCharacter?.Generation + BindingOffset },
                store, Clock, () => Position(), itemName: id => LifeskillItemNameCatalog.Default.Name(id, language),
                dailyStore: new(Path.Combine(Root, "daily.json")));
            Service.DropAccepted += Accepted.Add;
        }
        internal LootTrackerCharacterSnapshot? Bind(OwnCharacterContext own)
        {
            BeforeBind?.Invoke();
            return Bound ? new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Synthetic gatherer", 1, 1) : null;
        }
        internal NetworkGathering Harvest(long sequence, params NetworkGatheringItem[] items) =>
            new(Clock.GetUtcNow(), sequence, Source.OwnCharacter!, 999123999, items.ToImmutableArray())
            { InInstance = true, Object = new(7001, 999456999, 999456999) };
        internal void Gather(long sequence, params NetworkGatheringItem[] items) => Source.Emit(Harvest(sequence, items));
        internal Task Ready() => Service.DrainAsync();
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeSource(ManualTime clock) : INetworkAbyssSource, INetworkFarmSource, INetworkKinaSource, INetworkGatheringSource
    {
        public bool Running { get; set; } = true;
        public bool GatheringVerified { get; set; } = true;
        public string GatheringStatus => "Synthetic gathering source";
        public OwnCharacterContext? OwnCharacter { get; private set; } = new(new(1, 1, 1, "Synthetic gatherer"), 1) { ObservedAt = clock.GetUtcNow() };
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
        internal void Emit(NetworkGathering gathering) => GatheringCompleted?.Invoke(gathering);
        internal void Notify() => Changed?.Invoke();
        internal void ChangeOwn(OwnCharacterContext? own) { OwnCharacter = own; OwnCharacterChanged?.Invoke(); Changed?.Invoke(); }
        internal void Drop(long sequence, int item, long quantity) =>
            FarmDropGained?.Invoke(new(clock.GetUtcNow(), sequence, OwnCharacter!, item, quantity, NetworkFarmDropKind.Item, true));
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
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
