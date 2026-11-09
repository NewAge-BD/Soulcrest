using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ReplayPositionPauseTests
{
    [Fact]
    public async Task ProductiveClockDiscardsIdlePositionsAndResumesWithMonotonicCompressedElapsed()
    {
        await using var f = new Fixture();
        f.Recorder.Start(f.Id, 0, 1, 1, productiveClock: true);
        f.Observe(1, 1); f.Observe(2, 2); f.Recorder.Commit(f.Clock.GetTimestamp(), TimeSpan.FromSeconds(2));
        f.Observe(3, 5); Assert.Equal(1, f.Recorder.PendingSampleCount);
        f.Clock.Seconds = 22; f.Recorder.Pause(); Assert.Equal(0, f.Recorder.PendingSampleCount);
        f.Observe(4, 23); Assert.Equal(0, f.Recorder.PendingSampleCount);
        f.Clock.Seconds = 32; f.Recorder.Resume(f.Clock.GetTimestamp(), TimeSpan.FromSeconds(30));
        f.Observe(5, 31); // A delayed old batch cannot enter the resumed life of the clock.
        f.Observe(8, 32); // The exact old elapsed anchor belongs only to the pre-pause point.
        f.Observe(6, 33); f.Recorder.Commit(f.Clock.GetTimestamp(), TimeSpan.FromSeconds(3));
        f.Observe(7, 40); await f.Recorder.SealAsync(TimeSpan.FromSeconds(3));
        var read = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(3));
        Assert.False(read.Truncated); Assert.Equal(new long[] { 1, 2, 6 }, read.Samples.Select(row => row.FrameId));
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) }, read.Samples.Select(row => row.Elapsed));
        Assert.Equal(Fixture.At.AddSeconds(33), read.Samples[^1].OriginalAt);
        Assert.Single(read.Samples, row => row.Elapsed == TimeSpan.FromSeconds(2));
        Assert.True(read.Samples[^1].Elapsed > TimeSpan.FromSeconds(2));
        Assert.Contains(read.Gaps, row => row.Reason == ReplayPositionGapReason.SessionPaused && row.Elapsed == TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ProvisionalPositionMemoryIsBoundedAndAnOverflowIsExplicit()
    {
        await using var f = new Fixture(); f.Recorder.Start(f.Id, 0, 1, 1, productiveClock: true);
        f.Recorder.Observe(Enumerable.Range(1, ReplayPositionRecorder.MaximumPendingSamples + 100)
            .Select(frame => new ReplayMapPositionSample("sample-map", frame, 2, Fixture.At.AddTicks(frame), frame,
                frame, 1, 1, 1, 1, ReplayPositionProvenance.Flow, 1)).ToArray());
        Assert.InRange(f.Recorder.PendingSampleCount, 0, ReplayPositionRecorder.MaximumPendingSamples);
        f.Recorder.Commit(ReplayPositionRecorder.MaximumPendingSamples + 100, TimeSpan.FromTicks(ReplayPositionRecorder.MaximumPendingSamples + 100));
        await f.Recorder.SealAsync(TimeSpan.FromSeconds(1));
        var read = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(1));
        Assert.False(read.Truncated); Assert.True(read.Samples.Length < ReplayPositionRecorder.MaximumPendingSamples);
        Assert.Contains(read.Gaps, row => row.Reason == ReplayPositionGapReason.QueueOverflow);
    }

    [Fact]
    public async Task OverflowSplitsTheRetainedTailFromAnAlreadyCommittedAnchorBeforeAppendingIt()
    {
        await using var f = new Fixture(); f.Recorder.Start(f.Id, 0, 1, 1, productiveClock: true);
        f.Recorder.Observe([new("sample-map", 1, 2, Fixture.At.AddTicks(1), 1, 1,
            1, 1, 1, 1, ReplayPositionProvenance.Matched, 1)]);
        f.Recorder.Commit(1, TimeSpan.FromTicks(1));
        var finalStamp = ReplayPositionRecorder.MaximumPendingSamples + 101;
        f.Recorder.Observe(Enumerable.Range(2, finalStamp - 1)
            .Select(frame => new ReplayMapPositionSample("sample-map", frame, 2, Fixture.At.AddTicks(frame), frame,
                frame, 1, 1, 1, 1, ReplayPositionProvenance.Matched, 1)).ToArray());
        f.Recorder.Commit(finalStamp, TimeSpan.FromTicks(finalStamp));
        await f.Recorder.SealAsync(TimeSpan.FromTicks(finalStamp));
        var read = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromTicks(finalStamp));
        Assert.False(read.Truncated); Assert.True(read.Samples.Length > 2);
        Assert.Equal(1, read.Samples[0].FrameId);
        Assert.NotEqual(read.Samples[0].SegmentId, read.Samples[1].SegmentId);
        Assert.Equal(read.Samples[1].Elapsed, Assert.Single(read.Gaps, row => row.Reason == ReplayPositionGapReason.QueueOverflow).Elapsed);
        var prepared = LootTrackerPreparedReplay.Prepare(new LootTrackerSession
        {
            Id = "synthetic-overflow", Kind = LootTrackerSessionKind.Mixed, Status = LootTrackerSessionStatus.Completed,
            StartedAt = Fixture.At, EndedAt = Fixture.At.AddTicks(finalStamp), Elapsed = TimeSpan.FromTicks(finalStamp)
        }, read);
        Assert.Equal(2, prepared.Routes.Length);
        Assert.Single(prepared.Routes[0].Points); Assert.True(prepared.Routes[1].Points.Length > 1);
    }
    [Fact]
    public async Task TheProvisionalWindowCannotCaptureAnUnboundedIdleTailEvenIfTheLootWorkerIsDelayed()
    {
        await using var f = new Fixture(); f.Recorder.Start(f.Id, 0, 1, 1, productiveClock: true);
        f.Observe(1, 10); f.Observe(2, 25);
        Assert.Equal(1, f.Recorder.PendingSampleCount);
        f.Recorder.Commit(TimeSpan.FromSeconds(10).Ticks, TimeSpan.FromSeconds(10));
        f.Observe(3, 26); f.Recorder.Commit(f.Clock.GetTimestamp(), TimeSpan.FromSeconds(26));
        await f.Recorder.SealAsync(TimeSpan.FromSeconds(26));
        var read = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(26));
        Assert.False(read.Truncated); Assert.Equal(new long[] { 1, 3 }, read.Samples.Select(row => row.FrameId));
        Assert.Contains(read.Gaps, row => row.Reason == ReplayPositionGapReason.QueueOverflow);
        Assert.NotEqual(read.Samples[0].SegmentId, read.Samples[1].SegmentId);
    }

    [Fact]
    public async Task DefaultAbyssRecorderRetainsTheContinuousClockAndIgnoresOutsidePauseControls()
    {
        await using var f = new Fixture(); f.Recorder.Start(f.Id, 0, 1, 1);
        f.Observe(1, 1); f.Observe(2, 25); f.Recorder.Pause(); f.Recorder.Resume(f.Clock.GetTimestamp(), TimeSpan.FromSeconds(20));
        f.Observe(3, 26); await f.Recorder.SealAsync(TimeSpan.FromSeconds(26));
        var read = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(26));
        Assert.False(read.Truncated); Assert.Equal(new long[] { 1, 2, 3 }, read.Samples.Select(row => row.FrameId));
        Assert.Equal(TimeSpan.FromSeconds(26), read.Samples[^1].Elapsed);
        Assert.DoesNotContain(read.Gaps, row => row.Reason == ReplayPositionGapReason.SessionPaused);
    }

    [Fact]
    public async Task AContextChangeDropsUncommittedMovementWithoutBorrowingAnOldBinding()
    {
        await using var f = new Fixture(); f.Recorder.Start(f.Id, 0, 1, 1, productiveClock: true);
        f.Observe(1, 1); f.Recorder.Commit(f.Clock.GetTimestamp(), TimeSpan.FromSeconds(1)); f.Observe(2, 2);
        f.Recorder.UpdateContext(2, 3); Assert.Equal(0, f.Recorder.PendingSampleCount);
        f.Observe(3, 3); // Old binding.
        f.Recorder.Observe([new("sample-map", 4, 2, Fixture.At.AddSeconds(4), TimeSpan.FromSeconds(4).Ticks,
            4, 1, 1, 3, 2, ReplayPositionProvenance.Matched, 1)]);
        f.Recorder.Commit(TimeSpan.FromSeconds(4).Ticks, TimeSpan.FromSeconds(4));
        await f.Recorder.SealAsync(TimeSpan.FromSeconds(4));
        var read = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(4));
        Assert.Equal(new long[] { 1, 4 }, read.Samples.Select(row => row.FrameId));
        Assert.Contains(read.Gaps, row => row.Reason == ReplayPositionGapReason.SourceInvalidated);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-productive-positions-" + Guid.NewGuid().ToString("N"));
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal Clock Clock { get; } = new();
        internal ReplayPositionRecorder Recorder { get; }
        internal Fixture() => Recorder = new(null, root: Root, time: Clock);
        internal void Observe(int frame, int seconds)
        {
            Clock.Seconds = seconds;
            Recorder.Observe([new("sample-map", frame, 2, At.AddSeconds(seconds), Clock.GetTimestamp(), frame,
                1, 1, 1, 1, ReplayPositionProvenance.Matched, 1)]);
        }
        public async ValueTask DisposeAsync()
        { await Recorder.DisposeAsync(); ReplayPositionStore.Forget(Root, Id); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Clock : TimeProvider
    {
        internal int Seconds;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => TimeSpan.FromSeconds(Seconds).Ticks;
        public override DateTimeOffset GetUtcNow() => Fixture.At.AddSeconds(Seconds);
    }
}
