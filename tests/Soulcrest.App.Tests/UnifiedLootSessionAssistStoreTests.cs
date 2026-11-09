using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootSessionStoreTests
{
    [Fact]
    public void ConfirmedAndEstimatedAssistDeltasKeepTheirProvenanceAcrossRecovery()
    {
        using var fixture = new Fixture();
        var first = Session(fixture.Id, abyss: true);
        fixture.Store.Append(first);
        var confirmed = Assist(1, estimated: false);
        var estimated = Assist(2, estimated: true);
        var next = first with
        {
            LastPresenceAt = Start.AddSeconds(2), Elapsed = TimeSpan.FromSeconds(2),
            CombatEvents = [confirmed, estimated]
        };
        fixture.Store.Append(next);
        fixture.Store.Append(next with { LastPresenceAt = Start.AddSeconds(3), Elapsed = TimeSpan.FromSeconds(3) });
        var read = new UnifiedLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues);
        Assert.Equal(new[] { confirmed, estimated }, Assert.Single(read.Sessions).CombatEvents.ToArray());
        using var entry = JsonDocument.Parse(File.ReadAllLines(fixture.Journal)[1]);
        var delta = entry.RootElement.GetProperty("CombatEventDelta");
        Assert.False(delta[0].TryGetProperty("IsEstimated", out _));
        Assert.True(delta[1].GetProperty("IsEstimated").GetBoolean());
        Assert.Empty(entry.RootElement.GetProperty("Snapshot").GetProperty("CombatEvents").EnumerateArray());
    }

    [Fact]
    public void ExistingKillAndDeathRecordsWithoutAssistProvenanceRemainConfirmedAndUnchanged()
    {
        using var fixture = new Fixture();
        var session = Session(fixture.Id, abyss: true) with
        {
            LastPresenceAt = Start.AddSeconds(2), Elapsed = TimeSpan.FromSeconds(2),
            CombatEvents = [Assist(1, false) with { Kind = LootTrackerCombatKind.Kill },
                Assist(2, false) with { Kind = LootTrackerCombatKind.Death }]
        };
        fixture.Store.Append(session);
        Assert.DoesNotContain("IsEstimated", File.ReadAllText(fixture.Journal), StringComparison.Ordinal);
        var read = new UnifiedLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues);
        Assert.Equal(new[] { LootTrackerCombatKind.Kill, LootTrackerCombatKind.Death },
            Assert.Single(read.Sessions).CombatEvents.Select(row => row.Kind));
        Assert.All(Assert.Single(read.Sessions).CombatEvents, row => Assert.False(row.IsEstimated));
    }

    [Fact]
    public void LegacyAbyssJournalRemainsKillAndDeathOnlyWithoutReconstructingAssistHistory()
    {
        using var fixture = new Fixture();
        var store = new FarmLootSessionStore(fixture.Root);
        var session = Session(fixture.Id, abyss: true) with
        {
            UnifiedVersion = 0, TimelineVersion = 0,
            LastPresenceAt = Start.AddSeconds(2), Elapsed = TimeSpan.FromSeconds(2),
            CombatEvents = [new(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Kill, Start.AddSeconds(1), TimeSpan.FromSeconds(1)),
                new(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Death, Start.AddSeconds(2), TimeSpan.FromSeconds(2))]
        };
        store.Append(session);
        using var record = JsonDocument.Parse(File.ReadAllLines(fixture.Journal)[0]);
        Assert.Equal(4, record.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.DoesNotContain("IsEstimated", File.ReadAllText(fixture.Journal), StringComparison.Ordinal);
        var read = new FarmLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues);
        Assert.All(Assert.Single(read.Sessions).CombatEvents, row => Assert.False(row.IsEstimated));
        Assert.Throws<InvalidDataException>(() => store.Append(session with
        { CombatEvents = session.CombatEvents.Add(Assist(2, estimated: true) with { Origin = null, OpponentClassId = null, OpponentAbyssRankGrade = null }) }));
    }

    [Theory]
    [InlineData(LootTrackerCombatKind.Kill, true)]
    [InlineData(LootTrackerCombatKind.Death, true)]
    [InlineData((LootTrackerCombatKind)99, false)]
    public void InvalidCombatKindsAndEstimatedNonAssistRecordsCannotBeCommitted(LootTrackerCombatKind kind, bool estimated)
    {
        using var fixture = new Fixture();
        var session = Session(fixture.Id, abyss: true) with
        {
            LastPresenceAt = Start.AddSeconds(1), Elapsed = TimeSpan.FromSeconds(1),
            CombatEvents = [Assist(1, estimated) with { Kind = kind }]
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(session));
        Assert.False(File.Exists(fixture.Journal));
    }

    [Fact]
    public void EstimatedAssistCannotRewriteTheEvidenceOfAnAlreadyCommittedCombatRecord()
    {
        using var fixture = new Fixture();
        var confirmed = Assist(1, estimated: false);
        var session = Session(fixture.Id, abyss: true) with
        {
            LastPresenceAt = Start.AddSeconds(1), Elapsed = TimeSpan.FromSeconds(1), CombatEvents = [confirmed]
        };
        fixture.Store.Append(session);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(session with
        { CombatEvents = [confirmed with { IsEstimated = true }] }));
        Assert.False(Assert.Single(Assert.Single(new UnifiedLootSessionStore(fixture.Root).Read().Sessions).CombatEvents).IsEstimated);
    }

    [Theory]
    [InlineData(LootTrackerActivityOrigin.Pve)]
    [InlineData(LootTrackerActivityOrigin.Lifeskill)]
    [InlineData(null)]
    public void AssistRequiresItsAbyssOriginInThePersistedSession(LootTrackerActivityOrigin? origin)
    {
        using var fixture = new Fixture();
        var session = Session(fixture.Id, abyss: true) with
        {
            LastPresenceAt = Start.AddSeconds(1), Elapsed = TimeSpan.FromSeconds(1),
            CombatEvents = [Assist(1, estimated: true) with { Origin = origin }]
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(session));
    }

    private static LootTrackerCombatEvent Assist(int seconds, bool estimated) =>
        new(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Assist, Start.AddSeconds(seconds), TimeSpan.FromSeconds(seconds))
        { Origin = LootTrackerActivityOrigin.Abyss, IsEstimated = estimated, OpponentClassId = "Ranger", OpponentAbyssRankGrade = 12 };
}
