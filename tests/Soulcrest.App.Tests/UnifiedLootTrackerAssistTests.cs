using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssistsRetainTheirEvidenceAndAnonymousMetadataWithoutInventingPointIncome(bool estimated)
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.CombatCapabilities = new(true, true, !estimated) { EstimatedAssistSupported = estimated };
        f.Settings.Current.AbyssKillFeedEnabled = f.Settings.Current.AbyssKillFeedShowAssists = false;
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        f.Source.Combat(NetworkAbyssCombatKind.Assist, 1, estimated);
        f.Source.Combat(NetworkAbyssCombatKind.Assist, 1, estimated);
        await f.Ready();
        var session = Assert.Single(f.Service.Snapshot.Sessions);
        var assist = Assert.Single(session.CombatEvents);
        Assert.Equal(LootTrackerCombatKind.Assist, assist.Kind);
        Assert.Equal(estimated, assist.IsEstimated);
        Assert.Equal(LootTrackerActivityOrigin.Abyss, assist.Origin);
        Assert.Equal("Ranger", assist.OpponentClassId); Assert.Equal(19, assist.OpponentAbyssRankGrade);
        Assert.Equal(TimeSpan.FromSeconds(2), assist.Elapsed); Assert.Null(assist.Position);
        Assert.Equal(0, session.AbyssPoints); Assert.Equal(0, session.RankPointsGained); Assert.Equal(0, session.RankPointsLost);
        Assert.DoesNotContain(session.TimelineEvents, fact => fact.Kind is LootTrackerTimelineEventKind.AbyssPoints or LootTrackerTimelineEventKind.RankPoints);
        Assert.DoesNotContain(f.Journal, line => line.Contains("Synthetic opponent", StringComparison.Ordinal)
            || line.Contains("CharacterId", StringComparison.Ordinal) || line.Contains("Damage", StringComparison.Ordinal));
        var read = new UnifiedLootSessionStore(f.SessionRoot).Read();
        Assert.Empty(read.Issues);
        Assert.Equal(assist, Assert.Single(Assert.Single(read.Sessions).CombatEvents));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OfficialAndEstimatedAssistCapabilitiesCannotSubstituteForEachOther(bool officialSupported, bool estimatedSupported)
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.CombatCapabilities = new(true, true, officialSupported) { EstimatedAssistSupported = estimatedSupported };
        f.Source.Combat(NetworkAbyssCombatKind.Assist, 1, estimated: !estimatedSupported);
        await f.Ready();
        Assert.Empty(Assert.Single(f.Service.Snapshot.Sessions).CombatEvents);
    }

    [Theory]
    [InlineData(NetworkAbyssCombatKind.Kill)]
    [InlineData(NetworkAbyssCombatKind.Death)]
    [InlineData((NetworkAbyssCombatKind)99)]
    public async Task EstimatedProofCanOnlyDescribeAnAssist(NetworkAbyssCombatKind kind)
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.CombatCapabilities = new(true, true, true) { EstimatedAssistSupported = true };
        f.Source.Combat(kind, 1, estimated: true); await f.Ready();
        Assert.Empty(Assert.Single(f.Service.Snapshot.Sessions).CombatEvents);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("future")]
    [InlineData("default-time")]
    [InlineData("before-own")]
    [InlineData("sequence")]
    [InlineData("stopped")]
    [InlineData("stale-world")]
    [InlineData("changed-world")]
    [InlineData("revoked-capability")]
    public async Task EstimatedAssistRequiresCurrentOwnSourceTimeAndWorldAtReceiptAndConsumption(string invalid)
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.CombatCapabilities = new(true, true, false) { EstimatedAssistSupported = true };
        var own = f.Source.OwnCharacter!;
        var at = f.Clock.GetUtcNow();
        if (invalid == "foreign") own = new(own.Identity, own.Generation) { ObservedAt = own.ObservedAt };
        if (invalid == "future") at += TimeSpan.FromSeconds(1);
        if (invalid == "default-time") at = default;
        if (invalid == "before-own") at -= TimeSpan.FromTicks(1);
        if (invalid == "stopped") f.Source.Running = false;
        if (invalid == "stale-world") f.WorldStatus = WorldContextStatus.Stale;
        if (invalid == "changed-world") f.BeforeBind = () => { f.WorldGeneration++; f.Source.Notify(); };
        if (invalid == "revoked-capability") f.BeforeBind = () => f.Source.CombatCapabilities = NetworkAbyssCombatCapabilities.None;
        f.Source.Combat(NetworkAbyssCombatKind.Assist, invalid == "sequence" ? 0 : 1, estimated: true, own: own, at: at);
        await f.Ready();
        Assert.All(f.Service.Snapshot.Sessions, session => Assert.Empty(session.CombatEvents));
    }

    [Fact]
    public async Task EstimatedAssistRecordingRespectsTrackingOptOutAndDoesNotReplayTheSuppressedEcho()
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.CombatCapabilities = new(true, true, false) { EstimatedAssistSupported = true };
        f.Settings.Current.AbyssTrackingEnabled = false;
        f.Source.Combat(NetworkAbyssCombatKind.Assist, 1, estimated: true); await f.Ready();
        f.Settings.Current.AbyssTrackingEnabled = true;
        f.Source.Combat(NetworkAbyssCombatKind.Assist, 1, estimated: true); await f.Ready();
        Assert.Empty(Assert.Single(f.Service.Snapshot.Sessions).CombatEvents);
        f.Source.Combat(NetworkAbyssCombatKind.Assist, 2, estimated: true); await f.Ready();
        Assert.True(Assert.Single(Assert.Single(f.Service.Snapshot.Sessions).CombatEvents).IsEstimated);
    }

    [Fact]
    public async Task EstimatedAssistCannotStartAnOutsideSessionOrAddACombatMarkerToOne()
    {
        await using var f = new Fixture(); await f.Ready();
        f.Source.CombatCapabilities = new(true, true, false) { EstimatedAssistSupported = true };
        f.Source.Combat(NetworkAbyssCombatKind.Assist, 1, estimated: true); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions);
        f.Source.Drop(7, 1, 1); await f.Ready();
        f.Source.Combat(NetworkAbyssCombatKind.Assist, 2, estimated: true); await f.Ready();
        Assert.Empty(Assert.Single(f.Service.Snapshot.Sessions).CombatEvents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EstimatedAssistUsesOnlyItsQualifiedCompletionPosition(bool fresh)
    {
        await using var f = new Fixture(abyss: true); await f.Ready();
        f.Source.CombatCapabilities = new(true, true, false) { EstimatedAssistSupported = true };
        f.Clock.Advance(TimeSpan.FromSeconds(3));
        f.Position = () => new(new("test-map", 12, 34, f.Clock.GetUtcNow().AddSeconds(fresh ? 0 : -3))
            { ContextGeneration = 1 }, 1, f.WorldGeneration, f.Source.OwnCharacter!.Generation, f.Clock.GetUtcNow(), 30, 1);
        f.Source.Combat(NetworkAbyssCombatKind.Assist, 1, estimated: true); await f.Ready();
        var assist = Assert.Single(Assert.Single(f.Service.Snapshot.Sessions).CombatEvents);
        Assert.Equal(fresh, assist.Position is not null);
        if (fresh) { Assert.Equal(12, assist.Position!.X); Assert.Equal(34, assist.Position.Y); }
    }
}
