using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class BossTimerCacheTests : IDisposable
{
    private const long ServerNow = 1791547200000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-boss-cache-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private string CachePath => Path.Combine(_folder, "boss-timers.json");

    public BossTimerCacheTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void MissingCacheHasNoSchedule()
    {
        Assert.Null(new BossTimerCache(CachePath, _time).Load());
        Assert.Empty(new BossTimerCache(CachePath, _time).LoadAll());
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public void RestartRestoresProvisionalListAndAdvancesServerTimeAndObservationAge()
    {
        var snapshot = Snapshot() with { Age = TimeSpan.FromSeconds(2) };
        Assert.True(new BossTimerCache(CachePath, _time).Save(snapshot));
        _time.Advance(TimeSpan.FromMinutes(2));
        var restored = new BossTimerCache(CachePath, _time).Load();
        Assert.NotNull(restored);
        Assert.True(restored.Cached);
        Assert.Equal(snapshot.MapId, restored.MapId);
        Assert.Equal(snapshot.Bosses.OrderBy(b => b.SpawnId), restored.Bosses);
        Assert.Equal(ServerNow + 120000, restored.ServerUnixMs);
        Assert.Equal(TimeSpan.FromSeconds(122), restored.Age);
        Assert.False(restored.Bosses[0].Spawned);
    }

    [Fact]
    public void MissingTimesRoundTripWithoutLosingTheValidBossOrInventingAnEpochTime()
    {
        var bosses = Enumerable.Range(2001, 8)
            .Select(id => new BossSpawn(id, false, id == 2001 ? ServerNow + 60000 : null)).ToArray();
        var cache = new BossTimerCache(CachePath, _time);
        Assert.True(cache.Save(new(20, bosses, ServerNow, TimeSpan.Zero)));
        using (var json = JsonDocument.Parse(File.ReadAllText(CachePath)))
        {
            var saved = json.RootElement.GetProperty("Maps").GetProperty("20").GetProperty("Bosses");
            Assert.Equal(7, saved.EnumerateArray().Count(row => row.GetProperty("SpawnUnixMs").ValueKind == JsonValueKind.Null));
        }
        _time.Advance(TimeSpan.FromSeconds(10));
        var restored = new BossTimerCache(CachePath, _time).Load();
        Assert.NotNull(restored);
        Assert.True(restored.Cached);
        Assert.Equal(bosses, restored.Bosses);
        Assert.Equal(2001, restored.Next!.SpawnId);
        Assert.Equal(ServerNow + 10000, restored.ServerUnixMs);
        var original = File.ReadAllBytes(CachePath);
        Assert.False(cache.Save(new(20, [new(2001, true, null)], ServerNow, TimeSpan.Zero)));
        Assert.False(cache.Save(new(20, [new(2001, false, 0)], ServerNow, TimeSpan.Zero)));
        Assert.Equal(original, File.ReadAllBytes(CachePath));
        Assert.True(cache.Save(new(20, bosses.Select(b => b with { SpawnUnixMs = null }).ToArray(), ServerNow, TimeSpan.Zero)));
        var unknown = new BossTimerCache(CachePath, _time).Load();
        Assert.NotNull(unknown);
        Assert.Equal(8, unknown.Bosses.Count);
        Assert.Null(unknown.Next);
    }

    [Fact]
    public void ReorderedIdenticalListsAndClockRefreshesDoNotRewriteTheFile()
    {
        var cache = new BossTimerCache(CachePath, _time);
        var snapshot = Snapshot();
        Assert.True(cache.Save(snapshot));
        var original = File.ReadAllText(CachePath);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(cache.Save(snapshot with { Bosses = Enumerable.Reverse(snapshot.Bosses).ToArray(), ServerUnixMs = ServerNow + 10000 }));
        Assert.False(new BossTimerCache(CachePath, _time).Save(snapshot));
        Assert.Equal(original, File.ReadAllText(CachePath));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void BossChangesAndMapSelectionPersistWithOtherMapsIntact()
    {
        var cache = new BossTimerCache(CachePath, _time);
        var first = Snapshot();
        Assert.True(cache.Save(first));
        var changed = first with { Bosses = [first.Bosses[0] with { Spawned = true }, first.Bosses[1]] };
        Assert.True(cache.Save(changed));
        Assert.True(cache.Save(Snapshot(1010)));
        Assert.Equal(1010, new BossTimerCache(CachePath, _time).Load()!.MapId);
        Assert.True(cache.Save(changed)); // Same saved map data, a different active map.
        var restored = new BossTimerCache(CachePath, _time).Load();
        Assert.Equal(1110, restored!.MapId);
        Assert.True(restored.Bosses[0].Spawned);
        using var document = JsonDocument.Parse(File.ReadAllText(CachePath));
        Assert.Equal(2, document.RootElement.GetProperty("Maps").EnumerateObject().Count());
        Assert.False(cache.Save(changed));
        Assert.True(cache.Save(changed with { Bosses = [changed.Bosses[0] with { SpawnUnixMs = ServerNow + 2000 }] }));
    }

    [Fact]
    public void InvalidAndProvisionalSnapshotsCannotReplaceAGoodCache()
    {
        var cache = new BossTimerCache(CachePath, _time);
        var good = Snapshot();
        Assert.True(cache.Save(good));
        var original = File.ReadAllText(CachePath);
        BossRushSnapshot[] invalid =
        [
            good with { MapId = 0 },
            good with { MapId = -1 },
            good with { Bosses = [] },
            good with { Bosses = Enumerable.Range(1, 129).Select(id => new BossSpawn(id, false, ServerNow)).ToArray() },
            good with { Bosses = [good.Bosses[0], good.Bosses[0]] },
            good with { Bosses = [new(0, false, ServerNow)] },
            good with { Bosses = [new(1, false, 1577836799999)] },
            good with { Bosses = [new(1, false, 4102444800000)] },
            good with { ServerUnixMs = 0 },
            good with { Age = TimeSpan.FromMilliseconds(-1) },
            good with { Age = TimeSpan.FromHours(25) },
            good with { Cached = true }
        ];
        Assert.All(invalid, snapshot => Assert.False(cache.Save(snapshot)));
        Assert.Equal(original, File.ReadAllText(CachePath));
    }

    [Fact]
    public void OldObservedListsRemainProvisionalWithoutInferringANewSpawnAndRejectABackwardsWallClock()
    {
        Assert.True(new BossTimerCache(CachePath, _time).Save(Snapshot()));
        _time.Advance(TimeSpan.FromDays(30));
        var restored = new BossTimerCache(CachePath, _time).Load();
        Assert.NotNull(restored);
        Assert.True(restored.Cached);
        Assert.Equal(TimeSpan.FromDays(30), restored.Age);
        Assert.False(restored.Bosses[0].Spawned);
        Assert.Equal(ServerNow + 60000, restored.Bosses[0].SpawnUnixMs);
        _time.Advance(-TimeSpan.FromDays(30) - TimeSpan.FromMilliseconds(1));
        Assert.Null(new BossTimerCache(CachePath, _time).Load());
        Assert.Empty(new BossTimerCache(CachePath, _time).LoadAll());
    }

    [Fact]
    public void LoadAllRestoresEverySavedMapAndNeverRewritesExpiredObservedTimes()
    {
        var cache = new BossTimerCache(CachePath, _time);
        Assert.True(cache.Save(Snapshot()));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(cache.Save(Snapshot(1010) with { ServerUnixMs = ServerNow + 120000 }));
        var bytes = File.ReadAllBytes(CachePath);
        _time.Advance(TimeSpan.FromDays(2));
        var restored = new BossTimerCache(CachePath, _time).LoadAll();
        Assert.Equal(new[] { 1010, 1110 }, restored.Select(snapshot => snapshot.MapId));
        Assert.All(restored, snapshot => Assert.True(snapshot.Cached));
        Assert.All(restored, snapshot => Assert.Equal(ServerNow + 120000 + 172800000, snapshot.ServerUnixMs));
        Assert.Equal(TimeSpan.FromDays(2), restored[0].Age);
        Assert.Equal(TimeSpan.FromDays(2) + TimeSpan.FromMinutes(2), restored[1].Age);
        Assert.Equal(bytes, File.ReadAllBytes(CachePath));
    }

    [Fact]
    public void LoadAllRejectsOnlyClockUnsafeRestorationsWithoutReplacingTheLatestMap()
    {
        var cache = new BossTimerCache(CachePath, _time);
        Assert.True(cache.Save(Snapshot()));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(cache.Save(Snapshot(1010) with { ServerUnixMs = ServerNow + 3600000 }));
        _time.Advance(-TimeSpan.FromMinutes(30));
        var restored = new BossTimerCache(CachePath, _time);
        Assert.Null(restored.Load());
        Assert.Equal(1110, Assert.Single(restored.LoadAll()).MapId);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"LatestMap\":1110,\"Maps\":{}}")]
    [InlineData("{\"LatestMap\":1110,\"Maps\":null}")]
    [InlineData("{\"LatestMap\":1110,\"Maps\":{\"1110\":null}}")]
    [InlineData("{\"LatestMap\":1110,\"Maps\":{\"1110\":{\"MapId\":1110,\"Bosses\":[]}}}")]
    public void CorruptOrInvalidDocumentsAreIgnoredAndARealListCanRepairThem(string content)
    {
        File.WriteAllText(CachePath, content);
        var cache = new BossTimerCache(CachePath, _time);
        Assert.Null(cache.Load());
        Assert.Equal(content, File.ReadAllText(CachePath));
        Assert.True(cache.Save(Snapshot()));
        Assert.NotNull(new BossTimerCache(CachePath, _time).Load());
    }

    [Fact]
    public void ValidJsonWithDuplicateBossIdsIsRejected()
    {
        var cache = new BossTimerCache(CachePath, _time);
        Assert.True(cache.Save(Snapshot()));
        var document = JsonDocument.Parse(File.ReadAllText(CachePath));
        var saved = document.RootElement.GetProperty("Maps").GetProperty("1110");
        var duplicate = new
        {
            LatestMap = 1110,
            Maps = new Dictionary<int, object>
            {
                [1110] = new
                {
                    MapId = 1110,
                    Bosses = new[] { Snapshot().Bosses[0], Snapshot().Bosses[0] },
                    ServerUnixMs = ServerNow,
                    SavedAtUtc = saved.GetProperty("SavedAtUtc").GetDateTimeOffset(),
                    ObservedAtUtc = saved.GetProperty("ObservedAtUtc").GetDateTimeOffset()
                }
            }
        };
        File.WriteAllText(CachePath, JsonSerializer.Serialize(duplicate));
        document.Dispose();
        Assert.Null(new BossTimerCache(CachePath, _time).Load());
    }

    private static BossRushSnapshot Snapshot(int map = 1110) => new(map,
        [new(111001, false, ServerNow + 60000), new(111002, true, ServerNow - 1000)], ServerNow, TimeSpan.Zero);

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(ServerNow);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}