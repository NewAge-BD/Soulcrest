using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class BossAlertTests
{
    private const long Now = 1791257500000;
    private static BossRushSnapshot Snapshot(bool spawned, long at, long now = Now, int map = 1110, int age = 0) =>
        new(map, [new(111001, spawned, at)], now, TimeSpan.FromSeconds(age));

    [Fact]
    public void WarnsOnceThenAlertsOnlyOnConfirmedSpawnAndAllowsNewCycle()
    {
        var policy = new BossAlertPolicy();
        var selected = new HashSet<string> { "1110:111001" };
        Assert.Empty(policy.Evaluate(Snapshot(false, Now + 61000), selected, 60));
        Assert.False(Assert.Single(policy.Evaluate(Snapshot(false, Now + 60000), selected, 60)).Spawned);
        Assert.Empty(policy.Evaluate(Snapshot(false, Now + 60000, Now + 1000), selected, 60));
        Assert.Empty(policy.Evaluate(Snapshot(false, Now + 60000, Now + 61000), selected, 60));
        Assert.True(Assert.Single(policy.Evaluate(Snapshot(true, Now + 60000, Now + 61000), selected, 60)).Spawned);
        Assert.Empty(policy.Evaluate(Snapshot(true, Now + 60000, Now + 62000), selected, 60));
        Assert.Single(policy.Evaluate(Snapshot(false, Now + 120000, Now + 70000), selected, 60));
    }

    [Fact]
    public void IgnoresUnselectedMapsCachedListsAndWaitingTimersWithZeroLead()
    {
        var policy = new BossAlertPolicy();
        var selected = new HashSet<string> { "1110:111001" };
        Assert.Empty(policy.Evaluate(Snapshot(true, Now - 1000), new HashSet<string>(), 60));
        Assert.Empty(policy.Evaluate(Snapshot(true, Now - 1000, map: 1010), selected, 60));
        Assert.Empty(policy.Evaluate(Snapshot(true, Now - 1000) with { Cached = true }, selected, 60));
        Assert.Empty(policy.Evaluate(Snapshot(false, Now + 1000), selected, 0));
        Assert.Single(policy.Evaluate(Snapshot(true, Now - 1000), selected, 0));
    }

    [Fact]
    public void CachedSnapshotNeverAlertsAndRefreshDoesNotRepeatAnAlreadySeenCycle()
    {
        var policy = new BossAlertPolicy();
        var selected = new HashSet<string> { "1110:111001" };
        var live = Snapshot(false, Now + 10000);
        Assert.Empty(policy.Evaluate(live with { Cached = true }, selected, 60));
        Assert.Single(policy.Evaluate(live, selected, 60));
        Assert.Empty(policy.Evaluate(live with { Cached = true }, selected, 60));
        Assert.Empty(policy.Evaluate(live, selected, 60));
        Assert.Empty(policy.Evaluate(Snapshot(true, Now + 10000) with { Cached = true }, selected, 60));
        Assert.Single(policy.Evaluate(Snapshot(true, Now + 10000), selected, 60));
    }

    [Fact]
    public void IndividualLeadsDoNotChangeOtherBossesAndOffKeepsTheCycleUnseen()
    {
        var policy = new BossAlertPolicy();
        var selected = new HashSet<string> { "1110:111001", "1110:111002", "1110:111003" };
        var leads = new Dictionary<string, int?> { ["1110:111001"] = 120, ["1110:111002"] = 900, ["1110:111003"] = null };
        var snapshot = new BossRushSnapshot(1110,
            [new(111001, false, Now + 300000), new(111002, false, Now + 300000), new(111003, true, Now)], Now, TimeSpan.Zero);
        var first = Assert.Single(policy.Evaluate(snapshot, selected, key => leads[key]));
        Assert.Equal(111002, first.Boss.SpawnId);
        Assert.Empty(policy.Evaluate(snapshot, selected, key => leads[key]));
        var later = snapshot with { ServerUnixMs = Now + 180000 };
        Assert.Equal(111001, Assert.Single(policy.Evaluate(later, selected, key => leads[key])).Boss.SpawnId);
        leads["1110:111003"] = 0;
        Assert.True(Assert.Single(policy.Evaluate(later, selected, key => leads[key])).Spawned);
    }

    [Fact]
    public void ChangingAnIndividualLeadDoesNotRepeatWarningAndCacheNeverAlerts()
    {
        var policy = new BossAlertPolicy();
        var selected = new HashSet<string> { "1110:111001" };
        var upcoming = Snapshot(false, Now + 120000, age: 600);
        Assert.Empty(policy.Evaluate(upcoming with { Cached = true }, selected, _ => 900));
        Assert.Single(policy.Evaluate(upcoming, selected, _ => 120));
        Assert.Empty(policy.Evaluate(upcoming, selected, _ => 900));
        Assert.Empty(policy.Evaluate(upcoming with { Bosses = [new(111001, true, Now + 120000)] }, selected, _ => null));
        Assert.True(Assert.Single(policy.Evaluate(upcoming with { Bosses = [new(111001, true, Now + 120000)] }, selected, _ => 0)).Spawned);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(3600)]
    public void ConfirmedSpawnDoesNotExpireWithTheListObservationAge(int age)
    {
        var policy = new BossAlertPolicy();
        var selected = new HashSet<string> { "1110:111001" };
        var snapshot = Snapshot(true, Now - 1000, age: age);
        Assert.True(Assert.Single(policy.Evaluate(snapshot, selected, 0)).Spawned);
        Assert.Empty(policy.Evaluate(snapshot with { Age = TimeSpan.FromSeconds(age + 60) }, selected, 0));
    }

    [Fact]
    public void LongUnchangedScheduleWarnsAtItsLeadWindowAndTimerExpiryDoesNotProveSpawn()
    {
        var policy = new BossAlertPolicy();
        var selected = new HashSet<string> { "1110:111001" };
        var upcoming = Snapshot(false, Now + 600000);
        Assert.Empty(policy.Evaluate(upcoming, selected, 60));
        var near = upcoming with { ServerUnixMs = Now + 540000, Age = TimeSpan.FromSeconds(540) };
        Assert.False(Assert.Single(policy.Evaluate(near, selected, 60)).Spawned);
        Assert.Empty(policy.Evaluate(near with { ServerUnixMs = Now + 550000, Age = TimeSpan.FromSeconds(550) }, selected, 60));
        var elapsed = near with { ServerUnixMs = Now + 600000, Age = TimeSpan.FromMinutes(10) };
        Assert.Empty(policy.Evaluate(elapsed, selected, 60));
        Assert.Empty(new BossAlertPolicy().Evaluate(elapsed, selected, 60)); // no warning was seen, but zero is still no spawn
        Assert.Empty(new BossAlertPolicy().Evaluate(elapsed with { ServerUnixMs = Now + 610000 }, selected, 60));
        Assert.Empty(policy.Evaluate(elapsed with { Bosses = [new(111001, true, Now + 600000)], Cached = true }, selected, 60));
        Assert.True(Assert.Single(policy.Evaluate(elapsed with { Bosses = [new(111001, true, Now + 600000)], Age = TimeSpan.Zero }, selected, 60)).Spawned);
    }

    [Fact]
    public void SimultaneousBossesKeepTheirOwnAlertsInSpawnOrder()
    {
        var policy = new BossAlertPolicy();
        var snapshot = new BossRushSnapshot(1110, [new(111002, true, Now - 1000), new(111001, true, Now - 2000)], Now, TimeSpan.Zero);
        var alerts = policy.Evaluate(snapshot, new HashSet<string> { "1110:111001", "1110:111002" }, 60);
        Assert.Equal(new[] { 111001, 111002 }, alerts.Select(a => a.Boss.SpawnId));
    }
}
