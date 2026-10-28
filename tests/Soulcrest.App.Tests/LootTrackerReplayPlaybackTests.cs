using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerReplayPlaybackTests
{
    [Fact]
    public void NewSessionStartsPausedAtItsEndAndSessionChangeResetsControls()
    {
        var playback = new LootTrackerReplayPlayback();
        Assert.Null(playback.SessionId); Assert.True(playback.FollowingEnd);
        Assert.Equal(1, playback.Direction); Assert.Equal(1, playback.Speed);
        playback.UpdateSession("first", TimeSpan.FromSeconds(20), true);
        playback.Seek(TimeSpan.FromSeconds(5)); playback.Play(-1); playback.SetSpeed(4);
        playback.UpdateSession("second", TimeSpan.FromSeconds(30), false);
        Assert.Equal("second", playback.SessionId); Assert.Equal(TimeSpan.FromSeconds(30), playback.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(30), playback.Duration); Assert.False(playback.IsLive);
        Assert.True(playback.FollowingEnd); Assert.False(playback.IsPlaying);
        Assert.Equal(1, playback.Direction); Assert.Equal(1, playback.Speed);
    }

    [Theory]
    [InlineData(.25)]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void EverySupportedSpeedAdvancesFromTheCallerDelta(double speed)
    {
        var playback = Example();
        playback.SetSpeed(speed); playback.Play();
        Assert.Equal(TimeSpan.Zero, playback.Elapsed); // play at the end restarts at the beginning
        playback.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2 * speed), playback.Elapsed);
        Assert.True(playback.IsPlaying); Assert.False(playback.FollowingEnd);
    }

    [Fact]
    public void ReverseStartsFromEndWhenAtBeginningAndStopsExactlyAtZero()
    {
        var playback = Example();
        playback.Seek(TimeSpan.Zero); playback.SetSpeed(2); playback.Play(-1);
        Assert.Equal(TimeSpan.FromSeconds(20), playback.Elapsed);
        playback.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(14), playback.Elapsed);
        playback.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.Zero, playback.Elapsed); Assert.False(playback.IsPlaying); Assert.False(playback.FollowingEnd);
        Assert.Equal(-1, playback.Direction);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForwardBoundaryFollowsOnlyALiveSession(bool live)
    {
        var playback = Example(live);
        playback.Seek(TimeSpan.FromSeconds(19)); playback.Play(); playback.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(20), playback.Elapsed); Assert.False(playback.IsPlaying);
        Assert.Equal(live, playback.FollowingEnd);
        playback.UpdateSession("example", TimeSpan.FromSeconds(25), live);
        Assert.Equal(TimeSpan.FromSeconds(live ? 25 : 20), playback.Elapsed);
    }

    [Fact]
    public void SeekAlwaysPausesClampsAndDetachesFromFollowing()
    {
        var playback = Example(); playback.Play();
        playback.Seek(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), playback.Elapsed); Assert.False(playback.IsPlaying); Assert.False(playback.FollowingEnd);
        playback.UpdateSession("example", TimeSpan.FromSeconds(30), true);
        Assert.Equal(TimeSpan.FromSeconds(7), playback.Elapsed);
        playback.Seek(TimeSpan.MaxValue); Assert.Equal(playback.Duration, playback.Elapsed); Assert.False(playback.FollowingEnd);
        playback.Seek(TimeSpan.MinValue); Assert.Equal(TimeSpan.Zero, playback.Elapsed);
    }

    [Fact]
    public void JumpToEndPausesResetsDirectionAndFollowsTheGrowingSession()
    {
        var playback = Example(); playback.SetSpeed(4); playback.Play(-1); playback.Advance(TimeSpan.FromSeconds(1));
        playback.JumpToEnd();
        Assert.Equal(playback.Duration, playback.Elapsed); Assert.Equal(1, playback.Direction);
        Assert.True(playback.FollowingEnd); Assert.False(playback.IsPlaying); Assert.Equal(4, playback.Speed);
        playback.UpdateSession("example", TimeSpan.FromSeconds(25), true);
        Assert.Equal(TimeSpan.FromSeconds(25), playback.Elapsed);
    }

    [Fact]
    public void SessionGrowthKeepsHistoricalPlaybackWhileCompletionPausesOnlyOnce()
    {
        var playback = Example(); playback.Seek(TimeSpan.FromSeconds(5)); playback.Play(); playback.Advance(TimeSpan.FromSeconds(2));
        playback.UpdateSession("example", TimeSpan.FromSeconds(30), true);
        Assert.Equal(TimeSpan.FromSeconds(7), playback.Elapsed); Assert.True(playback.IsPlaying);
        playback.UpdateSession("example", TimeSpan.FromSeconds(30), false);
        Assert.Equal(TimeSpan.FromSeconds(7), playback.Elapsed); Assert.False(playback.IsPlaying);
        playback.Play(); playback.UpdateSession("example", TimeSpan.FromSeconds(30), false);
        Assert.True(playback.IsPlaying); // completed-session rerenders do not stop a newly requested replay
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void ShortenedRangeClampsAndPausesBothPlaybackDirections(int direction)
    {
        var playback = Example(); playback.Seek(TimeSpan.FromSeconds(15)); playback.Play(direction);
        playback.UpdateSession("example", TimeSpan.FromSeconds(10), true);
        Assert.Equal(TimeSpan.FromSeconds(10), playback.Elapsed); Assert.False(playback.IsPlaying); Assert.False(playback.FollowingEnd);
    }

    [Fact]
    public void ShortenedRangeStopsImmediatelyWhenThePlayheadIsExactlyAtItsNewBoundary()
    {
        var playback = Example(); playback.Seek(TimeSpan.FromSeconds(10)); playback.Play();
        playback.UpdateSession("example", TimeSpan.FromSeconds(10), true);
        Assert.Equal(TimeSpan.FromSeconds(10), playback.Elapsed); Assert.False(playback.IsPlaying);
    }

    [Fact]
    public void StepIsAPausedClampedSeekEvenForExtremeDeltas()
    {
        var playback = Example(); playback.Seek(TimeSpan.FromSeconds(5)); playback.Play();
        playback.Step(TimeSpan.FromSeconds(3)); Assert.Equal(TimeSpan.FromSeconds(8), playback.Elapsed);
        Assert.False(playback.IsPlaying); Assert.False(playback.FollowingEnd);
        playback.Step(TimeSpan.MaxValue); Assert.Equal(playback.Duration, playback.Elapsed);
        playback.Step(TimeSpan.MinValue); Assert.Equal(TimeSpan.Zero, playback.Elapsed);
    }

    [Fact]
    public void ToggleResumesTheSelectedDirectionAndPausedOrNonpositiveDeltasDoNothing()
    {
        var playback = Example(); playback.Seek(TimeSpan.FromSeconds(10)); playback.Play(-1); playback.TogglePlayPause();
        Assert.False(playback.IsPlaying); playback.Advance(TimeSpan.FromSeconds(2)); Assert.Equal(TimeSpan.FromSeconds(10), playback.Elapsed);
        playback.TogglePlayPause(); Assert.True(playback.IsPlaying); Assert.Equal(-1, playback.Direction);
        playback.Advance(TimeSpan.Zero); playback.Advance(TimeSpan.FromSeconds(-2)); Assert.Equal(TimeSpan.FromSeconds(10), playback.Elapsed);
        playback.Advance(TimeSpan.FromSeconds(2)); Assert.Equal(TimeSpan.FromSeconds(8), playback.Elapsed);
    }

    [Fact]
    public void ZeroDurationCannotPlayAndHugeAdvancesSaturateWithoutOverflow()
    {
        var playback = new LootTrackerReplayPlayback(); playback.UpdateSession("zero", TimeSpan.FromSeconds(-1), true);
        playback.Play(); Assert.False(playback.IsPlaying); Assert.Equal(TimeSpan.Zero, playback.Elapsed);
        playback.UpdateSession("huge", TimeSpan.MaxValue, false); playback.Play(); playback.SetSpeed(8);
        playback.Advance(TimeSpan.MaxValue);
        Assert.Equal(TimeSpan.MaxValue, playback.Elapsed); Assert.False(playback.IsPlaying);
    }

    [Fact]
    public void QuarterSpeedRetainsSubTickProgressAcrossFrames()
    {
        var playback = Example(); playback.Play(); playback.SetSpeed(.25);
        for (var frame = 0; frame < 4; frame++) playback.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(TimeSpan.FromTicks(1), playback.Elapsed);
    }

    [Fact]
    public void InvalidSpeedOrDirectionCannotChangeTheCurrentControls()
    {
        var playback = Example(); playback.SetSpeed(2); playback.Seek(TimeSpan.FromSeconds(5)); playback.Play(-1);
        foreach (var speed in new[] { 0d, -1, .75, 16, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => playback.SetSpeed(speed));
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.Play(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.Play(2));
        Assert.Equal(2, playback.Speed); Assert.Equal(-1, playback.Direction);
        Assert.Equal(TimeSpan.FromSeconds(5), playback.Elapsed); Assert.True(playback.IsPlaying);
    }

    private static LootTrackerReplayPlayback Example(bool live = true)
    {
        var playback = new LootTrackerReplayPlayback();
        playback.UpdateSession("example", TimeSpan.FromSeconds(20), live);
        return playback;
    }
}
