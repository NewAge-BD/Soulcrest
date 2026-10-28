using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerPresentationTests
{
    [Fact]
    public void LootDisplayUsesRarityThenQuantityNameAndIdWithoutGroupingOrChangingStoredRows()
    {
        var session = new LootTrackerSession
        {
            Loot = [new("unknown-b", "Same", LootTrackerLootKind.Item, 8),
                new("Item:110150003", "Rare gear B", LootTrackerLootKind.Item, 1),
                new("Item:110150004", "Rare gear A", LootTrackerLootKind.Item, 1),
                new("Item:610530001", "Common material", LootTrackerLootKind.Item, 900),
                new("Item:610530005", "Rare material", LootTrackerLootKind.Item, 100),
                new("Item:610530008", "Legend material", LootTrackerLootKind.Item, 1),
                new("Item:514310110", "Epic item", LootTrackerLootKind.Item, 1),
                new("Item:210230006", "Unique item", LootTrackerLootKind.Item, 5),
                new("Item:310900001", "Special item", LootTrackerLootKind.Item, 1),
                new("unknown-a", "Same", LootTrackerLootKind.Item, 8),
                new("SoulCrystal:2", "Crystal", LootTrackerLootKind.Soul, 1),
                new("unknown-z", "Alpha", LootTrackerLootKind.Item, 8)],
        };
        var before = session.Loot.ToArray();
        var ordered = LootTrackerPresentation.OrderedLoot(session);
        Assert.Equal(new[] { "Item:310900001", "Item:514310110", "Item:210230006", "Item:610530008",
            "Item:610530005", "Item:110150004", "Item:110150003", "Item:610530001", "unknown-z", "unknown-a", "unknown-b", "SoulCrystal:2" },
            ordered.Select(row => row.Id));
        Assert.Equal(before, session.Loot.ToArray());
        Assert.Equal(session.Loot.Length, ordered.Length);
        Assert.Equal(before.OrderBy(row => row.Id), ordered.OrderBy(row => row.Id));
        Assert.Equal(ordered.ToArray(), LootTrackerPresentation.OrderedLoot(session with { Loot = Enumerable.Reverse(ordered).ToImmutableArray() }).ToArray());
        Assert.Empty(LootTrackerPresentation.OrderedLoot(new LootTrackerSession()));
        Assert.Empty(LootTrackerPresentation.OrderedLoot(session with { Loot = default }));
    }

    [Fact]
    public void MissingSettingUsesTwoMinutesAndExplicitSettingRoundTrips()
    {
        Assert.Equal(2, JsonSerializer.Deserialize<AppSettings>("{}")!.LootTrackerIdleTimeoutMinutes);
        var settings = new AppSettings { LootTrackerIdleTimeoutMinutes = 7 };
        Assert.Equal(7, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.LootTrackerIdleTimeoutMinutes);
    }

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(int.MaxValue, 60)]
    public void IdleTimeoutIsBoundedWithoutTouchingUserSettings(int input, int expected) =>
        Assert.Equal(expected, LootTrackerPresentation.NormalizeIdleTimeoutMinutes(input));

    [Fact]
    public void GrindNameUsesItsLocationAndDungeonKeepsItsOwnName()
    {
        var grind = new LootTrackerSession
        {
            Location = "Immortal Isles",
            Mobs = [new("another", "Other monster", 2), new("worker", "Kuru Worker", 11)],
        };
        Assert.Equal("Immortal Isles", LootTrackerPresentation.SessionName(grind));
        Assert.Equal("Fire Temple", LootTrackerPresentation.SessionName(grind with
        {
            Kind = LootTrackerSessionKind.Dungeon, DungeonName = "Fire Temple",
        }));
        Assert.Equal("Immortal Isles", LootTrackerPresentation.SessionName(grind with { Mobs = [] }));
        Assert.Null(LootTrackerPresentation.SessionName(new LootTrackerSession()));
    }

    [Fact]
    public void MissingComponentAndZeroElapsedNeverBecomeZeroIncomeOrRate()
    {
        var session = new LootTrackerSession { KinaBound = 0, KinaUnbound = 120 };
        Assert.Equal(120, LootTrackerPresentation.TotalKina(session));
        Assert.Equal(240, LootTrackerPresentation.PerHour(120, TimeSpan.FromMinutes(30)));
        Assert.Null(LootTrackerPresentation.TotalKina(session with { KinaBound = null }));
        Assert.Null(LootTrackerPresentation.TotalKina(session with { KinaBound = long.MaxValue }));
        Assert.Null(LootTrackerPresentation.PerHour(null, TimeSpan.FromMinutes(30)));
        Assert.Null(LootTrackerPresentation.PerHour(120, TimeSpan.Zero));
        Assert.Null(LootTrackerPresentation.PerHour(-1, TimeSpan.FromMinutes(30)));
        Assert.Equal(0, LootTrackerPresentation.PerHour(0, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void EmptySnapshotContainsNoSyntheticSessionAndDoesNotClaimACurrentCapture()
    {
        Assert.Empty(LootTrackerSnapshot.Empty.Sessions);
        Assert.Null(LootTrackerSnapshot.Empty.At);
        var session = new LootTrackerSession();
        Assert.Null(session.Character);
        Assert.Null(session.CombatScore);
        Assert.Null(session.KinaBound);
        Assert.Null(session.KinaUnbound);
        Assert.Null(session.MonsterCount);
        Assert.Null(session.ItemCount);
        Assert.Null(session.SoulCount);
        Assert.Empty(session.RouteSegments);
        Assert.Empty(session.ResourceNodes);
        Assert.Empty(session.MinuteIncome);
    }

    [Fact]
    public void CharacterSnapshotKeepsProfileNameAndScoresWhenProfileChanges()
    {
        var firstProfileId = Guid.NewGuid().ToString("N");
        var profile = new CharacterExploration { Id = firstProfileId, Name = "Example Ranger" };
        var firstSession = new LootTrackerSession
        {
            Character = LootTrackerPresentation.CaptureCharacter(profile, 1840, 1260),
        };

        profile.Name = "Renamed Ranger";
        var renamedSession = new LootTrackerSession
        {
            Character = LootTrackerPresentation.CaptureCharacter(profile, 2000, 1300),
        };
        profile.Id = Guid.NewGuid().ToString("N");
        profile.Name = "Example Cleric";
        var secondSession = new LootTrackerSession
        {
            Character = LootTrackerPresentation.CaptureCharacter(profile, 2500, 1500),
        };

        Assert.Equal(firstProfileId, LootTrackerPresentation.CharacterProfileId(firstSession));
        Assert.Equal(firstProfileId, LootTrackerPresentation.CharacterProfileId(renamedSession));
        Assert.NotEqual(firstProfileId, LootTrackerPresentation.CharacterProfileId(secondSession));
        Assert.Equal("Example Ranger", LootTrackerPresentation.CharacterName(firstSession));
        Assert.Equal("Renamed Ranger", LootTrackerPresentation.CharacterName(renamedSession));
        Assert.Equal("Example Cleric", LootTrackerPresentation.CharacterName(secondSession));
        Assert.Equal(1840, LootTrackerPresentation.CombatScore(firstSession));
        Assert.Equal(1260, LootTrackerPresentation.GearScore(firstSession));
        Assert.Equal(2000, LootTrackerPresentation.CombatScore(renamedSession));
        Assert.Equal(1300, LootTrackerPresentation.GearScore(renamedSession));
    }

    [Fact]
    public void StartMetadataRoundTripsWithoutCurrentProfileOrNetworkIdentifiers()
    {
        var session = new LootTrackerSession
        {
            Character = new(Guid.NewGuid().ToString("N"), "Example Ranger", 1840, 1260),
        };
        var restored = JsonSerializer.Deserialize<LootTrackerSession>(JsonSerializer.Serialize(session))!;
        Assert.Equal(session.Character, restored.Character);
        Assert.Equal(LootTrackerPresentation.CharacterProfileId(session), LootTrackerPresentation.CharacterProfileId(restored));
        Assert.Equal(1840, LootTrackerPresentation.CombatScore(restored));
        Assert.Equal(1260, LootTrackerPresentation.GearScore(restored));
    }

    [Fact]
    public void LegacyScoreIsUsedOnlyWithoutCharacterSnapshotAndZeroIsARealScore()
    {
        var legacy = JsonSerializer.Deserialize<LootTrackerSession>("{\"CombatScore\":1840}")!;
        Assert.Null(legacy.Character);
        Assert.Null(LootTrackerPresentation.CharacterName(legacy));
        Assert.Null(LootTrackerPresentation.CharacterProfileId(legacy));
        Assert.Equal(1840, LootTrackerPresentation.CombatScore(legacy));
        Assert.Null(LootTrackerPresentation.GearScore(legacy));

        var unknown = legacy with { Character = new(null, null) };
        Assert.Null(LootTrackerPresentation.CombatScore(unknown));
        Assert.Null(LootTrackerPresentation.GearScore(unknown));
        var zero = unknown with { Character = new(null, null, 0, 0) };
        Assert.Equal(0, LootTrackerPresentation.CombatScore(zero));
        Assert.Equal(0, LootTrackerPresentation.GearScore(zero));
        var negative = unknown with { Character = new(null, null, -1, -1) };
        Assert.Null(LootTrackerPresentation.CombatScore(negative));
        Assert.Null(LootTrackerPresentation.GearScore(negative));
    }

    [Fact]
    public void CharacterFilterUsesCanonicalLocalGuidAndNeverNameOrOpaqueNetworkKey()
    {
        var guid = Guid.NewGuid();
        var session = new LootTrackerSession { Character = new(guid.ToString("D"), "Example Ranger") };
        Assert.Equal(guid.ToString("N"), LootTrackerPresentation.CharacterProfileId(session));
        Assert.Null(LootTrackerPresentation.CharacterProfileId(session with { Character = new("123456789", "Example Ranger") }));
        Assert.Null(LootTrackerPresentation.CharacterProfileId(session with { Character = new(null, "Example Ranger") }));
        Assert.Null(LootTrackerPresentation.CharacterName(session with { Character = new(null, "  ") }));
        var captured = LootTrackerPresentation.CaptureCharacter(new CharacterExploration
        {
            Id = guid.ToString("D"), Name = " Example Ranger ", NetworkCharacterId = 123456789, ServerId = 1,
        }, -1, -1);
        Assert.Equal(guid.ToString("N"), captured.ProfileId);
        Assert.Equal("Example Ranger", captured.Name);
        Assert.Null(captured.CombatScore);
        Assert.Null(captured.GearScore);
    }

    [Fact]
    public void LifeskillUsesLocationWithoutTurningMonsterCountsIntoResourceNames()
    {
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill,
            Location = "Immortal Isles",
            Mobs = [new("worker", "Kuru Worker", 11)],
        };
        Assert.Equal("Immortal Isles", LootTrackerPresentation.SessionName(session));
        Assert.Null(LootTrackerPresentation.SessionName(session with { Location = "" }));
    }

    [Fact]
    public void ResourceVisitsKeepEncounterOrderAndTheirOwnSegmentAcrossGaps()
    {
        var started = DateTimeOffset.Parse("2026-10-25T10:00:00Z");
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill,
            RouteSegments =
            [
                new("map-a", "Example area", [new(100, 200), new(150, 250)]),
                new("map-a", "Example area", [new(300, 400)]),
                new("map-b", "Other area", [new(100, 200)]),
            ],
            ResourceNodes =
            [
                new("node-a", "Example resource", "map-a", "Example area", 105, 205, 0, 0, started),
                new("node-b", "Other resource", "map-a", "Example area", 300, 400, 1, 0, started.AddMinutes(1)),
                new("node-a", "Example resource", "map-b", "Other area", 100, 200, 2, 0, started.AddMinutes(2)),
            ],
        };
        var visits = LootTrackerPresentation.GetValidatedResourceVisits(session);
        Assert.Equal(session.ResourceNodes.ToArray(), visits.ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, visits.Select(visit => visit.SegmentIndex));
        var restored = JsonSerializer.Deserialize<LootTrackerSession>(JsonSerializer.Serialize(session))!;
        Assert.Equal(visits.ToArray(), LootTrackerPresentation.GetValidatedResourceVisits(restored).ToArray());
    }

    [Fact]
    public void ResourceVisitsRejectUnknownNodeMapInvalidReferencesAndInvalidCoordinates()
    {
        var valid = new LootTrackerResourceVisit("node-a", "Example resource", "map-a", "Example area", 100, 200, 0, 0);
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill,
            RouteSegments = [new("map-a", "Example area", [new(100, 200)])],
            ResourceNodes =
            [
                valid with { NodeId = "" },
                valid with { MapId = "map-b" },
                valid with { SegmentIndex = -1 },
                valid with { SegmentIndex = 1 },
                valid with { PointIndex = -1 },
                valid with { PointIndex = 1 },
                valid with { X = -1 },
                valid with { X = double.NaN },
                valid with { Y = double.PositiveInfinity },
                valid,
            ],
        };
        Assert.Equal(new[] { valid }, LootTrackerPresentation.GetValidatedResourceVisits(session).ToArray());
        Assert.Empty(LootTrackerPresentation.GetValidatedResourceVisits(session with
        {
            RouteSegments = [new("map-a", "Example area", [new(-100, 200)])],
            ResourceNodes = [valid],
        }));
        Assert.Empty(LootTrackerPresentation.GetValidatedResourceVisits(session with
        {
            RouteSegments = [new("map-a", "Example area", [new(100, -200)])],
            ResourceNodes = [valid],
        }));
        Assert.Empty(LootTrackerPresentation.GetValidatedResourceVisits(session with
        {
            RouteSegments = [new("map-a", "Example area", [new(double.PositiveInfinity, 200)])],
            ResourceNodes = [valid],
        }));
        Assert.Empty(LootTrackerPresentation.GetValidatedResourceVisits(session with { RouteSegments = [] }));
        Assert.Empty(LootTrackerPresentation.GetValidatedResourceVisits(new LootTrackerSession()));
    }
}
