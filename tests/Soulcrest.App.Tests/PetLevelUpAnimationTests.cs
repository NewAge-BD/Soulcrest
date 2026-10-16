using System.Diagnostics;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PetLevelUpAnimationTests
{
    [Fact]
    public void DurationUsesThirtyMonotonicSecondsAndAnEmptyStateHasNoFrame()
    {
        var animation = new PetLevelUpAnimation();
        Assert.Equal(30, PetLevelUpAnimation.DurationSeconds);
        Assert.Null(animation.Sample(0));
        Assert.Null(animation.Sample(long.MaxValue));
    }

    [Fact]
    public void ActiveProgressIsBoundedAndEndsAtTheDurationWithoutAWaitingMessage()
    {
        var animation = new PetLevelUpAnimation();
        var change = Change("first", 1);
        const long start = 1000;
        animation.Enqueue(change, start);
        var beforeStart = Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(start - 1));
        Assert.Equal(change, beforeStart.Change);
        Assert.Equal(0, beforeStart.Progress);
        Assert.Equal(0, Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(start)).Progress);
        Assert.Equal(.5, Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(start + Ticks(15))).Progress, 6);
        Assert.InRange(Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(start + Ticks(30) - 1)).Progress, .999, 1);
        Assert.Null(animation.Sample(start + Ticks(30)));
        Assert.Null(animation.Sample(start + Ticks(60)));
    }

    [Fact]
    public void DuplicateAndLowerActiveLevelsDoNotRestartButAHigherLevelRestartsExactlyOnce()
    {
        var animation = new PetLevelUpAnimation();
        animation.Enqueue(Change("pet", 1), 0);
        animation.Enqueue(Change("pet", 2), Ticks(2));
        var upgraded = Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(2)));
        Assert.Equal(2, upgraded.Change.Level);
        Assert.Equal(0, upgraded.Progress);
        animation.Enqueue(Change("pet", 2), Ticks(3));
        animation.Enqueue(Change("pet", 1), Ticks(3));
        Assert.Equal(1d / 30, Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(3))).Progress, 6);
        animation.Enqueue(Change("pet", 3), Ticks(4));
        var max = Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(4)));
        Assert.Equal(3, max.Change.Level);
        Assert.True(max.Change.IsMax);
        Assert.Equal(0, max.Progress);
        animation.Enqueue(Change("pet", 3), Ticks(5));
        Assert.Equal(1d / 30, Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(5))).Progress, 6);
        Assert.Null(animation.Sample(Ticks(34)));
    }

    [Fact]
    public void WaitingPetsCoalesceAtTheirOriginalQueuePositionAndStartWhenSampled()
    {
        var animation = new PetLevelUpAnimation();
        animation.Enqueue(Change("active", 1), 0);
        animation.Enqueue(Change("second", 1), Ticks(1));
        animation.Enqueue(Change("third", 1), Ticks(2));
        animation.Enqueue(Change("second", 3), Ticks(3));
        animation.Enqueue(Change("second", 2), Ticks(4));
        var second = Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(30)));
        Assert.Equal(Change("second", 3), second.Change);
        Assert.Equal(0, second.Progress);
        Assert.Equal("third", Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(60))).Change.PetId);
        Assert.Null(animation.Sample(Ticks(90)));
    }

    [Fact]
    public void ALongGapExpiresOnlyTheActiveMessageAndGivesEveryWaitingMessageItsFullDuration()
    {
        var animation = new PetLevelUpAnimation();
        animation.Enqueue(Change("active", 1), 0);
        animation.Enqueue(Change("second", 1), Ticks(.1));
        animation.Enqueue(Change("third", 1), Ticks(.2));
        var second = Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(100)));
        Assert.Equal("second", second.Change.PetId);
        Assert.Equal(0, second.Progress);
        Assert.Equal("second", Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(101))).Change.PetId);
        var third = Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(130)));
        Assert.Equal("third", third.Change.PetId);
        Assert.Equal(0, third.Progress);
        Assert.Null(animation.Sample(Ticks(160)));
    }

    [Fact]
    public void QueueRetainsEightWaitingPetsDropsTheOldestWaitingAndCoalescesWithoutEvictingAnotherPet()
    {
        var animation = new PetLevelUpAnimation();
        animation.Enqueue(Change("active", 1), 0);
        for (var index = 0; index < 9; index++) animation.Enqueue(Change("waiting-" + index, 1), 0);
        animation.Enqueue(Change("waiting-1", 2), 0);
        for (var index = 1; index <= 8; index++)
        {
            var frame = Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(index * 30)));
            Assert.Equal("waiting-" + index, frame.Change.PetId);
            Assert.Equal(index == 1 ? 2 : 1, frame.Change.Level);
            Assert.Equal(0, frame.Progress);
        }
        Assert.Null(animation.Sample(Ticks(9 * 30)));
    }

    [Fact]
    public void ClearRemovesTheActiveMessageAndAllWaitingMessagesAndAllowsANewIndependentAnimation()
    {
        var animation = new PetLevelUpAnimation();
        animation.Enqueue(Change("active", 1), 0);
        animation.Enqueue(Change("waiting", 2), 0);
        animation.Clear();
        Assert.Null(animation.Sample(Ticks(1)));
        Assert.Null(animation.Sample(Ticks(100)));
        animation.Clear();
        animation.Enqueue(Change("active", 1), Ticks(100));
        Assert.Equal(0, Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(100))).Progress);
        Assert.Null(animation.Sample(Ticks(130)));
    }

    [Fact]
    public void ParallelNotificationsForOnePetRetainOnlyItsHighestLevel()
    {
        var animation = new PetLevelUpAnimation();
        Parallel.For(0, 300, index => animation.Enqueue(Change("same-pet", index % 3 + 1), 0));
        var frame = Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(0));
        Assert.Equal(Change("same-pet", 3), frame.Change);
        Assert.Null(animation.Sample(Ticks(30)));
    }

    [Fact]
    public void ParallelDistinctPetsRespectTheActivePlusEightWaitingLimitWithoutDuplicatingAnyPet()
    {
        var animation = new PetLevelUpAnimation();
        Parallel.For(0, 100, index => animation.Enqueue(Change("pet-" + index, 1), 0));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 9; index++)
        {
            var frame = Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(index * 30)));
            Assert.True(seen.Add(frame.Change.PetId));
            Assert.Equal(0, frame.Progress);
        }
        Assert.Equal(9, seen.Count);
        Assert.Null(animation.Sample(Ticks(9 * 30)));
    }

    [Fact]
    public void ConcurrentEnqueueSampleAndClearRemainBoundedAndAClearAfterTheWorkersPreventsReappearance()
    {
        var animation = new PetLevelUpAnimation();
        Parallel.For(0, 1000, index =>
        {
            animation.Enqueue(Change("pet-" + index % 20, index % 3 + 1), Ticks(index % 5));
            var frame = animation.Sample(Ticks(index % 9));
            if (frame is not null) Assert.InRange(frame.Progress, 0, 1);
            if (index % 7 == 0) animation.Clear();
        });
        animation.Clear();
        Assert.Null(animation.Sample(0));
        Assert.Null(animation.Sample(long.MaxValue));
    }

    [Fact]
    public void DisablingAtomicallyClearsActiveAndWaitingMessagesAndReenablingDoesNotReplayIgnoredEvents()
    {
        var animation = new PetLevelUpAnimation();
        animation.Enqueue(Change("active", 1), 0);
        animation.Enqueue(Change("waiting", 1), 0);
        animation.SetEnabled(false);
        animation.Enqueue(Change("ignored", 2), Ticks(1));
        Assert.Null(animation.Sample(Ticks(1)));
        animation.SetEnabled(true);
        Assert.Null(animation.Sample(Ticks(2)));
        animation.Enqueue(Change("fresh", 1), Ticks(2));
        Assert.Equal("fresh", Assert.IsType<PetLevelUpAnimation.Frame>(animation.Sample(Ticks(2))).Change.PetId);
        Assert.Null(animation.Sample(Ticks(32)));
    }

    [Fact]
    public void ClosingPermanentlyRejectsFurtherEventsEvenAfterReenableOrClear()
    {
        var animation = new PetLevelUpAnimation();
        animation.Enqueue(Change("active", 1), 0);
        animation.Enqueue(Change("waiting", 1), 0);
        animation.Close();
        animation.SetEnabled(true);
        animation.Enqueue(Change("ignored", 3), Ticks(1));
        Assert.Null(animation.Sample(Ticks(1)));
        animation.Clear();
        animation.SetEnabled(false);
        animation.SetEnabled(true);
        animation.Enqueue(Change("still-ignored", 3), Ticks(2));
        Assert.Null(animation.Sample(Ticks(100)));
    }

    [Fact]
    public void ConcurrentPauseCloseAndNotificationsCannotReopenAClosedAnimation()
    {
        var animation = new PetLevelUpAnimation();
        Parallel.For(0, 1000, index =>
        {
            if (index % 17 == 0) animation.Close();
            else animation.SetEnabled(index % 3 != 0);
            animation.Enqueue(Change("pet-" + index % 20, index % 3 + 1), Ticks(index % 5));
            var frame = animation.Sample(Ticks(index % 9));
            if (frame is not null) Assert.InRange(frame.Progress, 0, 1);
        });
        animation.Close();
        Parallel.For(0, 1000, index =>
        {
            animation.SetEnabled(true);
            animation.Clear();
            animation.Enqueue(Change("late-" + index, 3), Ticks(100));
        });
        Assert.Null(animation.Sample(Ticks(100)));
        Assert.Null(animation.Sample(long.MaxValue));
    }

    private static long Ticks(double seconds) => (long)Math.Ceiling(seconds * Stopwatch.Frequency);
    private static PetLevelUp Change(string id, int level) => new(id, level, level == 3);
}
