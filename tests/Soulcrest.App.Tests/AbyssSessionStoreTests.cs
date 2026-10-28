using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssSessionStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VersionFourRecoversQuietResidenceThroughPresenceRatherThanLastLoot()
    {
        using var f = new Fixture();
        var live = Session(f.Id) with
        {
            LastLootAt = At.AddMinutes(1), LastPresenceAt = At.AddMinutes(10), Elapsed = TimeSpan.FromMinutes(10),
            AbyssPoints = 100, RankPointsGained = 10, RankPointsLost = 30,
            MinuteIncome = [new(1, null, null) { AbyssPoints = 100, RankPointsGained = 10, RankPointsLost = 30 }]
        };
        f.Store.Append(live);
        using var journal = JsonDocument.Parse(File.ReadAllLines(f.Journal).Single());
        Assert.Equal(4, journal.RootElement.GetProperty("SchemaVersion").GetInt32());
        var read = new FarmLootSessionStore(f.Root).Read(); Assert.Empty(read.Issues);
        var recovered = Assert.Single(read.Sessions);
        Assert.Equal(At.AddMinutes(10), recovered.EndedAt); Assert.Equal(TimeSpan.FromMinutes(10), recovered.Elapsed);
        Assert.Equal(-20, LootTrackerPresentation.NetRankPoints(recovered));
        Assert.Equal(100, Assert.Single(recovered.MinuteIncome).AbyssPoints);
    }

    [Fact]
    public void QuietEntryWithNoLootIsRecoveredAndCannotBeDisguisedAsALegacyJournal()
    {
        using var f = new Fixture(); f.Store.Append(Session(f.Id));
        Assert.Equal(At, Assert.Single(new FarmLootSessionStore(f.Root).Read().Sessions).EndedAt);
        File.WriteAllText(f.Journal, File.ReadAllText(f.Journal).Replace("\"SchemaVersion\":4", "\"SchemaVersion\":2", StringComparison.Ordinal));
        var read = new FarmLootSessionStore(f.Root).Read();
        Assert.Empty(read.Sessions); Assert.Contains("invalid-farm-journal-record", read.Issues);
    }

    [Fact]
    public void ApAndRankAccountingCannotGoBackwardAndPresenceCannotPrecedeLoot()
    {
        using var f = new Fixture(); var initial = Session(f.Id) with { AbyssPoints = 10, RankPointsGained = 5, RankPointsLost = 20 };
        f.Store.Append(initial);
        Assert.Throws<InvalidDataException>(() => f.Store.Append(initial with { AbyssPoints = 9 }));
        Assert.Throws<InvalidDataException>(() => f.Store.Append(initial with { RankPointsGained = 4 }));
        Assert.Throws<InvalidDataException>(() => f.Store.Append(initial with { RankPointsLost = 19 }));
        Assert.Throws<InvalidDataException>(() => f.Store.Append(initial with { LastLootAt = At.AddSeconds(1) }));
        Assert.Single(File.ReadAllLines(f.Journal));
    }

    [Fact]
    public void ExistingKindsCannotPersistAbyssFieldsOrLimitedModeForAbyss()
    {
        using var f = new Fixture();
        Assert.Throws<InvalidDataException>(() => f.Store.Append(Session(f.Id) with { Kind = LootTrackerSessionKind.Grind }));
        Assert.Throws<InvalidDataException>(() => f.Store.Append(Session(f.Id) with { Mode = LootTrackerSessionMode.LimitedKina }));
        Assert.Throws<InvalidDataException>(() => f.Store.Append(Session(f.Id) with { AbyssPoints = -1 }));
    }

    [Fact]
    public void VersionFourAppendsOnlyNewMarkersAndRecoversTheCompletePrefixOnce()
    {
        using var f = new Fixture(); var initial = Session(f.Id);
        f.Store.Append(initial);
        var kill = new LootTrackerCombatEvent(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Kill,
            At.AddSeconds(1), TimeSpan.FromSeconds(1), new("test-map", "Test map", 100, 200, At.AddSeconds(1)));
        var visit = new LootTrackerGatherVisit(Guid.NewGuid().ToString("N"), 0, "test-map", "Test map",
            100, 200, 105, 200, At.AddSeconds(1), true, "Odyle", 8192, 123, "odyle-node");
        var first = initial with { CombatEvents = [kill], GatherVisits = [visit], LastPresenceAt = At.AddSeconds(1), Elapsed = TimeSpan.FromSeconds(1) };
        f.Store.Append(first);
        var death = new LootTrackerCombatEvent(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Death,
            At.AddSeconds(2), TimeSpan.FromSeconds(2));
        var last = first with { CombatEvents = [kill, death], LastPresenceAt = At.AddSeconds(2), Elapsed = TimeSpan.FromSeconds(2) };
        f.Store.Append(last); f.Store.Append(last);
        var lines = File.ReadAllLines(f.Journal).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        Assert.Equal(new[] { 0, 1, 1, 0 }, lines.Select(line => line["CombatEventDelta"]!.AsArray().Count));
        Assert.Equal(new[] { 0, 1, 0, 0 }, lines.Select(line => line["HarvestVisitDelta"]!.AsArray().Count));
        Assert.All(lines, line => { Assert.Empty(line["Snapshot"]!["CombatEvents"]!.AsArray()); Assert.Empty(line["Snapshot"]!["GatherVisits"]!.AsArray()); });
        File.AppendAllText(f.Journal, "{\"SchemaVersion\":4");
        var read = new FarmLootSessionStore(f.Root).Read(); var recovered = Assert.Single(read.Sessions);
        Assert.Contains("incomplete-farm-journal-tail", read.Issues); Assert.Equal(new[] { kill, death }, recovered.CombatEvents);
        Assert.Equal(visit, Assert.Single(recovered.GatherVisits)); Assert.Null(recovered.CombatEvents[1].Position);
        Assert.Equal(TimeSpan.FromSeconds(2), recovered.Elapsed);
        Assert.DoesNotContain("Opponent", File.ReadAllText(f.Journal));
        Assert.DoesNotContain("Native", File.ReadAllText(f.Journal));
    }

    [Fact]
    public void LegacyVersionThreeRemainsReadableWithoutReconstructingHistoricalMarkers()
    {
        using var f = new Fixture(); f.Store.Append(Session(f.Id) with { RankPointsGained = 100, RankPointsLost = 20, AbyssPoints = 100 });
        var legacy = JsonNode.Parse(File.ReadAllLines(f.Journal).Single())!.AsObject();
        legacy["SchemaVersion"] = 3; legacy.Remove("CombatEventDelta"); legacy.Remove("HarvestVisitDelta");
        legacy["Snapshot"]!.AsObject().Remove("CombatEvents");
        File.WriteAllText(f.Journal, legacy.ToJsonString() + "\n");
        var read = new FarmLootSessionStore(f.Root).Read(); Assert.Empty(read.Issues);
        var session = Assert.Single(read.Sessions); Assert.Empty(session.CombatEvents); Assert.Empty(session.GatherVisits);
        Assert.Equal(80, LootTrackerPresentation.NetRankPoints(session));
    }

    [Fact]
    public void LegacyVersionThreeCanBeExtendedByVersionFourWithoutInventingEarlierOutcomes()
    {
        using var f = new Fixture(); var initial = Session(f.Id); f.Store.Append(initial);
        var legacy = JsonNode.Parse(File.ReadAllLines(f.Journal).Single())!.AsObject(); legacy["SchemaVersion"] = 3;
        legacy.Remove("CombatEventDelta"); legacy.Remove("HarvestVisitDelta");
        legacy["Snapshot"]!.AsObject().Remove("CombatEvents"); File.WriteAllText(f.Journal, legacy.ToJsonString() + "\n");
        var marker = new LootTrackerCombatEvent(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Kill, At, TimeSpan.Zero);
        new FarmLootSessionStore(f.Root).Append(initial with { CombatEvents = [marker] });
        var read = new FarmLootSessionStore(f.Root).Read(); Assert.Empty(read.Issues);
        Assert.Equal(marker, Assert.Single(Assert.Single(read.Sessions).CombatEvents));
        Assert.Equal(new[] { 3, 4 }, File.ReadAllLines(f.Journal).Select(line => JsonNode.Parse(line)!["SchemaVersion"]!.GetValue<int>()));
    }

    [Fact]
    public void MarkersAreAppendOnlyAndCannotBeDuplicatedOrMovedIntoOtherSessionKinds()
    {
        using var f = new Fixture();
        var marker = new LootTrackerCombatEvent(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Death, At, TimeSpan.Zero);
        var first = Session(f.Id) with { CombatEvents = [marker] }; f.Store.Append(first);
        Assert.Throws<InvalidDataException>(() => f.Store.Append(first with { CombatEvents = [] }));
        Assert.Throws<InvalidDataException>(() => f.Store.Append(first with { CombatEvents = [marker with { Kind = LootTrackerCombatKind.Kill }] }));
        Assert.Throws<InvalidDataException>(() => f.Store.Append(first with { CombatEvents = [marker, marker] }));
        Assert.Throws<InvalidDataException>(() => f.Store.Append(first with { Id = Guid.NewGuid().ToString("N"), Kind = LootTrackerSessionKind.Grind, LastPresenceAt = null }));
        Assert.Single(File.ReadAllLines(f.Journal));
    }

    [Theory]
    [InlineData("future")]
    [InlineData("elapsed")]
    [InlineData("kind")]
    [InlineData("stale-position")]
    [InlineData("future-position")]
    [InlineData("nan")]
    public void InvalidMarkerAuthorityOrCoordinatesCannotCommit(string invalid)
    {
        using var f = new Fixture();
        var marker = new LootTrackerCombatEvent(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Kill,
            At, TimeSpan.Zero, new("test-map", "Test map", 100, 200, At));
        marker = invalid switch
        {
            "future" => marker with { At = At.AddSeconds(1) },
            "elapsed" => marker with { Elapsed = TimeSpan.FromSeconds(1) },
            "kind" => marker with { Kind = (LootTrackerCombatKind)999 },
            "stale-position" => marker with { Position = marker.Position! with { ObservedAt = At.AddSeconds(-3) } },
            "future-position" => marker with { Position = marker.Position! with { ObservedAt = At.AddSeconds(1) } },
            _ => marker with { Position = marker.Position! with { X = double.NaN } }
        };
        Assert.Throws<InvalidDataException>(() => f.Store.Append(Session(f.Id) with { CombatEvents = [marker] }));
        Assert.False(File.Exists(f.Journal));
    }

    [Fact]
    public void VersionFourRequiresDeltasAndCannotDowngradeAfterMarkers()
    {
        using var f = new Fixture(); var first = Session(f.Id); f.Store.Append(first); f.Store.Append(first);
        var lines = File.ReadAllLines(f.Journal);
        var second = JsonNode.Parse(lines[1])!.AsObject(); second["SchemaVersion"] = 3;
        second.Remove("CombatEventDelta"); second.Remove("HarvestVisitDelta");
        File.WriteAllText(f.Journal, lines[0] + "\n" + second.ToJsonString() + "\n");
        var read = new FarmLootSessionStore(f.Root).Read(); Assert.Contains("invalid-farm-journal-record", read.Issues);
        Assert.Single(read.Sessions);
        var missing = JsonNode.Parse(lines[0])!.AsObject(); missing.Remove("CombatEventDelta");
        File.WriteAllText(f.Journal, missing.ToJsonString() + "\n");
        Assert.Empty(new FarmLootSessionStore(f.Root).Read().Sessions);
    }

    private static LootTrackerSession Session(string id) => new()
    {
        Id = id, Kind = LootTrackerSessionKind.Abyss, Status = LootTrackerSessionStatus.Live,
        StartedAt = At, LastPresenceAt = At, Character = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Synthetic character")
    };
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-abyss-journal-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public FarmLootSessionStore Store { get; }
        public string Journal => Path.Combine(Root, Id, "events-0001.jsonl");
        public Fixture() => Store = new(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
