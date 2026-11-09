using System.Collections.Immutable;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    private static readonly DateTimeOffset RetentionAt = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("Category", "AutomaticRetention")]
    [InlineData(19, true)]
    [InlineData(20, false)]
    [InlineData(21, false)]
    public async Task AutomaticRetentionUsesAStrictQuantityThresholdForExistingSessions(long quantity, bool deleted)
    {
        var row = RetentionSession(quantity);
        await using var f = new RetentionFixture(seed: (store, _) => store.Append(row));
        await f.Ready();
        Assert.Equal(deleted, f.Service.Snapshot.Sessions.IsEmpty);
        Assert.Equal(deleted, !Directory.Exists(Path.Combine(f.SessionRoot, row.Id)));
        Assert.Equal(deleted ? 1 : 0, f.Service.AutomaticallyDeletedSmallSessionCount);
        if (!deleted)
        {
            var kept = Assert.Single(f.Service.Snapshot.Sessions);
            Assert.Equal(quantity, kept.ItemCount);
            Assert.Single(kept.Loot); // One item definition with twenty units is large enough.
        }
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task ZeroThresholdKeepsExistingAndNewlyClosedSmallSessions()
    {
        var historical = RetentionSession(1);
        await using var f = new RetentionFixture(threshold: 0, seed: (store, _) => store.Append(historical));
        await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromMinutes(2)); await f.Ready();
        Assert.Equal(2, f.Service.Snapshot.Sessions.Length);
        Assert.All(f.Service.Snapshot.Sessions, row => Assert.Equal(LootTrackerSessionStatus.Completed, row.Status));
        Assert.Equal(0, f.Service.AutomaticallyDeletedSmallSessionCount);
    }

    [Theory]
    [Trait("Category", "AutomaticRetention")]
    [InlineData(LootTrackerSessionKind.Grind)]
    [InlineData(LootTrackerSessionKind.Dungeon)]
    [InlineData(LootTrackerSessionKind.Lifeskill)]
    public async Task ExistingLegacySessionsUseTheirOriginalDeleteDelegateRegardlessOfView(LootTrackerSessionKind kind)
    {
        var row = RetentionSession(19) with { UnifiedVersion = 0, TimelineVersion = 0, Kind = kind, TimelineEvents = [] };
        var deleted = new List<string>();
        await using var f = new RetentionFixture(legacy: [row], deleteLegacy: session =>
        { deleted.Add(session.Id); return false; });
        f.Settings.Current.LootTrackerViewMode = LootTrackerViewMode.Abyss;
        await f.Ready();
        Assert.Equal(new[] { row.Id }, deleted);
        Assert.Empty(f.Service.Snapshot.Sessions);
        Assert.Equal(1, f.Service.AutomaticallyDeletedSmallSessionCount);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task HistoricalAndLiveAbyssSessionsRemainOutsideAutomaticRetention()
    {
        var historical = RetentionSession(1, abyss: true);
        await using var f = new RetentionFixture(abyss: true, seed: (store, _) => store.Append(historical));
        await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromMinutes(10)); await f.Ready();
        Assert.Equal(2, f.Service.Snapshot.Sessions.Length);
        Assert.Contains(f.Service.Snapshot.Sessions, row => row.Id == historical.Id);
        Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        Assert.All(f.Service.Snapshot.Sessions, row => Assert.Equal(LootTrackerSessionKind.Abyss, row.Kind));
        Assert.Equal(0, f.Service.AutomaticallyDeletedSmallSessionCount);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task ActualLegacyStoresRetainTheirDeletionAndRestartContracts()
    {
        var small = RetentionSession(19) with { UnifiedVersion = 0, TimelineVersion = 0,
            Kind = LootTrackerSessionKind.Grind, TimelineEvents = [] };
        var abyss = small with { Id = Guid.NewGuid().ToString("N"), Kind = LootTrackerSessionKind.Abyss,
            LastPresenceAt = small.EndedAt };
        await using var f = new RetentionFixture(seedLegacy: root =>
        {
            new FarmLootSessionStore(Path.Combine(root, "monster-sessions")).Append(small);
            new FarmLootSessionStore(Path.Combine(root, "abyss-sessions")).Append(abyss);
        });
        await f.Ready();
        Assert.Equal(abyss.Id, Assert.Single(f.Service.Snapshot.Sessions).Id);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "legacy", "monster-sessions", small.Id)));
        var restarted = new UnifiedLootLegacyHistory(Path.Combine(f.Root, "legacy")).ReadSessions();
        Assert.False(restarted.Incomplete); Assert.Equal(abyss.Id, Assert.Single(restarted.Sessions).Id);
        Assert.Equal(1, f.Service.AutomaticallyDeletedSmallSessionCount);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task ANewSmallSessionIsKeptLiveAndPausedThenDeletedOnlyAfterItsDeadline()
    {
        await using var f = new RetentionFixture(); await f.Ready();
        var callbacks = new List<(string Id, bool DirectoryExists)>();
        f.Service.SessionDeleted += id => callbacks.Add((id, Directory.Exists(Path.Combine(f.SessionRoot, id))));
        f.Source.Drop(7, 19, 1); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Ready();
        var paused = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(id, paused.Id); Assert.Equal(LootTrackerSessionStatus.Live, paused.Status); Assert.True(paused.IsPaused);
        Assert.Equal(0, f.Service.AutomaticallyDeletedSmallSessionCount); Assert.Empty(callbacks);
        f.Clock.Advance(TimeSpan.FromSeconds(100)); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions); Assert.Equal(1, f.Service.AutomaticallyDeletedSmallSessionCount);
        var notification = Assert.Single(callbacks);
        Assert.Equal(id, notification.Id); Assert.False(notification.DirectoryExists); // Tombstone precedes callback.
        Assert.Empty(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        f.Source.Drop(7, 19, 1); await f.Ready(); // Late echo cannot recreate a deleted session.
        Assert.Empty(f.Service.Snapshot.Sessions);
        f.Source.Drop(7, 20, 2); await f.Ready();
        Assert.Equal(20, Assert.Single(f.Service.Snapshot.Sessions).ItemCount);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task ChangingTheThresholdSweepsAllClosedHistoryButNeverTheCurrentSession()
    {
        var smaller = RetentionSession(20); var larger = RetentionSession(100);
        await using var f = new RetentionFixture(threshold: 20, seed: (store, _) => { store.Append(smaller); store.Append(larger); });
        await f.Ready(); f.Source.Drop(7, 1, 1); await f.Ready();
        var live = Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live);
        f.Settings.Current.LootTrackerSmallSessionItemThreshold = 21; f.Source.Notify(); await f.Ready();
        Assert.DoesNotContain(f.Service.Snapshot.Sessions, row => row.Id == smaller.Id);
        Assert.Contains(f.Service.Snapshot.Sessions, row => row.Id == larger.Id);
        Assert.Contains(f.Service.Snapshot.Sessions, row => row.Id == live.Id);
        Assert.Equal(1, f.Service.AutomaticallyDeletedSmallSessionCount);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task DisablingAutomaticDeletionDuringABatchStopsBeforeTheNextSession()
    {
        var rows = Enumerable.Range(0, 3).Select(_ => RetentionSession(1)).ToArray();
        await using var f = new RetentionFixture(threshold: 0, seed: (store, _) =>
        { foreach (var row in rows) store.Append(row); });
        await f.Ready(); Assert.Equal(3, f.Service.Snapshot.Sessions.Length);
        f.Service.SessionDeleted += _ => f.Settings.Current.LootTrackerSmallSessionItemThreshold = 0;
        f.Settings.Current.LootTrackerSmallSessionItemThreshold = 20; f.Source.Notify(); await f.Ready();
        Assert.Equal(1, f.Service.AutomaticallyDeletedSmallSessionCount);
        Assert.Equal(2, f.Service.Snapshot.Sessions.Length);
        f.Source.Notify(); await f.Ready();
        Assert.Equal(2, f.Service.Snapshot.Sessions.Length); Assert.False(f.Service.StatusIsError);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task AQueuedLootReceiptIsCommittedBeforeHistoryCleanupContinues()
    {
        var rows = Enumerable.Range(0, 3).Select(_ => RetentionSession(1)).ToArray();
        await using var f = new RetentionFixture(threshold: 0, seed: (store, _) =>
        { foreach (var row in rows) store.Append(row); });
        await f.Ready();
        var order = new List<string>();
        f.Service.SessionDeleted += _ =>
        {
            order.Add("deleted");
            if (order.Count == 1) f.Source.Drop(7, 20, 1);
        };
        f.Service.DropAccepted += _ => order.Add("booked");
        f.Settings.Current.LootTrackerSmallSessionItemThreshold = 20; f.Source.Notify();
        await f.Ready();
        await f.Ready(); // Includes the receipt enqueued by the first deletion callback.
        Assert.Equal(new[] { "deleted", "booked", "deleted", "deleted" }, order);
        Assert.Equal(3, f.Service.AutomaticallyDeletedSmallSessionCount);
        var active = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Live, active.Status); Assert.Equal(20, active.ItemCount);
        Assert.False(f.Service.StatusIsError);
        Assert.Equal(active.Id, Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions).Id);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task AFailedSettingsTransactionCannotExposeItsCandidateThresholdToRetention()
    {
        var row = RetentionSession(19) with { UnifiedVersion = 0, TimelineVersion = 0,
            Kind = LootTrackerSessionKind.Grind, TimelineEvents = [] };
        var deleted = new List<string>();
        await using var f = new RetentionFixture(threshold: 0, legacy: [row], deleteLegacy: session =>
        { deleted.Add(session.Id); return false; });
        await f.Ready();
        var path = AppPaths.SettingsFile;
        var previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
        File.Delete(path); Directory.CreateDirectory(path);
        using var candidate = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Instrument only the real settings transaction's write callback to hold its pre-save
        // window open. Its actual JsonFile.Save fails, and its existing rollback restores zero.
        var transaction = typeof(SettingsService).GetMethod("SetDropOption",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.MakeGenericMethod(typeof(long));
        Func<long> read = () => f.Settings.Current.LootTrackerSmallSessionItemThreshold;
        Action<long> write = value =>
        {
            f.Settings.Current.LootTrackerSmallSessionItemThreshold = value;
            if (value == 20) { candidate.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
        };
        var writer = Task.Run(() => Record.Exception(() => transaction.Invoke(f.Settings, [read, write, 20L])));
        Task? drain = null;
        Exception? saveError = null;
        try
        {
            Assert.True(candidate.Wait(TimeSpan.FromSeconds(5)));
            f.Source.Notify(); drain = f.Ready();
            await Task.Delay(100);
            Assert.False(drain.IsCompleted); Assert.Empty(deleted);
        }
        finally
        {
            release.Set();
            try
            {
                saveError = await writer;
                if (drain is not null) await drain;
            }
            finally
            {
                Directory.Delete(path);
                if (previous is not null) File.WriteAllBytes(path, previous);
            }
        }
        Assert.True(saveError?.GetBaseException() is IOException or UnauthorizedAccessException);
        Assert.Equal(0, f.Settings.GetLootTrackerSmallSessionItemThreshold());
        Assert.Equal(row.Id, Assert.Single(f.Service.Snapshot.Sessions).Id);
        Assert.Empty(deleted); Assert.Equal(0, f.Service.AutomaticallyDeletedSmallSessionCount);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task UnknownItemCountsAndCurrencyOrSoulAmountsDoNotBecomeGuessedItemQuantities()
    {
        var unknown = RetentionSession(null);
        var currencyOnly = RetentionSession(0) with { UnifiedVersion = 0, TimelineVersion = 0,
            Kind = LootTrackerSessionKind.Grind, TimelineEvents = [], SoulCount = 10_000, KinaUnbound = 1_000_000 };
        var deleted = new List<string>();
        await using var f = new RetentionFixture(seed: (store, _) => store.Append(unknown), legacy: [currencyOnly],
            deleteLegacy: session => { deleted.Add(session.Id); return false; });
        await f.Ready();
        Assert.Equal(unknown.Id, Assert.Single(f.Service.Snapshot.Sessions).Id);
        Assert.Equal(new[] { currencyOnly.Id }, deleted);
        Assert.Equal(1, f.Service.AutomaticallyDeletedSmallSessionCount);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task AReadIssueProtectsInitialHistoryWhileFreshKnownClosedSessionsCanStillBeRemoved()
    {
        var healthy = RetentionSession(19); var partial = RetentionSession(1);
        await using var f = new RetentionFixture(seed: (store, root) =>
        {
            store.Append(healthy); store.Append(partial);
            File.AppendAllText(Path.Combine(root, partial.Id, "events-0001.jsonl"), "{ incomplete");
        });
        await f.Ready();
        Assert.Equal(2, f.Service.Snapshot.Sessions.Length);
        Assert.True(f.Service.SmallSessionCleanupIncomplete); Assert.True(f.Service.StatusIsError);
        f.Source.Drop(7, 1, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromMinutes(2)); await f.Ready();
        Assert.Equal(2, f.Service.Snapshot.Sessions.Length);
        Assert.Equal(new[] { healthy.Id, partial.Id }.Order(), f.Service.Snapshot.Sessions.Select(row => row.Id).Order());
        Assert.Equal(1, f.Service.AutomaticallyDeletedSmallSessionCount);
        Assert.True(File.Exists(Path.Combine(f.SessionRoot, partial.Id, "events-0001.jsonl")));
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task AReadOnlyLegacyHistoryIsRetainedWithAVisibleProblemRatherThanKillingTheWriter()
    {
        var legacy = RetentionSession(1) with { UnifiedVersion = 0, TimelineVersion = 0,
            Kind = LootTrackerSessionKind.Lifeskill, TimelineEvents = [] };
        await using var f = new RetentionFixture(legacy: [legacy]); await f.Ready();
        Assert.True(f.Service.SmallSessionCleanupIncomplete); Assert.True(f.Service.StatusIsError);
        Assert.Contains(f.Service.Snapshot.Sessions, row => row.Id == legacy.Id);
        f.Source.Drop(7, 20, 1); await f.Ready();
        Assert.Equal(20, Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live).ItemCount);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task AFailedDeleteDoesNotStopOtherCleanupOrRecordingAndRetriesOnlyOnPolicyChange()
    {
        var failed = RetentionSession(19); var deletable = RetentionSession(1); var moves = 0; var fail = true;
        await using var f = new RetentionFixture(storeFactory: root => new(root, RetentionAppend,
            (path, value) => JsonFile.Save(path, value), (source, target) =>
            {
                if (Path.GetFileName(source) == failed.Id)
                { moves++; if (fail) throw new IOException("Synthetic retention move failure."); }
                Directory.Move(source, target);
            }), seed: (store, _) => { store.Append(failed); store.Append(deletable); });
        await f.Ready();
        Assert.Equal(failed.Id, Assert.Single(f.Service.Snapshot.Sessions).Id);
        Assert.Equal(1, moves); Assert.Equal(1, f.Service.AutomaticallyDeletedSmallSessionCount);
        f.Source.Notify(); f.Clock.Advance(TimeSpan.FromSeconds(3)); await f.Ready(); Assert.Equal(1, moves);
        f.Source.Drop(7, 20, 1); await f.Ready();
        Assert.Equal(20, Assert.Single(f.Service.Snapshot.Sessions, row => row.Status == LootTrackerSessionStatus.Live).ItemCount);
        fail = false; f.Settings.Current.LootTrackerSmallSessionItemThreshold = 21; f.Source.Notify(); await f.Ready();
        Assert.Equal(2, moves); Assert.DoesNotContain(f.Service.Snapshot.Sessions, row => row.Id == failed.Id);
        Assert.Equal(2, f.Service.AutomaticallyDeletedSmallSessionCount);
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task CleanupFailureAfterTombstoneNeverRestoresADeletedSession()
    {
        var row = RetentionSession(1);
        await using var f = new RetentionFixture(storeFactory: root => new(root, RetentionAppend,
            (path, value) => JsonFile.Save(path, value), delete: _ => throw new IOException("Synthetic cleanup failure.")),
            seed: (store, _) => store.Append(row));
        await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions); Assert.Equal(1, f.Service.AutomaticallyDeletedSmallSessionCount);
        Assert.False(Directory.Exists(Path.Combine(f.SessionRoot, row.Id)));
        Assert.Contains(Directory.EnumerateDirectories(f.SessionRoot), path => Path.GetFileName(path).StartsWith(".deleted-", StringComparison.Ordinal));
        var recovered = new UnifiedLootSessionStore(f.SessionRoot).Read();
        Assert.Empty(recovered.Sessions);
        Assert.Throws<InvalidOperationException>(() => f.Store.Append(row));
    }

    [Fact]
    [Trait("Category", "AutomaticRetention")]
    public async Task AutoDeletionStopsReplayWriterAndDeletesSessionSidecarsBeforeNotifying()
    {
        var row = RetentionSession(1);
        await using var f = new RetentionFixture(replaySession: row, seed: (store, root) =>
        { store.Append(row); File.WriteAllText(Path.Combine(root, row.Id, "synthetic-sidecar.json"), "{}"); });
        await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions);
        Assert.False(Directory.Exists(Path.Combine(f.SessionRoot, row.Id)));
        f.PositionRecorder!.Observe([new("synthetic-map", 10, 20, RetentionAt.AddSeconds(1),
            TimeSpan.FromSeconds(1).Ticks, 1, 1, 1, 1, 1, ReplayPositionProvenance.Matched, 1)]);
        Assert.False(Directory.Exists(Path.Combine(f.SessionRoot, row.Id)));
    }

    private static LootTrackerSession RetentionSession(long? quantity, bool abyss = false)
    {
        var rewards = quantity is > 0
            ? ImmutableArray.Create(new LootTrackerLoot("Item:7", "Synthetic resource", LootTrackerLootKind.Item, quantity.Value))
            : ImmutableArray<LootTrackerLoot>.Empty;
        return new()
        {
            Id = Guid.NewGuid().ToString("N"), UnifiedVersion = 1, TimelineVersion = 1,
            Kind = abyss ? LootTrackerSessionKind.Abyss : LootTrackerSessionKind.Mixed,
            Status = LootTrackerSessionStatus.Completed, StartedAt = RetentionAt, LastLootAt = RetentionAt.AddSeconds(1),
            LastPresenceAt = abyss ? RetentionAt.AddSeconds(1) : null, EndedAt = RetentionAt.AddSeconds(1),
            Elapsed = TimeSpan.FromSeconds(1), Character = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Synthetic character"),
            ItemCount = quantity, SoulCount = 0, Loot = rewards,
            TimelineEvents = rewards.IsEmpty ? [] : [new(Guid.NewGuid().ToString("N"), LootTrackerTimelineEventKind.Drop,
                RetentionAt.AddSeconds(1), TimeSpan.FromSeconds(1))
                { Origin = abyss ? LootTrackerActivityOrigin.Abyss : LootTrackerActivityOrigin.Pve, Loot = rewards }]
        };
    }

    private static void RetentionAppend(string path, string line) => File.AppendAllText(path, line, new UTF8Encoding(false));

    private sealed class RetentionFixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-retention-" + Guid.NewGuid().ToString("N"));
        internal string SessionRoot => Path.Combine(Root, "sessions");
        internal SettingsService Settings { get; } = new();
        internal ManualTime Clock { get; } = new(null);
        internal FakeSource Source { get; }
        internal UnifiedLootSessionStore Store { get; }
        internal UnifiedLootTrackerService Service { get; }
        internal ReplayPositionRecorder? PositionRecorder { get; }

        internal RetentionFixture(long threshold = 20, bool abyss = false,
            IEnumerable<LootTrackerSession>? legacy = null, Func<LootTrackerSession, bool>? deleteLegacy = null,
            Action<UnifiedLootSessionStore, string>? seed = null, Func<string, UnifiedLootSessionStore>? storeFactory = null,
            LootTrackerSession? replaySession = null, Action<string>? seedLegacy = null)
        {
            Directory.CreateDirectory(Root); Source = new(Clock) { InInstance = abyss };
            Settings.Current.LootTrackingEnabled = Settings.Current.MonsterLootTrackingEnabled = true;
            Settings.Current.AbyssTrackingEnabled = Settings.Current.LifeskillTrackingEnabled = true;
            Settings.Current.LootTrackerSmallSessionItemThreshold = threshold;
            Store = storeFactory?.Invoke(SessionRoot) ?? new(SessionRoot);
            seed?.Invoke(Store, SessionRoot);
            if (replaySession is not null)
            {
                PositionRecorder = new(null, root: SessionRoot, time: Clock);
                PositionRecorder.Start(replaySession.Id, Clock.GetTimestamp(), 1, 1);
            }
            Func<UnifiedLootLegacyRead>? readLegacy = null;
            if (seedLegacy is not null)
            {
                var legacyRoot = Path.Combine(Root, "legacy");
                seedLegacy(legacyRoot);
                var originalStores = new UnifiedLootLegacyHistory(legacyRoot);
                readLegacy = originalStores.ReadSessions;
                deleteLegacy = originalStores.Delete;
            }
            Service = new(Settings, Source, Source, Source, Source,
                _ => new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Synthetic character"),
                () => new(WorldContextStatus.Confirmed, abyss ? WorldContextKind.Instance : WorldContextKind.OpenWorld,
                    1, null, abyss ? "Abyss" : null, null, "Synthetic map", "Synthetic map", 1, Clock.GetUtcNow(), "synthetic")
                    { BindingGeneration = Source.OwnCharacter?.Generation },
                Store, Clock, legacyHistory: legacy, deleteLegacy: deleteLegacy, readLegacyHistory: readLegacy,
                dailyStore: new(Path.Combine(Root, "daily.json")), replayPositionRecorder: PositionRecorder);
        }

        internal Task Ready() => Service.FlushAsync();
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (!Directory.Exists(Root)) return;
            var resolved = Path.GetFullPath(Root);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                || !Path.GetFileName(resolved).StartsWith("soulcrest-retention-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected synthetic fixture directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
