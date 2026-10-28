using System.Text.Json.Nodes;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerReplayServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    private static LootTrackerSession Session => new()
    {
        Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", Kind = LootTrackerSessionKind.Abyss, UnifiedVersion = 1,
        Status = LootTrackerSessionStatus.Completed, StartedAt = At, EndedAt = At.AddSeconds(20), Elapsed = TimeSpan.FromSeconds(20),
        RouteSegments = [new("a", "A", [new(100, 1000, At, TimeSpan.Zero), new(200, 1000, At.AddSeconds(10), TimeSpan.FromSeconds(10))])]
    };
    private static readonly ReplayPositionReadResult Empty = new([], [], "", false, false);
    private static LootTrackerReplayService Service(string root,
        Func<LootTrackerSession, ReplayPositionReadResult>? read = null,
        Func<string, CancellationToken, Task>? flush = null,
        Func<LootTrackerSession, ReplayPositionReadResult, CancellationToken, Action<int>, LootTrackerPreparedReplay>? prepare = null)
        => new(root, read ?? (_ => Empty), flush ?? ((_, _) => Task.CompletedTask),
            prepare ?? ((session, positions, token, progress) => LootTrackerPreparedReplay.Prepare(session, positions, token, progress)));

    [Fact]
    public async Task RecordingAndStateReadsNeverReadSourcesBuildOrWriteCache()
    {
        using var directory = new TempDirectory(); var calls = 0;
        using var service = Service(directory.Path, _ => { calls++; return Empty; }, (_, _) => { calls++; return Task.CompletedTask; });
        var session = Session with { Status = LootTrackerSessionStatus.Live, EndedAt = null };
        Assert.Equal(LootTrackerReplayStatus.Recording, service.GetState(session).Status);
        Assert.Equal(LootTrackerReplayStatus.Recording, (await service.LoadAsync(session)).Status);
        Assert.Equal(LootTrackerReplayStatus.Recording, (await service.PrepareAsync(session)).Status);
        Assert.Equal(0, calls); Assert.Empty(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
        Assert.Equal(LootTrackerReplayStatus.NotPrepared, service.GetState(Session).Status); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConcurrentPreparationCoalescesAndPersistedCacheReopensAfterFlush()
    {
        using var directory = new TempDirectory(); var session = Session; var builds = 0; var flushed = false;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using (var service = Service(directory.Path, _ => { Assert.True(flushed); return Empty; }, (_, _) => { flushed = true; return Task.CompletedTask; },
            (source, positions, token, progress) => { Interlocked.Increment(ref builds); entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return LootTrackerPreparedReplay.Prepare(source, positions, token, progress); }))
        {
            var first = service.PrepareAsync(session); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var second = service.PrepareAsync(session); release.Set();
            var states = await Task.WhenAll(first, second); Assert.Equal(1, builds);
            Assert.All(states, state => Assert.Equal(LootTrackerReplayStatus.Ready, state.Status)); Assert.Same(states[0].Replay, states[1].Replay);
            for (var index = 0; index < 100; index++) Assert.Same(states[0], service.GetState(session));
        }
        using var reopened = Service(directory.Path);
        var loaded = await reopened.LoadAsync(session);
        Assert.Equal(LootTrackerReplayStatus.Ready, loaded.Status); Assert.NotNull(loaded.Replay);
        Assert.Single(Directory.EnumerateFiles(directory.Path, $"replay-v{LootTrackerPreparedReplay.CurrentVersion}.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PrepareRequestedDuringLoadWaitsThenStartsPreparation()
    {
        using var directory = new TempDirectory(); var session = Session;
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Service(directory.Path, flush: async (_, token) => { flushStarted.TrySetResult(); await releaseFlush.Task.WaitAsync(token); });
        var loading = service.LoadAsync(session); await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var preparing = service.PrepareAsync(session); releaseFlush.SetResult();
        Assert.Equal(LootTrackerReplayStatus.NotPrepared, (await loading).Status);
        Assert.Equal(LootTrackerReplayStatus.Ready, (await preparing).Status);
    }

    [Fact]
    public async Task ValidJsonGeometryCorruptionIsStaleAndCanBePreparedAgain()
    {
        using var directory = new TempDirectory(); var session = Session;
        using (var service = Service(directory.Path)) Assert.Equal(LootTrackerReplayStatus.Ready, (await service.PrepareAsync(session)).Status);
        var cache = Assert.Single(Directory.EnumerateFiles(directory.Path, $"replay-v{LootTrackerPreparedReplay.CurrentVersion}.json", SearchOption.AllDirectories));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(cache))!;
        json["Routes"]![0]!["Points"]![0]!["X"] = 999;
        await File.WriteAllTextAsync(cache, json.ToJsonString());
        using var reopened = Service(directory.Path);
        var invalid = await reopened.LoadAsync(session);
        Assert.Equal(LootTrackerReplayStatus.Stale, invalid.Status); Assert.Equal(LootTrackerReplayFailure.InvalidCache, invalid.Reason);
        Assert.Equal(LootTrackerReplayStatus.Ready, (await reopened.PrepareAsync(session)).Status);
    }

    [Fact]
    public async Task NewImmutableSnapshotChecksFactsOffThreadAndSidecarChangesInvalidateTheCache()
    {
        using var directory = new TempDirectory(); var session = Session; var fingerprint = "first";
        using var service = Service(directory.Path, _ => Empty with { SourceFingerprint = fingerprint });
        Assert.Equal(LootTrackerReplayStatus.Ready, (await service.PrepareAsync(session)).Status);
        var renamed = session with { Location = "Localized location", Character = new(null, "Example") };
        Assert.Equal(LootTrackerReplayStatus.Stale, service.GetState(renamed).Status);
        Assert.Equal(LootTrackerReplayStatus.Ready, (await service.LoadAsync(renamed)).Status);
        fingerprint = "changed";
        // A load is an explicit source validation even if a reference was already ready.
        Assert.Equal(LootTrackerReplayStatus.Stale, (await service.LoadAsync(renamed)).Status);
    }

    [Fact]
    public async Task ForceFailureKeepsThePriorValidatedArtifactAndBytes()
    {
        using var directory = new TempDirectory(); var session = Session; var fail = false;
        using var service = Service(directory.Path, prepare: (source, positions, token, progress) => fail
            ? throw new IOException("private path must never be published") : LootTrackerPreparedReplay.Prepare(source, positions, token, progress));
        var original = await service.PrepareAsync(session);
        var path = Assert.Single(Directory.EnumerateFiles(directory.Path, $"replay-v{LootTrackerPreparedReplay.CurrentVersion}.json", SearchOption.AllDirectories));
        var bytes = await File.ReadAllBytesAsync(path); fail = true;
        var failed = await service.PrepareAsync(session, true);
        Assert.Equal(LootTrackerReplayStatus.Ready, failed.Status); Assert.Equal(LootTrackerReplayFailure.Storage, failed.Reason);
        Assert.Same(original.Replay, failed.Replay); Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CancelingAForcedRebuildKeepsThePriorArtifactAndAllowsAnotherRetry()
    {
        using var directory = new TempDirectory(); var session = Session; var blocking = false;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var service = Service(directory.Path, prepare: (source, positions, token, progress) =>
        {
            if (blocking) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
            return LootTrackerPreparedReplay.Prepare(source, positions, token, progress);
        });
        var original = await service.PrepareAsync(session); blocking = true;
        var job = service.PrepareAsync(session, true); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        service.Cancel(session.Id, session.Kind); release.Set();
        var canceled = await job;
        Assert.Equal(LootTrackerReplayStatus.Ready, canceled.Status); Assert.Same(original.Replay, canceled.Replay);
        blocking = false;
        Assert.Equal(LootTrackerReplayStatus.Ready, (await service.PrepareAsync(session, true)).Status);
    }

    [Fact]
    public async Task CancelNeverRestoresAFallbackAlreadyInvalidatedByNewSourceFacts()
    {
        using var directory = new TempDirectory(); var session = Session; var blocking = false; var digest = "first";
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var service = Service(directory.Path, _ => Empty with { SourceFingerprint = digest }, prepare: (source, positions, token, progress) =>
        {
            if (blocking) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
            return LootTrackerPreparedReplay.Prepare(source, positions, token, progress);
        });
        Assert.Equal(LootTrackerReplayStatus.Ready, (await service.PrepareAsync(session)).Status);
        digest = "different-sidecar"; blocking = true;
        var job = service.PrepareAsync(session, true); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        service.Cancel(session.Id, session.Kind); release.Set();
        var canceled = await job;
        Assert.Equal(LootTrackerReplayStatus.Canceled, canceled.Status); Assert.Null(canceled.Replay);
        Assert.Equal(LootTrackerReplayStatus.Canceled, service.GetState(session).Status);
    }

    [Fact]
    public async Task CancelPreventsANonCooperativeWorkerFromPublishingOrWritingLater()
    {
        using var directory = new TempDirectory(); var session = Session;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var service = Service(directory.Path, prepare: (source, positions, _, progress) =>
        { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return LootTrackerPreparedReplay.Prepare(source, positions); });
        var job = service.PrepareAsync(session); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        service.Cancel(session.Id, session.Kind); release.Set();
        Assert.Equal(LootTrackerReplayStatus.Canceled, (await job).Status);
        Assert.Equal(LootTrackerReplayStatus.Canceled, service.GetState(session).Status);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DeletionDuringPreparationWaitsForTheWorkerAndPreventsLatePublication()
    {
        using var directory = new TempDirectory(); var session = Session;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var service = Service(directory.Path, prepare: (source, positions, _, progress) =>
        { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return LootTrackerPreparedReplay.Prepare(source, positions); });
        var job = service.PrepareAsync(session); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var deleting = service.RemoveAsync(session.Id, session.Kind); release.Set(); await Task.WhenAll(job, deleting);
        Assert.Equal(LootTrackerReplayStatus.NotPrepared, service.GetState(session).Status);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
        Assert.Equal(LootTrackerReplayStatus.Failed, (await service.PrepareAsync(session)).Status);
    }

    [Fact]
    public async Task OriginNamespacesAreIndependentAndLegacyNeverReadsOrFlushesSidecars()
    {
        using var directory = new TempDirectory(); var session = Session; var legacy = session with { UnifiedVersion = 0 };
        var reads = 0; var flushes = 0;
        using var service = Service(directory.Path, _ => { reads++; return Empty; }, (_, _) => { flushes++; return Task.CompletedTask; });
        Assert.Equal(LootTrackerReplayStatus.Ready, (await service.PrepareAsync(legacy)).Status);
        Assert.Equal(0, reads); Assert.Equal(0, flushes);
        Assert.Equal(LootTrackerReplayStatus.Ready, (await service.PrepareAsync(session)).Status);
        Assert.Equal(1, reads); Assert.Equal(1, flushes);
        var paths = Directory.EnumerateFiles(directory.Path, $"replay-v{LootTrackerPreparedReplay.CurrentVersion}.json", SearchOption.AllDirectories).ToArray();
        Assert.Equal(2, paths.Length); Assert.Contains(paths, path => path.Contains(Path.DirectorySeparatorChar + "unified" + Path.DirectorySeparatorChar));
        Assert.Contains(paths, path => path.Contains(Path.DirectorySeparatorChar + "farm" + Path.DirectorySeparatorChar));
        await service.RemoveAsync(session.Id, session.Kind); Assert.Empty(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DisposePreventsLateWritesAndNotifications()
    {
        using var directory = new TempDirectory(); var session = Session;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var service = Service(directory.Path, prepare: (source, positions, _, progress) =>
        { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return LootTrackerPreparedReplay.Prepare(source, positions); });
        var changes = 0; service.Changed += () => Interlocked.Increment(ref changes);
        var job = service.PrepareAsync(session); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        service.Dispose(); var before = changes; release.Set(); Assert.Equal(LootTrackerReplayStatus.Canceled, (await job).Status);
        Assert.Equal(before, changes); Assert.Empty(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-prepared-cache-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
