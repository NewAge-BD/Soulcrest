using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatRunStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-run-storage-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly CombatSourceContext Source = new("synthetic-stream", 1);
    private static readonly CombatEntityReference Boss = new("synthetic-boss-entity", 1, "synthetic-stream");

    [Fact]
    public void CompletedAttemptAcceptsOnlyIdenticalJournalReplay()
    {
        var store = new CombatHistoryStore(_directory);
        var run = Run(); store.SaveRun(run);
        store.AppendEvents(run.Id, run.Attempts[0].Id, run.Attempts[0].Events);
        var before = JsonSerializer.Serialize(store.LoadRun(run.Id).Run);
        var extra = new CombatEventEnvelope(2, 1, Start, Source, new CombatDamageEvent("player", Boss, "skill", 10));
        Assert.Throws<InvalidOperationException>(() => store.AppendEvents(run.Id, run.Attempts[0].Id, [extra]));
        Assert.Throws<InvalidOperationException>(() => store.SaveRun(run with
        { Attempts = [run.Attempts[0] with { Events = [.. run.Attempts[0].Events, extra] }] }));
        Assert.Equal(before, JsonSerializer.Serialize(store.LoadRun(run.Id).Run));
    }

    [Fact]
    public void UnclassifiedLocalAmountRoundtripsAndDoesNotBecomeDamage()
    {
        var store = new CombatHistoryStore(_directory);
        var run = Run() with { Attempts = [Run().Attempts[0] with
        {
            Events = [new(1, 0, Start, Source,
                new CombatUnclassifiedAmountEvent("player", null, Boss, null, "unknown-effect", null, true))]
        }] };
        store.SaveRun(run);
        var attempt = store.LoadRun(run.Id).Run.Attempts[0];
        Assert.IsType<CombatUnclassifiedAmountEvent>(Assert.Single(attempt.Events).Event);
        var summary = CombatProjection.Project(attempt);
        Assert.Equal(0, summary.Damage);
        Assert.False(summary.IsComplete);
        Assert.Contains(summary.QualityIssues, q => q.Code == "amount-semantics-unconfirmed");
    }

    [Fact]
    public void StaleControllerCheckpointCannotUndoFavorite()
    {
        var store = new CombatHistoryStore(_directory);
        var run = Run(); store.SaveRun(run);
        store.SetFavorite(run.Id, true);
        store.SaveRun(run);
        Assert.True(store.LoadRun(run.Id).Run.Favorite);
    }

    [Fact]
    public void ClosedRunCannotGainAnotherAttempt()
    {
        var store = new CombatHistoryStore(_directory);
        var run = Run(); store.SaveRun(run);
        Assert.Throws<InvalidOperationException>(() => store.SaveRun(run with
            { Attempts = [.. run.Attempts, run.Attempts[0] with { Id = Guid.NewGuid() }] }));
        Assert.Single(store.LoadRun(run.Id).Run.Attempts);
    }

    [Fact]
    public void LifecycleGapUtcIsExplicitlyAnAnchorRatherThanAnObservedPacketTimestamp()
    {
        var controller = new CombatRunController();
        var stamp = new CombatControllerStamp(1, 0, Start, Source);
        controller.ConfirmDungeon(stamp, "synthetic-dungeon", null);
        controller.SetRoster(stamp, [new("player")]);
        controller.BindEncounter(stamp, new("synthetic-boss", null, CombatAttemptKind.Boss,
            CombatCapabilities.Damage, new("synthetic-rules")), [Boss]);
        controller.Observe(stamp, new CombatDamageEvent("player", Boss, "skill", 100));
        controller.Interrupt(stamp with { Sequence = 2, ElapsedTicks = 10, IsCaptureTimestampObserved = false }, CombatGapReason.Transport);
        var run = controller.Snapshot.CurrentRun!;
        var store = new CombatHistoryStore(_directory); store.SaveRun(run);
        var events = store.LoadRun(run.Id).Run.Attempts[0].Events;
        Assert.True(events[0].IsCaptureTimestampObserved);
        Assert.False(events[^1].IsCaptureTimestampObserved);
        Assert.IsType<CombatGapEvent>(events[^1].Event);
    }

    private static CombatRunDocument Run() => new()
    {
        Id = Guid.NewGuid(), DungeonKey = "synthetic-dungeon", StartedAtUtc = Start, EndedAtUtc = Start.AddSeconds(10),
        Attempts = [new()
        {
            Id = Guid.NewGuid(), BossKey = "synthetic-boss", BossTargets = [Boss], StartedAtUtc = Start,
            DurationTicks = TimeSpan.FromSeconds(10).Ticks, EndReason = CombatEndReason.Victory,
            Capabilities = CombatCapabilities.Damage, Participants = [new("player", "Player One")],
            Events = [new(1, 0, Start, Source, new CombatDamageEvent("player", Boss, "skill", 100))]
        }]
    };
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
