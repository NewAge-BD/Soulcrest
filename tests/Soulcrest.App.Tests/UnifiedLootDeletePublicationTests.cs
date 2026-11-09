using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualDeletionPublishesBeforeCompletingEvenWhenObservationWorkIsPending(bool cleanupPending)
    {
        var legacy = new LootTrackerSession
        {
            Id = Guid.NewGuid().ToString("N"), Kind = LootTrackerSessionKind.Grind,
            StartedAt = DateTimeOffset.UnixEpoch, LastLootAt = DateTimeOffset.UnixEpoch,
            Status = LootTrackerSessionStatus.Completed
        };
        await using var f = new Fixture(legacy: cleanupPending ? [legacy] : null, deleteLegacy: _ => true);
        await f.Ready();
        if (!cleanupPending) { f.Source.Drop(7, 1, 1); await f.Ready(); }
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id;
        using var releasePublication = new ManualResetEventSlim();
        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = 0; var publicationBlocked = 0;
        f.Service.SessionDeleted += _ =>
        {
            Interlocked.Increment(ref deleted);
            f.Source.Notify(); // Pending work makes the post-delete retention sweep yield.
        };
        f.Service.Changed += () =>
        {
            if (Volatile.Read(ref deleted) == 0 || !f.Service.Snapshot.Sessions.IsEmpty
                || Interlocked.Exchange(ref publicationBlocked, 1) != 0) return;
            publicationEntered.TrySetResult();
            releasePublication.Wait(TimeSpan.FromSeconds(10));
        };
        var deletion = f.Service.DeleteSessionAsync(id);
        try
        {
            await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<TimeoutException>(() => deletion.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.Empty(f.Service.Snapshot.Sessions);
            Assert.Null(f.Service.OverlayState.Session);
            if (cleanupPending) Assert.Equal("Session gelöscht; Dateibereinigung ausstehend.", f.Service.Status);
        }
        finally { releasePublication.Set(); }
        await deletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref deleted));
        Assert.Empty(f.Service.Snapshot.Sessions);
    }
}
