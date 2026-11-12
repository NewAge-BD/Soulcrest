using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootSessionDiagnosticsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.FromHours(1));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingWorldContextIsAnExplicitAnonymousFactIndependentOfAnEmptySessionList(bool waiting)
    {
        var result = DiagnosticContributors.LootSessionSnapshot(new([], At), false, false, 20, 0, false, At, true, waiting);
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(waiting, result.Facts["waitingForWorldContext"]);
        Assert.Equal(0, result.Facts["liveSessions"]);
        Assert.Equal(0, result.Facts["pausedSessions"]);
    }

    [Fact]
    public void PausedSessionsPolicyAndRetentionAreProjectedWithoutIdentitiesOrSnapshotStrings()
    {
        const string privateText = "Synthetic private session character";
        var snapshot = new LootTrackerSnapshot([
            new() { Id = privateText, Location = privateText, Status = LootTrackerSessionStatus.Live,
                IsPaused = true, Character = new(privateText, privateText) },
            new() { Status = LootTrackerSessionStatus.Live, Kind = LootTrackerSessionKind.Abyss },
            new() { Status = LootTrackerSessionStatus.Interrupted }
        ], At);
        var result = DiagnosticContributors.LootSessionSnapshot(snapshot, true, false, 20, 7, false, At, true);
        Assert.Equal("loot-sessions", result.FeatureId);
        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
        Assert.Equal(At.ToUniversalTime(), result.CheckedAt);
        Assert.Equal(3, result.Facts["sessions"]);
        Assert.Equal(2, result.Facts["liveSessions"]);
        Assert.Equal(1, result.Facts["pausedSessions"]);
        Assert.Equal(1, result.Facts["interruptedSessions"]);
        Assert.Equal(20, result.Facts["idlePauseSeconds"]);
        Assert.Equal(120, result.Facts["idleCloseSeconds"]);
        Assert.Equal(20L, result.Facts["smallSessionItemThreshold"]);
        Assert.Equal(true, result.Facts["automaticSmallSessionDeletionEnabled"]);
        Assert.Equal(7L, result.Facts["automaticallyDeletedSmallSessions"]);
        Assert.Equal(false, result.Facts["smallSessionCleanupIncomplete"]);
        Assert.Equal(true, result.Facts["abyssSessionsExcludedFromIdleAndAutomaticDeletion"]);
        Assert.Equal(true, result.Facts["snapshotFresh"]);
        Assert.Equal(0L, result.Facts["snapshotAgeMilliseconds"]);
        Assert.Equal(At.ToUniversalTime(), result.Facts["observedAtUtc"]);
        Assert.DoesNotContain(privateText, JsonSerializer.Serialize(result));
        Assert.All(result.Facts.Values, value => Assert.True(value is null or bool or int or long or DateTimeOffset));
    }

    [Fact]
    public void MissingSettingsAndObservationStayUnknownRatherThanEnablingCleanup()
    {
        var result = DiagnosticContributors.LootSessionSnapshot(LootTrackerSnapshot.Empty, false, false,
            null, 0, false, At, null);
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Null(result.Facts["trackingEnabled"]);
        Assert.Null(result.Facts["waitingForWorldContext"]);
        Assert.Null(result.Facts["snapshotFresh"]);
        Assert.Null(result.Facts["snapshotAgeMilliseconds"]);
        Assert.Null(result.Facts["smallSessionItemThreshold"]);
        Assert.Null(result.Facts["automaticSmallSessionDeletionEnabled"]);
        Assert.Null(result.Facts["observedAtUtc"]);
        Assert.Equal(false, result.Facts["sourceAvailable"]);
    }

    [Theory]
    [InlineData(-5L)]
    [InlineData(0L)]
    public void ZeroOrInvalidNegativeThresholdIsReportedDisabledAndCleanupFailureRemainsVisible(long threshold)
    {
        var result = DiagnosticContributors.LootSessionSnapshot(LootTrackerSnapshot.Empty, true, true,
            threshold, -1, true, At, true);
        Assert.Equal(DiagnosticStatus.Error, result.Status);
        Assert.Equal(0L, result.Facts["smallSessionItemThreshold"]);
        Assert.Equal(false, result.Facts["automaticSmallSessionDeletionEnabled"]);
        Assert.Equal(0L, result.Facts["automaticallyDeletedSmallSessions"]);
        Assert.Equal(true, result.Facts["smallSessionCleanupIncomplete"]);
    }

    [Theory]
    [InlineData(false, false, DiagnosticStatus.Disabled)]
    [InlineData(true, false, DiagnosticStatus.Waiting)]
    public void DisabledOrUnavailableTrackingDoesNotReportHealthy(bool enabled, bool available, DiagnosticStatus expected)
    {
        var result = DiagnosticContributors.LootSessionSnapshot(new([], At), available, false, 20, 0, false, At, enabled);
        Assert.Equal(expected, result.Status);
        Assert.Equal(enabled, result.Facts["trackingEnabled"]);
    }

    [Theory]
    [InlineData(-6, false)]
    [InlineData(1, false)]
    [InlineData(-5, true)]
    [InlineData(0, true)]
    public void SnapshotAgeAndTimeDirectionAreCheckedEvenWhenTheCachedSourceFlagIsTrue(int observedSeconds, bool fresh)
    {
        var result = DiagnosticContributors.LootSessionSnapshot(new([], At.AddSeconds(observedSeconds)),
            true, false, 20, 0, false, At, true);
        Assert.Equal(fresh ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(fresh, result.Facts["snapshotFresh"]);
        Assert.Equal(-observedSeconds * 1_000L, result.Facts["snapshotAgeMilliseconds"]);
    }
}
