using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssRankMotionControllerTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InitialAndRestoredTotalsBecomeSilentBaselines()
    {
        var controller = new AbyssRankTransitionController();
        controller.Observe(Input(Points(8)), 0);
        Assert.Null(controller.Sample(0));
        controller.Observe(Input(Points(9), 2) with { Confirmed = false }, 10);
        controller.Observe(Input(Points(10), 3), 20);
        Assert.Null(controller.Sample(20));
        Assert.Equal(10, controller.CurrentStageIndex);
        Assert.Equal(Points(10), controller.LatestConfirmedPoints);
    }

    [Fact]
    public void PointChangesWithinTheSameStageNeverAnimate()
    {
        var controller = new AbyssRankTransitionController();
        controller.Observe(Input(3_000), 0);
        controller.Observe(Input(11_999, 2), 100);
        Assert.Null(controller.Sample(100));
        Assert.Equal(1, controller.CurrentStageIndex);
        Assert.Equal(11_999, controller.LatestConfirmedPoints);
        controller.Observe(Input(12_000, 3), 200);
        Assert.True(controller.IsAnimating);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryNeighbourWorksInBothDirectionsIncludingTheCatalogEdges(bool reduced)
    {
        var count = AbyssRankCatalog.Default.Stages.Count;
        for (var lower = 0; lower < count - 1; lower++)
        foreach (var upward in new[] { true, false })
        {
            var from = Points(upward ? lower : lower + 1);
            var to = Points(upward ? lower + 1 : lower);
            var controller = Start(from, to, reduced);
            Assert.True(controller.IsAnimating);
            Assert.Equal(upward ? lower : lower + 1, controller.CurrentStageIndex);
            var morphCommit = AbyssRankMotionTimeline.GetMorphCommitMs(from, to, 1, reduced);
            var frame = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(morphCommit));
            Assert.Equal(upward ? lower + 1 : lower, controller.CurrentStageIndex);
            Assert.Equal(to, frame.TotalPoints);
            Assert.Null(controller.Sample(AbyssRankMotionTimeline.GetDurationMs(from, to, reduced)));
            Assert.False(controller.IsAnimating);
            Assert.Equal(to, controller.LatestConfirmedPoints);
        }
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("binding")]
    public void ANewOwnContextCancelsAndAdoptsANewSilentBaseline(string boundary)
    {
        var controller = Start(Points(3), Points(4));
        var next = Input(Points(12), 3);
        next = boundary == "profile" ? next with { ProfileId = "synthetic-other" } : next with { BindingGeneration = 2 };
        controller.Observe(next, 500);
        Assert.Null(controller.Sample(500));
        Assert.Equal(12, controller.CurrentStageIndex);
        controller.Observe(next with { RankPoints = Points(13), RankObservationSequence = 4, ObservedAt = At.AddMilliseconds(4) }, 600);
        Assert.True(controller.IsAnimating);
    }

    [Theory]
    [InlineData("confirmation")]
    [InlineData("binding")]
    [InlineData("tracking")]
    [InlineData("profile")]
    [InlineData("generation")]
    [InlineData("sequence")]
    [InlineData("time")]
    [InlineData("unknown")]
    [InlineData("negative")]
    public void LossOfQualificationAbortsWithoutReplayingOnReconfirmation(string reason)
    {
        var controller = Start(Points(2), Points(3));
        var input = Input(Points(3), 3);
        input = reason switch
        {
            "confirmation" => input with { Confirmed = false },
            "binding" => input with { CharacterBound = false },
            "tracking" => input with { TrackingActive = false },
            "profile" => input with { ProfileId = null },
            "generation" => input with { BindingGeneration = null },
            "sequence" => input with { RankObservationSequence = 0 },
            "time" => input with { ObservedAt = default(DateTimeOffset) },
            "unknown" => input with { RankPoints = null },
            _ => input with { RankPoints = -1 }
        };
        controller.Observe(input, 500);
        Assert.Null(controller.Sample(500));
        Assert.Null(controller.LatestConfirmedPoints);
        controller.Observe(Input(Points(8), 4), 600);
        Assert.Null(controller.Sample(600));
        Assert.Equal(8, controller.CurrentStageIndex);
    }

    [Fact]
    public void HidingStopsMotionAndHiddenChangesAreNotReplayedOnShowing()
    {
        var controller = Start(Points(2), Points(3));
        controller.Observe(Input(Points(3), 3) with { Visible = false }, 500);
        Assert.Null(controller.Sample(500));
        controller.Observe(Input(Points(8), 4) with { Visible = false }, 600);
        controller.Observe(Input(Points(9), 5), 700);
        Assert.Null(controller.Sample(700));
        Assert.Equal(9, controller.CurrentStageIndex);
        controller.Observe(Input(Points(10), 6), 800);
        Assert.True(controller.IsAnimating);
    }

    [Fact]
    public void DuplicateAndOlderSequencesCannotReplaceTheNewestConfirmedTarget()
    {
        var controller = Start(Points(4), Points(5));
        controller.Observe(Input(Points(8), 2), 100);
        controller.Observe(Input(Points(1), 1), 200);
        Assert.Equal(Points(5), controller.LatestConfirmedPoints);
        Assert.Null(controller.Sample(10_000));
        Assert.Equal(5, controller.CurrentStageIndex);
    }

    [Fact]
    public void EqualFactTimesAreOrderedBySequenceAndTimeRegressionRevokesContinuity()
    {
        var controller = new AbyssRankTransitionController();
        controller.Observe(Input(Points(1)) with { ObservedAt = At }, 0);
        controller.Observe(Input(Points(2), 2) with { ObservedAt = At }, 100);
        Assert.True(controller.IsAnimating);
        controller.Observe(Input(Points(3), 3) with { ObservedAt = At.AddMilliseconds(-1) }, 200);
        Assert.Null(controller.Sample(200));
        controller.Observe(Input(Points(4), 4), 300);
        Assert.Null(controller.Sample(300));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiStageRoutesCommitEveryOriginalNeighbourWithoutRepeatedTravel(bool reduced)
    {
        var from = Points(0);
        var last = AbyssRankCatalog.Default.Stages.Count - 1;
        var to = Points(last);
        var controller = Start(from, to, reduced);
        for (var step = 1; step <= last; step++)
        {
            var frame = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(
                AbyssRankMotionTimeline.GetMorphCommitMs(from, to, step, reduced)));
            Assert.Equal(step, controller.CurrentStageIndex);
            Assert.Equal(reduced ? 32 : 240, frame.CenterX, 5);
            Assert.Equal(to, frame.TotalPoints);
        }
        Assert.Null(controller.Sample(AbyssRankMotionTimeline.GetDurationMs(from, to, reduced)));
        Assert.Equal(last, controller.CurrentStageIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReversalWaitsForTheCurrentOriginalNeighbourAndThenUsesTheLatestTarget(bool reduced)
    {
        var from = Points(4);
        var to = Points(7);
        var target = Points(2);
        var controller = Start(from, to, reduced);
        var commit = AbyssRankMotionTimeline.GetMorphCommitMs(from, to, 1, reduced);
        var changeAt = (AbyssRankMotionTimeline.GetMorphStartMs(from, to, reduced) + commit) / 2;
        controller.Observe(Input(Points(12), 3), changeAt);
        controller.Observe(Input(target, 4), changeAt + 1);
        var before = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(commit - 1));
        Assert.Equal(5, before.MotionToStageIndex);
        var redirected = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(commit));
        Assert.Equal(5, controller.CurrentStageIndex);
        Assert.Equal(5, redirected.MotionFromStageIndex);
        Assert.Equal(target, redirected.TotalPoints);
        Assert.Equal(reduced ? 32 : 240, redirected.CenterX, 5);
        Assert.Equal(4, redirected.DisplayCurrentStageIndex);
        Assert.Equal(0, redirected.NextOpacity);
        var continuing = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(commit + .01));
        Assert.Equal(4, continuing.MotionToStageIndex);
        Assert.Null(controller.Sample(20_000));
        Assert.Equal(2, controller.CurrentStageIndex);
        Assert.Equal(target, controller.LatestConfirmedPoints);
    }

    [Fact]
    public void ATargetAlreadyReachedAtTheNextNeighbourReturnsWithoutAnExtraMorph()
    {
        var from = Points(3);
        var to = Points(8);
        var controller = Start(from, to);
        var commit = AbyssRankMotionTimeline.GetMorphCommitMs(from, to, 1);
        controller.Observe(Input(Points(4), 3), commit - 10);
        var frame = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(commit + 10));
        Assert.True(frame.CenterX < 240);
        Assert.Equal(4, controller.CurrentStageIndex);
        Assert.Null(controller.Sample(10_000));
        Assert.Equal(4, controller.CurrentStageIndex);
    }

    [Fact]
    public void AnUpdateExactlyAtAMorphCommitCanRedirectWithoutAnotherNeighbour()
    {
        var from = Points(3);
        var to = Points(8);
        var controller = Start(from, to);
        var commit = AbyssRankMotionTimeline.GetMorphCommitMs(from, to, 1);
        controller.Observe(Input(Points(2), 3), commit);
        var frame = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(commit));
        Assert.Equal(4, frame.MotionFromStageIndex);
        Assert.Equal(3, frame.MotionToStageIndex);
        Assert.Null(controller.Sample(10_000));
        Assert.Equal(2, controller.CurrentStageIndex);
    }

    [Fact]
    public void RetargetingPreservesScaleLightAndBarAtTheOriginalNeighbourThenBlendsThemOver80Milliseconds()
    {
        var from = Points(3);
        var to = Points(6);
        var controller = Start(from, to);
        var firstCommit = AbyssRankMotionTimeline.GetMorphCommitMs(from, to, 1);
        var secondCommit = AbyssRankMotionTimeline.GetMorphCommitMs(from, to, 2);
        controller.Observe(Input(Points(2), 3), firstCommit + 30);
        var outgoing = AbyssRankMotionTimeline.Sample(from, to, secondCommit);
        var anchor = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(secondCommit));
        Assert.Equal(outgoing.IconScale, anchor.IconScale, 10);
        Assert.Equal(outgoing.LightStrength, anchor.LightStrength, 10);
        Assert.Equal(outgoing.BarProgress, anchor.BarProgress, 10);
        Assert.Equal(5, anchor.MotionFromStageIndex);
        Assert.Equal(4, anchor.MotionToStageIndex);
        Assert.Equal(1, anchor.MorphProgress);
        Assert.Equal(0, anchor.NextOpacity);
        var redirectedFrom = AbyssRankCatalog.Default.Stages[5].Threshold;
        var newMorphStart = AbyssRankMotionTimeline.GetMorphStartMs(redirectedFrom, Points(2));
        var rawMiddle = AbyssRankMotionTimeline.Sample(redirectedFrom, Points(2), newMorphStart + 40);
        var middle = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(secondCommit + 40));
        Assert.Equal((outgoing.BarProgress + rawMiddle.BarProgress) / 2, middle.BarProgress, 10);
        var rawAfter = AbyssRankMotionTimeline.Sample(redirectedFrom, Points(2), newMorphStart + 80);
        var after = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(secondCommit + 80));
        Assert.Equal(rawAfter.IconScale, after.IconScale, 10);
        Assert.Equal(rawAfter.LightStrength, after.LightStrength, 10);
        Assert.Equal(rawAfter.BarProgress, after.BarProgress, 10);
        Assert.Null(controller.Sample(100_000));
        Assert.Equal(2, controller.CurrentStageIndex);
    }

    [Fact]
    public void ReturningToTheSourceDuringDepartureBringsTheUnchangedOriginalHomeWithoutAnyMorph()
    {
        var from = Points(3);
        var to = Points(4);
        var controller = Start(from, to);
        var departureTime = 200d;
        var outgoing = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(departureTime));
        controller.Observe(Input(from + 25, 3), departureTime);
        var anchor = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(departureTime));
        Assert.Equal(outgoing.CenterX, anchor.CenterX);
        Assert.Equal(outgoing.IconScale, anchor.IconScale);
        Assert.Equal(outgoing.BarProgress, anchor.BarProgress);
        Assert.Equal(3, anchor.MotionFromStageIndex);
        Assert.Equal(3, anchor.MotionToStageIndex);
        var returning = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(departureTime + 80));
        Assert.InRange(returning.CenterX, 32, outgoing.CenterX);
        Assert.True(returning.CenterX < outgoing.CenterX);
        Assert.Equal(3, returning.MotionFromStageIndex);
        Assert.Equal(3, returning.MotionToStageIndex);
        Assert.Null(controller.Sample(100_000));
        Assert.Equal(3, controller.CurrentStageIndex);
        Assert.Equal(from + 25, controller.LatestConfirmedPoints);
    }

    [Fact]
    public void ANewTargetWhileAnUnchangedIconReturnsStillConvergesAfterAnArbitraryTimeJump()
    {
        var from = Points(3);
        var controller = Start(from, Points(4));
        controller.Observe(Input(from, 3), 200);
        controller.Observe(Input(Points(12), 4), 220);
        controller.Observe(Input(Points(1), 5), 240);
        Assert.Null(controller.Sample(1_000_000_000));
        Assert.Equal(1, controller.CurrentStageIndex);
        Assert.Equal(Points(1), controller.LatestConfirmedPoints);
    }

    [Fact]
    public void AnUpdateDuringTheReturnBeginsFromTheSettledOriginalAndKeepsTheLatestTotal()
    {
        var from = Points(4);
        var to = Points(5);
        var controller = Start(from, to);
        var returned = AbyssRankMotionTimeline.GetReturnStartMs(from, to);
        var duration = AbyssRankMotionTimeline.GetDurationMs(from, to);
        controller.Observe(Input(Points(2), 3), returned + 50);
        var frame = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(duration + 1));
        Assert.Equal(5, frame.MotionFromStageIndex);
        Assert.Equal(5, controller.CurrentStageIndex);
        Assert.Equal(Points(2), frame.TotalPoints);
        Assert.Null(controller.Sample(10_000));
        Assert.Equal(2, controller.CurrentStageIndex);
    }

    [Fact]
    public void ANewTotalWithinTheDestinationStageUpdatesTheFinalProgressWithoutANewTrip()
    {
        var from = Points(1);
        var to = Points(2);
        var controller = Start(from, to);
        var newest = to + 333;
        controller.Observe(Input(newest, 3), 500);
        var frame = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(600));
        Assert.Equal(newest, frame.TotalPoints);
        Assert.Null(controller.Sample(AbyssRankMotionTimeline.GetDurationMs(from, to)));
        Assert.Equal(newest, controller.LatestConfirmedPoints);
        Assert.Equal(2, controller.CurrentStageIndex);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReturningIconsAlreadyUseTheEvolvedLabelAndRemainingPointsWhileTheOldRightSlotIsInvisible(bool upward)
    {
        var fromStage = upward ? 3 : 4;
        var toStage = upward ? 4 : 3;
        var from = Points(fromStage);
        var to = Points(toStage);
        var controller = Start(from, to);
        var frame = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(
            AbyssRankMotionTimeline.GetReturnStartMs(from, to) + 200));
        Assert.Equal(toStage, frame.DisplayCurrentStageIndex);
        Assert.Equal(fromStage + 1, frame.DisplayNextStageIndex);
        Assert.Equal(0, frame.NextOpacity);
        Assert.Equal(AbyssRankCatalog.Default.Stages[toStage + 1].Threshold - to, frame.PointsToNext);
        Assert.True(frame.PointsToNext > 0);
    }

    [Fact]
    public void CancelForgetsThePriorBaselineAndTheClockNeverRewindsTheAnimation()
    {
        var controller = Start(0, 3_000);
        var first = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(600));
        var earlier = Assert.IsType<AbyssRankMotionFrame>(controller.Sample(300));
        Assert.Equal(first, earlier);
        controller.Cancel();
        Assert.Null(controller.Sample(700));
        controller.Observe(Input(long.MaxValue, 3), 800);
        Assert.Null(controller.Sample(800));
        Assert.Equal(AbyssRankCatalog.Default.Stages.Count - 1, controller.CurrentStageIndex);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    public void InvalidMonotonicTimesAreRejected(double time)
    {
        var controller = new AbyssRankTransitionController();
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Observe(Input(0), time));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Sample(time));
    }

    private static AbyssRankTransitionController Start(long from, long to, bool reduced = false)
    {
        var controller = new AbyssRankTransitionController(reduced);
        controller.Observe(Input(from), 0);
        controller.Observe(Input(to, 2), 0);
        return controller;
    }

    private static AbyssRankMotionInput Input(long points, long sequence = 1) =>
        new("synthetic-profile", 1, sequence, At.AddMilliseconds(sequence), points);

    private static long Points(int index) => AbyssRankCatalog.Default.Stages[index].Threshold + 100;
}
