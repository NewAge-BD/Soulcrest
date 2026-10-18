using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatLocalWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-combat-workflow-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Started = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshStoreReplaysAndComparesEveryPartyMemberThenDeletesWholeRuns()
    {
        var left = Run(1, false);
        var right = Run(2, true);
        new CombatHistoryStore(_directory).SaveRun(left);
        new CombatHistoryStore(_directory).SaveRun(right);

        // Use fresh store instances: this must work entirely from local primary records.
        var loadedLeft = new CombatHistoryStore(_directory).LoadRun(left.Id).Run;
        var loadedRight = new CombatHistoryStore(_directory).LoadRun(right.Id).Run;
        var comparison = CombatComparison.Compare(loadedLeft.Attempts.Single(), loadedRight.Attempts.Single());

        Assert.True(comparison.IsComparable);
        Assert.Equal(5, comparison.Participants.Count);
        foreach (var participant in comparison.Participants)
        {
            Assert.Equal(CombatComparisonPresence.Both, participant.Presence);
            Assert.Equal(participant.Damage.Left * 2, participant.Damage.Right);
            Assert.Equal(participant.Damage.Left, participant.Skills.Single().Damage.Difference);
            Assert.NotNull(participant.LeftParticipant!.CombatPower);
        }
        var support = comparison.Participants.Single(p => p.ParticipantId == "member-5");
        Assert.Equal(0.5m, support.Buffs.Single().Uptime.Left);
        Assert.Equal(0.6m, support.Buffs.Single().Uptime.Right);
        Assert.Equal(9, comparison.Left.Timeline.Count);
        Assert.Single(comparison.Left.HpPoints);

        var store = new CombatHistoryStore(_directory);
        Assert.True(store.DeleteRun(left.Id));
        Assert.Equal(right.Id, Assert.Single(new CombatHistoryStore(_directory).LoadIndex().Runs).Id);
        Assert.Throws<DirectoryNotFoundException>(() => new CombatHistoryStore(_directory).LoadRun(left.Id));
        Assert.Equal([right.Id], store.Prune(0));
        Assert.Empty(new CombatHistoryStore(_directory).LoadIndex().Runs);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_directory, "runs")));
    }

    private static CombatRunDocument Run(int multiplier, bool reverse)
    {
        var source = new CombatSourceContext("stream-" + multiplier, 1);
        var boss = new CombatEntityReference("boss-" + multiplier, 1, source.StreamId);
        var participants = Enumerable.Range(1, 5)
            .Select(i => new CombatParticipant("member-" + i, "Player " + i, "class-test", 10000 + i, i == 1)).ToArray();
        var start = Started.AddMinutes(multiplier);
        var events = participants.Select((p, i) => Envelope(i + 1, 1,
            new CombatDamageEvent(p.ParticipantId, boss, "skill-" + (i + 1), (i + 1) * 100 * multiplier))).ToList();
        events.Add(Envelope(6, 2, new CombatBuffEvent("buff-test", CombatBuffAction.Apply,
            "member-5", SourceParticipantId: "member-1", InitialStateKnown: true)));
        events.Add(Envelope(7, multiplier == 1 ? 7 : 8, new CombatBuffEvent("buff-test", CombatBuffAction.Remove,
            "member-5", SourceParticipantId: "member-1", InitialStateKnown: true)));
        events.Add(Envelope(8, 9, new CombatHpEvent(boss, 0, 100000)));
        events.Add(Envelope(9, 9, new CombatDeathEvent("member-2")));
        return new()
        {
            Id = Guid.NewGuid(), DungeonKey = "dungeon-test", InstanceKey = "instance-" + multiplier,
            StartedAtUtc = start, EndedAtUtc = start.AddSeconds(12),
            Attempts = [new()
            {
                Id = Guid.NewGuid(), BossKey = "boss-test", EncounterVariantKey = "normal-test", BossTargets = [boss],
                StartedAtUtc = start, DurationTicks = TimeSpan.FromSeconds(10).Ticks,
                EndReason = CombatEndReason.Victory, Capabilities = CombatCapabilities.Damage | CombatCapabilities.Buffs | CombatCapabilities.Deaths,
                MeasurementProfile = new("boss-damage-v1", "build-test", "decoder-test", "catalog-test"),
                Participants = reverse ? participants.Reverse().ToArray() : participants, Events = events.ToArray()
            }]
        };

        CombatEventEnvelope Envelope(long sequence, double seconds, CombatEvent item) =>
            new(sequence, TimeSpan.FromSeconds(seconds).Ticks, start.AddSeconds(seconds), source, item);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
