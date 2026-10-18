using System.Collections.Concurrent;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatHistoryWriterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly CombatEntityReference Boss = new("synthetic-boss-entity", 1, "synthetic-stream");
    private static readonly CombatSourceContext Source = new("synthetic-stream", 1);

    [Fact]
    public async Task CoalescesOnlyPendingFullCheckpointsAndOwnsTheirMutableArrays()
    {
        var a = Run();
        var b = Run(Envelope(1, 10));
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<CombatRunDocument>();
        var writer = new CombatHistoryWriter(run =>
        {
            if (run.Id == a.Id) { entered.TrySetResult(); Wait(release); }
            writes.Enqueue(run);
        });
        try
        {
            Assert.True(writer.TryEnqueue(a));
            await entered.Task.WaitAsync(Timeout);
            Assert.True(writer.TryEnqueue(b));
            var latest = Append(b, Envelope(2, 20));
            Assert.True(writer.TryEnqueue(latest));
            latest.Attempts[0].Events[1] = Envelope(2, 999);
            latest.Attempts[0].Participants[0] = new("different", "Changed after enqueue");
            release.Set();
            await writer.FlushAsync().WaitAsync(Timeout);
            Assert.Equal(2, writes.Count);
            var saved = Assert.Single(writes, run => run.Id == b.Id);
            Assert.Equal([10L, 20L], saved.Attempts[0].Events.Select(e => ((CombatDamageEvent)e.Event).Amount));
            Assert.Equal("synthetic-member", saved.Attempts[0].Participants[0].ParticipantId);
        }
        finally { release.Set(); await Stop(writer); }
    }

    [Fact]
    public async Task FlushTracksEveryOlderRunAcrossCoalescingAndDoesNotWaitForLaterRuns()
    {
        var a = Run(); var b = Run(); var c = Run(); var d = Run();
        var aEntered = Signal(); var cEntered = Signal(); var dEntered = Signal();
        using var aRelease = new ManualResetEventSlim();
        using var cRelease = new ManualResetEventSlim();
        using var dRelease = new ManualResetEventSlim();
        var writer = new CombatHistoryWriter(run =>
        {
            if (run.Id == a.Id) { aEntered.TrySetResult(); Wait(aRelease); }
            if (run.Id == c.Id) { cEntered.TrySetResult(); Wait(cRelease); }
            if (run.Id == d.Id) { dEntered.TrySetResult(); Wait(dRelease); }
        });
        try
        {
            Assert.True(writer.TryEnqueue(a));
            await aEntered.Task.WaitAsync(Timeout);
            Assert.True(writer.TryEnqueue(b));
            Assert.True(writer.TryEnqueue(c));
            var flush = writer.FlushAsync();
            Assert.True(writer.TryEnqueue(Append(b, Envelope(1, 1))));
            aRelease.Set();
            await cEntered.Task.WaitAsync(Timeout);
            Assert.False(flush.IsCompleted);
            Assert.True(writer.TryEnqueue(d));
            cRelease.Set();
            await dEntered.Task.WaitAsync(Timeout);
            await flush.WaitAsync(Timeout);
            Assert.False(writer.Completion.IsCompleted);
        }
        finally { aRelease.Set(); cRelease.Set(); dRelease.Set(); await Stop(writer); }
    }

    [Fact]
    public async Task PendingLimitFaultRetainsAcceptedDataAndNeverRetriesOrRestarts()
    {
        var a = Run(); var b = Run(Envelope(1, 2)); var rejected = Run();
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<Guid>();
        var writer = new CombatHistoryWriter(run =>
        {
            entered.TrySetResult(); Wait(release); writes.Enqueue(run.Id);
        }, maximumPendingRuns: 1);
        try
        {
            Assert.True(writer.TryEnqueue(a));
            await entered.Task.WaitAsync(Timeout);
            Assert.True(writer.TryEnqueue(b));
            Assert.False(writer.TryEnqueue(rejected));
            Assert.Equal("pending-checkpoint-limit", writer.Fault!.Code);
            await Assert.ThrowsAsync<CombatHistoryWriterException>(() => writer.FlushAsync());
            Assert.False(writer.TryEnqueue(b));
            Assert.Equal(2, writer.GetUnwrittenCheckpoints().Count);
            release.Set();
            await Assert.ThrowsAsync<CombatHistoryWriterException>(() => writer.Completion.WaitAsync(Timeout));
            Assert.Equal([a.Id], writes);
            var retained = Assert.Single(writer.GetUnwrittenCheckpoints());
            Assert.Equal(b.Id, retained.Id);
            retained.Attempts[0].Events[0] = Envelope(1, 999);
            Assert.Equal(2, ((CombatDamageEvent)writer.GetUnwrittenCheckpoints()[0].Attempts[0].Events[0].Event).Amount);
            Assert.Equal(1, writer.PendingRunCount);
        }
        finally { release.Set(); await Stop(writer); }
    }

    [Fact]
    public async Task IoFailureIsExplicitRetainsTheFailedCheckpointAndFaultHandlersCannotHideIt()
    {
        var writes = 0;
        var writer = new CombatHistoryWriter(_ => { Interlocked.Increment(ref writes); throw new IOException("Synthetic write failure"); });
        writer.Faulted += _ => throw new InvalidOperationException("Synthetic observer failure");
        try
        {
            var run = Run(Envelope(1, 1));
            Assert.True(writer.TryEnqueue(run));
            await Assert.ThrowsAsync<CombatHistoryWriterException>(() => writer.Completion.WaitAsync(Timeout));
            Assert.Equal("checkpoint-write-failed", writer.Fault!.Code);
            Assert.Equal(run.Id, Assert.Single(writer.GetUnwrittenCheckpoints()).Id);
            Assert.False(writer.TryEnqueue(Run()));
            await Assert.ThrowsAsync<CombatHistoryWriterException>(() => writer.FlushAsync());
            Assert.Equal(1, writes);
        }
        finally { await Stop(writer); }
    }

    [Fact]
    public async Task DisposeStopsAcceptingWithoutBlockingAndCompletionDrainsAcceptedWork()
    {
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var writes = 0;
        var writer = new CombatHistoryWriter(_ => { entered.TrySetResult(); Wait(release); Interlocked.Increment(ref writes); });
        try
        {
            Assert.True(writer.TryEnqueue(Run()));
            await entered.Task.WaitAsync(Timeout);
            Assert.True(writer.TryEnqueue(Run()));
            writer.Dispose();
            Assert.False(writer.TryEnqueue(Run()));
            Assert.False(writer.Completion.IsCompleted);
            release.Set();
            await writer.Completion.WaitAsync(Timeout);
            await writer.DisposeAsync();
            Assert.Equal(2, writes);
            Assert.Empty(writer.GetUnwrittenCheckpoints());
        }
        finally { release.Set(); await Stop(writer); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SealedAttemptCannotChangeOrGainAnyEventAfterAFlush(bool append)
    {
        var run = Finish(Run(Envelope(1, 1)));
        var writes = new ConcurrentQueue<CombatRunDocument>();
        var writer = new CombatHistoryWriter(writes.Enqueue);
        try
        {
            Assert.True(writer.TryEnqueue(run));
            await writer.FlushAsync().WaitAsync(Timeout);
            var changed = append ? Append(run, Envelope(2, 2)) :
                run with { Attempts = [run.Attempts[0] with { Events = [Envelope(1, 999)] }] };
            Assert.False(writer.TryEnqueue(changed));
            Assert.Equal("checkpoint-incompatible", writer.Fault!.Code);
            Assert.Single(writes);
            await Assert.ThrowsAsync<CombatHistoryWriterException>(() => writer.Completion.WaitAsync(Timeout));
        }
        finally { await Stop(writer); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveCheckpointCannotLoseOrRewriteItsAcceptedEventPrefix(bool remove)
    {
        var a = Run(); var run = Run(Envelope(1, 1));
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var writer = new CombatHistoryWriter(_ => { entered.TrySetResult(); Wait(release); });
        try
        {
            Assert.True(writer.TryEnqueue(a));
            await entered.Task.WaitAsync(Timeout);
            Assert.True(writer.TryEnqueue(run));
            var changed = run with { Attempts = [run.Attempts[0] with { Events = remove ? [] : [Envelope(1, 2)] }] };
            Assert.False(writer.TryEnqueue(changed));
            var retained = Assert.Single(writer.GetUnwrittenCheckpoints(), r => r.Id == run.Id);
            Assert.Equal(1, ((CombatDamageEvent)retained.Attempts[0].Events[0].Event).Amount);
        }
        finally { release.Set(); await Stop(writer); }
    }

    [Fact]
    public async Task ClosedRunCannotGainAnotherAttemptEvenAfterEarlierCheckpointsWereWritten()
    {
        var run = Finish(Run());
        var writer = new CombatHistoryWriter(_ => { });
        try
        {
            Assert.True(writer.TryEnqueue(run));
            await writer.FlushAsync().WaitAsync(Timeout);
            Assert.False(writer.TryEnqueue(run with { Attempts = [run.Attempts[0], Finish(Run()).Attempts[0]] }));
            Assert.Equal("checkpoint-incompatible", writer.Fault!.Code);
        }
        finally { await Stop(writer); }
    }

    [Fact]
    public async Task CheckpointByteLimitIsAnExplicitFaultAndPreservesPreviouslyAcceptedWork()
    {
        var a = Run();
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var writer = new CombatHistoryWriter(_ => { entered.TrySetResult(); Wait(release); },
            maximumPendingBytes: 8192, maximumCheckpointBytes: 4096);
        try
        {
            Assert.True(writer.TryEnqueue(a));
            await entered.Task.WaitAsync(Timeout);
            Assert.False(writer.TryEnqueue(Run() with { DungeonKey = new string('x', 8192) }));
            Assert.Equal("checkpoint-invalid-or-too-large", writer.Fault!.Code);
            Assert.Equal(a.Id, Assert.Single(writer.GetUnwrittenCheckpoints()).Id);
        }
        finally { release.Set(); await Stop(writer); }
    }

    [Fact]
    public async Task CancellingFlushDoesNotCancelAcceptedPersistence()
    {
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var writes = 0;
        var writer = new CombatHistoryWriter(_ => { entered.TrySetResult(); Wait(release); Interlocked.Increment(ref writes); });
        try
        {
            Assert.True(writer.TryEnqueue(Run()));
            await entered.Task.WaitAsync(Timeout);
            using var cancellation = new CancellationTokenSource();
            var flush = writer.FlushAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
            Assert.Null(writer.Fault);
            release.Set();
            await writer.FlushAsync().WaitAsync(Timeout);
            Assert.Equal(1, writes);
        }
        finally { release.Set(); await Stop(writer); }
    }

    [Fact]
    public async Task RealStoreReceivesTheFullAppendOnlyJournalBeforeTheFinalCheckpointCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Soulcrest.CombatWriter.Tests", Guid.NewGuid().ToString("N"));
        var store = new CombatHistoryStore(directory);
        var writer = new CombatHistoryWriter(store);
        try
        {
            var run = Run(Envelope(1, 10));
            Assert.True(writer.TryEnqueue(run));
            await writer.FlushAsync().WaitAsync(Timeout);
            var finished = Finish(Append(run, Envelope(2, 20)));
            Assert.True(writer.TryEnqueue(finished));
            writer.Complete();
            await writer.Completion.WaitAsync(Timeout);
            var loaded = store.LoadRun(run.Id).Run;
            Assert.Equal(CombatEndReason.ManualStop, loaded.Attempts[0].EndReason);
            Assert.Equal([10L, 20L], loaded.Attempts[0].Events.Select(e => ((CombatDamageEvent)e.Event).Amount));
            Assert.False(store.LoadRun(run.Id).HasIncompleteJournal);
        }
        finally
        {
            await Stop(writer);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static CombatRunDocument Run(params CombatEventEnvelope[] events) => new()
    {
        Id = Guid.NewGuid(), DungeonKey = "synthetic-dungeon", StartedAtUtc = Start,
        Attempts = [new()
        {
            Id = Guid.NewGuid(), BossKey = "synthetic-boss", EncounterVariantKey = "synthetic-normal",
            BossTargets = [Boss], StartedAtUtc = Start, Events = events,
            Participants = [new("synthetic-member", "Synthetic player")],
            MeasurementProfile = new("synthetic-ruleset", "synthetic-build", "synthetic-decoder"),
            Capabilities = CombatCapabilities.None
        }]
    };

    private static CombatRunDocument Append(CombatRunDocument run, CombatEventEnvelope item) => run with
    { Attempts = [run.Attempts[0] with { Events = [.. run.Attempts[0].Events, item] }] };

    private static CombatRunDocument Finish(CombatRunDocument run) => run with
    {
        EndedAtUtc = Start.AddSeconds(10), Attempts = [run.Attempts[0] with
        { DurationTicks = TimeSpan.FromSeconds(10).Ticks, EndReason = CombatEndReason.ManualStop }]
    };

    private static CombatEventEnvelope Envelope(long sequence, long amount) =>
        new(sequence, sequence, Start.AddTicks(sequence), Source, new CombatDamageEvent("synthetic-member", Boss, "synthetic-skill", amount));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Wait(ManualResetEventSlim release)
    { if (!release.Wait(Timeout)) throw new TimeoutException("Synthetic test gate timed out."); }

    private static async Task Stop(CombatHistoryWriter writer)
    {
        writer.Complete();
        try { await writer.Completion.WaitAsync(Timeout); }
        catch (CombatHistoryWriterException) { /* Failure tests assert the explicit fault separately. */ }
    }
}
