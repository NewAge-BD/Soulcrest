using Soulcrest.Core.Alerts;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class AdvanceWarningsTests
{
    [Fact]
    public void LegacyEmptyAndDuplicateSelectionsRemainDistinct()
    {
        Assert.Equal(new[] { 300 }, AdvanceWarnings.Resolve(null, 300, 3600));
        Assert.Empty(AdvanceWarnings.Resolve([], 300, 3600));
        Assert.Empty(AdvanceWarnings.Resolve(null, 0, 3600));
        Assert.Equal(new[] { 900, 300, 147 }, AdvanceWarnings.Resolve([300, 147, 900, 300], 0, 3600));
        Assert.False(AdvanceWarnings.Valid([3601], 3600));
        Assert.False(AdvanceWarnings.Valid([0], 3600));
        Assert.Equal(new[] { 900 }, AdvanceWarnings.Toggle([900, 300], 300, true));
        Assert.Equal(new[] { 900, 300 }, AdvanceWarnings.Toggle([900, 300], 300, false));
    }

    [Fact]
    public void BossStagesCatchUpOnceThenContinueAndNeverInferSpawn()
    {
        const long now = 1800000000000;
        var at = now + 900000;
        var policy = new BossAlertPolicy();
        var selected = new HashSet<string> { "1110:111001" };
        BossRushSnapshot Snapshot(long clock, bool spawned = false) => new(1110, [new(111001, spawned, at)], clock, TimeSpan.Zero);
        IReadOnlyList<BossAlertTrigger> Tick(long clock, bool spawned = false) => policy.EvaluateMany(Snapshot(clock, spawned), selected, _ => [900, 600, 300, 120]);
        Assert.Equal(600, Assert.Single(Tick(now + 310000)).LeadSeconds);
        Assert.Empty(Tick(now + 310001));
        Assert.Equal(300, Assert.Single(Tick(now + 600000)).LeadSeconds);
        Assert.Empty(Tick(now + 100000)); // clock correction cannot replay the 15-minute stage
        Assert.Equal(120, Assert.Single(Tick(now + 780000)).LeadSeconds);
        Assert.Empty(Tick(at));
        Assert.True(Assert.Single(Tick(at, true)).Spawned);
        Assert.Empty(Tick(at + 1000, true));
    }

    [Fact]
    public void PausedAndCachedBossesDoNotConsumeStages()
    {
        const long now = 1800000000000;
        var snapshot = new BossRushSnapshot(1110, [new(111001, false, now + 240000)], now, TimeSpan.Zero);
        var policy = new BossAlertPolicy();
        var selected = new HashSet<string> { "1110:111001" };
        Assert.Empty(policy.EvaluateMany(snapshot, selected, _ => null));
        Assert.Empty(policy.EvaluateMany(snapshot with { Cached = true }, selected, _ => [900, 600, 300]));
        Assert.Equal(300, Assert.Single(policy.EvaluateMany(snapshot, selected, _ => [900, 600, 300])).LeadSeconds);
        Assert.Empty(policy.EvaluateMany(snapshot, selected, _ => []));
        Assert.True(Assert.Single(policy.EvaluateMany(snapshot with { Bosses = [new(111001, true, now + 240000)] }, selected, _ => [])).Spawned);
    }
}
