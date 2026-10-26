using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class BossNavigationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(23)]
    public void AllSpawnedBossesBranchFromThePlayerAndUpcomingSpawnsUseAnIndependentChain(int previewCount)
    {
        BossRushEntry[] rows = [Entry(111001, spawned: true), Entry(111002, spawned: true),
            .. Enumerable.Range(111003, 24).Select((id, index) => Entry(id, seconds: 60 + index))];

        var targets = BossRushService.NavigationTargets(rows, previewCount, "en");

        Assert.Equal(3 + previewCount, targets.Count);
        var spawned = targets.Where(target => target.BossRole == "spawned").ToArray();
        Assert.Equal(new int?[] { 111001, 111002 }, spawned.Select(target => target.BossSpawnId));
        Assert.All(spawned, target =>
        {
            Assert.Null(target.After);
            Assert.Equal("#4ade80", target.Color);
            Assert.False(target.BossFaded);
        });
        var next = Assert.Single(targets, target => target.BossRole == "next");
        Assert.Equal(111003, next.BossSpawnId);
        Assert.Equal("boss:1110:111003", next.Id);
        Assert.Null(next.After);
        Assert.Equal("#ef4444", next.Color);
        Assert.False(next.BossFaded);
        var previews = targets.Where(target => target.BossRole == "preview").ToArray();
        Assert.Equal(previewCount, previews.Length);
        for (var index = 0; index < previews.Length; index++)
        {
            Assert.Equal($"boss:1110:{111003 + index}", previews[index].After);
            Assert.Equal("#ef4444", previews[index].Color);
            Assert.True(previews[index].BossFaded);
        }
        Assert.All(targets, target =>
        {
            Assert.True(target.IsBossNavigation);
            Assert.False(target.BossProvisional);
        });
    }

    [Theory]
    [InlineData("de", "Synthetischer Boss 111001")]
    [InlineData("en", "Synthetic boss 111001")]
    public void CachedBossesRemainGreenAndUnconfirmedWithTheSameChronologicalRoles(string language, string name)
    {
        BossRushEntry[] rows = [Entry(111001, spawned: true) with { Cached = true },
            Entry(111002, seconds: 0) with { Cached = true }, Entry(111003) with { Cached = true }];

        var targets = BossRushService.NavigationTargets(rows, 1, language);

        Assert.Equal(new[] { "spawned", "next", "preview" }, targets.Select(target => target.BossRole));
        Assert.Equal(name, targets[0].Name);
        Assert.All(targets, target =>
        {
            Assert.True(target.BossProvisional);
            Assert.True(target.BossFaded);
            Assert.Equal("#4ade80", target.Color);
        });
        Assert.Equal("boss:1110:111002", targets[2].After);
    }

    [Theory]
    [InlineData(111001)]
    [InlineData(111002)]
    public void MissingCoordinatesDoNotPromoteAnotherBossOrReconnectTheBrokenChain(int missingSpawnId)
    {
        var rows = Enumerable.Range(111001, 3).Select(id => Entry(id, located: id != missingSpawnId)).ToArray();

        var targets = BossRushService.NavigationTargets(rows, 2, "en");

        Assert.Equal(2, targets.Count);
        Assert.DoesNotContain(targets, target => target.BossSpawnId == missingSpawnId);
        var third = Assert.Single(targets, target => target.BossSpawnId == 111003);
        Assert.Equal("preview", third.BossRole);
        Assert.Equal("boss:1110:111002", third.After);
        if (missingSpawnId == 111001)
        {
            Assert.DoesNotContain(targets, target => target.BossRole == "next");
            Assert.Equal("boss:1110:111001", targets[0].After);
        }
        else
        {
            Assert.Null(Assert.Single(targets, target => target.BossRole == "next").After);
            Assert.DoesNotContain(targets, target => target.Id == third.After);
        }
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(1, double.NaN)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(1, double.NegativeInfinity)]
    public void NonFiniteBossCoordinatesRetainTheMissingNextSpawnIdentity(double x, double y)
    {
        var first = Entry(111001);
        first = first with { Place = first.Place with { Boss = first.Place.Boss with { X = x, Y = y } } };

        var target = Assert.Single(BossRushService.NavigationTargets([first, Entry(111002)], 1, "en"));

        Assert.Equal("preview", target.BossRole);
        Assert.Equal("boss:1110:111001", target.After);
    }

    [Fact]
    public void ZeroCountdownAndElapsedTimeDoNotChangeNavigationGeometryOrConfirmASpawn()
    {
        BossRushEntry[] original = [Entry(111001, seconds: 1), Entry(111002, seconds: 61)];
        var later = original.Select(row => row with { Remaining = TimeSpan.FromSeconds(Math.Max(0, row.Remaining.TotalSeconds - 2)), Age = TimeSpan.FromSeconds(2) }).ToArray();

        var first = BossRushService.NavigationTargets(original, 1, "en");
        var expired = BossRushService.NavigationTargets(later, 1, "en");

        Assert.Equal(first, expired);
        Assert.Equal("next", expired[0].BossRole);
        Assert.False(expired[0].BossProvisional);
        Assert.Equal("#ef4444", expired[0].Color);
        Assert.DoesNotContain(expired, target => target.BossRole == "spawned");
        var confirmed = BossRushService.NavigationTargets([later[0] with { Spawned = true, SpawnElapsed = TimeSpan.Zero }, later[1]], 1, "en");
        Assert.Equal("spawned", confirmed[0].BossRole);
        Assert.Equal("#4ade80", confirmed[0].Color);
        Assert.Null(confirmed[1].After);
    }

    [Fact]
    public void SnapshotReplacementPublishesTheWholeImmutableListOnceAndIgnoresEquivalentTimerTicks()
    {
        using var targets = new MapTargetsService();
        var savedMarks = targets.Targets.ToArray();
        var progression = new MapTarget("progression", "altgard", 1, 2, "Synthetic progression", "Pets", null);
        targets.SetProgression(progression);
        List<string[]> seen = [];
        targets.Changed += () => seen.Add(targets.BossNavigationTargets.Select(target => target.Id).ToArray());
        var input = BossRushService.NavigationTargets([Entry(111001, spawned: true), Entry(111002), Entry(111003)], 1, "en").ToArray();

        targets.SetBossNavigationTargets(input);
        var snapshot = targets.BossNavigationTargets;
        Assert.Equal(new[] { "boss:1110:111001", "boss:1110:111002", "boss:1110:111003" }, Assert.Single(seen));
        input[0] = input[0] with { Name = "Changed source array" };
        Assert.NotEqual(input[0].Name, snapshot[0].Name);
        Assert.True(Assert.IsAssignableFrom<ICollection<MapTarget>>(snapshot).IsReadOnly);
        targets.SetBossNavigationTargets(snapshot.Select(target => target with { }).ToArray());
        Assert.Same(snapshot, targets.BossNavigationTargets);
        Assert.Single(seen);
        targets.SetBossNavigationTargets([]);
        Assert.Equal(2, seen.Count);
        Assert.Empty(seen[1]);
        Assert.Equal(savedMarks, targets.Targets);
        Assert.Same(progression, targets.Progression);
        Assert.Equal(3, snapshot.Count); // Readers retaining the old snapshot see a complete old scene.
    }

    [Fact]
    public void SerializedBossMetadataIdentifiesRolesWithoutEncodingDynamicTimerText()
    {
        var target = Assert.Single(BossRushService.NavigationTargets([Entry(111001)], 0, "en"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(target));
        Assert.Equal(111001, json.RootElement.GetProperty("bossSpawnId").GetInt32());
        Assert.Equal("next", json.RootElement.GetProperty("bossRole").GetString());
        Assert.False(json.RootElement.GetProperty("bossProvisional").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("remaining", out _));
        Assert.False(json.RootElement.TryGetProperty("IsBossNavigation", out _));
        Assert.False(json.RootElement.TryGetProperty("BossFaded", out _));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(59999, true)]
    [InlineData(60000, true)]
    [InlineData(60001, false)]
    [InlineData(-1, false)]
    public void SpawnedNavigationUsesTheInclusiveActualFirstMinuteWithoutFilteringUpcomingSpawns(int elapsedMilliseconds, bool visible)
    {
        var spawned = Entry(111001, spawned: true) with { SpawnElapsed = TimeSpan.FromMilliseconds(elapsedMilliseconds) };
        var upcoming = Entry(111002, seconds: 120);

        var targets = BossRushService.NavigationTargets([spawned, upcoming], 0, "en");

        Assert.Equal(visible ? 2 : 1, targets.Count);
        Assert.Equal(visible, targets.Any(target => target.BossSpawnId == 111001));
        var next = Assert.Single(targets, target => target.BossRole == "next");
        Assert.Equal(111002, next.BossSpawnId);
        Assert.Null(next.After);
        Assert.Equal("#ef4444", next.Color);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingSpawnElapsedNeverFallsBackToTheListObservationAge(bool cached)
    {
        var spawned = Entry(111001, spawned: true) with { SpawnElapsed = null, Age = TimeSpan.Zero, Cached = cached };

        Assert.Empty(BossRushService.NavigationTargets([spawned], 1, "en"));
    }

    [Fact]
    public void ObservationAgeAndLiveCounterTicksDoNotRebuildTheSpawnGeometryInsideTheWindow()
    {
        var first = Entry(111001, spawned: true) with { SpawnElapsed = TimeSpan.FromSeconds(10), Age = TimeSpan.FromDays(1) };
        var later = first with { SpawnElapsed = TimeSpan.FromSeconds(59), Age = TimeSpan.FromDays(1) + TimeSpan.FromSeconds(49) };

        Assert.Equal(Assert.Single(BossRushService.NavigationTargets([first], 1, "en")),
            Assert.Single(BossRushService.NavigationTargets([later], 1, "en")));
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(false, 60, true)]
    [InlineData(false, 61, false)]
    [InlineData(false, 3600, false)]
    [InlineData(false, -1, false)]
    [InlineData(false, null, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 60, true)]
    [InlineData(true, 61, true)]
    [InlineData(true, 3600, true)]
    [InlineData(true, -1, false)]
    [InlineData(true, null, false)]
    public void OlderSpawnOptInRemovesOnlyTheUpperAgeLimit(bool showOlderSpawns, int? elapsedSeconds, bool visible)
    {
        var spawned = Entry(111001, spawned: true) with
        {
            SpawnElapsed = elapsedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null
        };

        var targets = BossRushService.NavigationTargets([spawned, Entry(111002)], 0, "en", showOlderSpawns);

        Assert.Equal(visible, targets.Any(target => target.BossSpawnId == 111001));
        var next = Assert.Single(targets, target => target.BossRole == "next");
        Assert.Equal(111002, next.BossSpawnId);
        Assert.Null(next.After);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldCachedSpawnOptInKeepsTheMarkerUnconfirmed(bool showOlderSpawns)
    {
        var cached = Entry(111001, spawned: true) with { Cached = true, SpawnElapsed = TimeSpan.FromHours(1) };

        var targets = BossRushService.NavigationTargets([cached], 1, "en", showOlderSpawns);

        if (!showOlderSpawns) Assert.Empty(targets);
        else
        {
            var target = Assert.Single(targets);
            Assert.Equal("spawned", target.BossRole);
            Assert.True(target.BossProvisional);
            Assert.True(target.BossFaded);
            Assert.Equal("#4ade80", target.Color);
        }
    }

    private static BossRushEntry Entry(int spawnId, bool spawned = false, int seconds = 60, bool located = true) =>
        new(new(1110, "altgard", new(spawnId, 2400017, $"Synthetic boss {spawnId}", $"Synthetischer Boss {spawnId}",
            located ? spawnId : null, located ? spawnId + 100 : null)), spawned, TimeSpan.FromSeconds(seconds), TimeSpan.Zero,
            SpawnElapsed: spawned ? TimeSpan.FromSeconds(20) : null);
}
