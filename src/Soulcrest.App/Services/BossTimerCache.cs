using System.Text.Json;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>
/// Last observed boss lists, without transient connection data. The protocol does not expose a verified
/// server or channel identity, so restored lists are always provisional until another full list arrives.
/// </summary>
public sealed class BossTimerCache(string path, TimeProvider? time = null)
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);
    private readonly object _gate = new();
    private readonly string _path = Path.GetFullPath(path);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CacheDocument? _document;

    /// <summary>Restores the last selected map once, extrapolating its saved server-time anchor by wall time.</summary>
    public BossRushSnapshot? Load()
    {
        lock (_gate)
        {
            var document = Read();
            if (!document.Maps.TryGetValue(document.LatestMap, out var saved)) return null;
            var now = _time.GetUtcNow();
            var elapsed = now - saved.SavedAtUtc;
            var age = now - saved.ObservedAtUtc;
            // A backwards system clock cannot safely advance the server clock.
            if (elapsed < TimeSpan.Zero || age < TimeSpan.Zero || age > MaximumAge) return null;
            var serverNow = saved.ServerUnixMs + (long)elapsed.TotalMilliseconds;
            if (!ValidTime(serverNow)) return null;
            return new(saved.MapId, saved.Bosses.ToArray(), serverNow, age, Cached: true);
        }
    }

    /// <summary>Writes the first list, a changed list, or a changed map selection; identical refreshes do not write.</summary>
    public bool Save(BossRushSnapshot snapshot)
    {
        if (snapshot.Cached || !ValidSnapshot(snapshot)) return false;
        lock (_gate)
        {
            var current = Read();
            var bosses = snapshot.Bosses.OrderBy(b => b.SpawnId).ToArray();
            if (current.LatestMap == snapshot.MapId && current.Maps.TryGetValue(snapshot.MapId, out var previous)
                && previous.Bosses.OrderBy(b => b.SpawnId).SequenceEqual(bosses)) return false;
            var now = _time.GetUtcNow();
            var observed = now - snapshot.Age;
            if (!ValidTime(now.ToUnixTimeMilliseconds()) || !ValidTime(observed.ToUnixTimeMilliseconds())) return false;
            var next = new CacheDocument { LatestMap = snapshot.MapId, Maps = new(current.Maps) };
            next.Maps[snapshot.MapId] = new CacheRecord
            {
                MapId = snapshot.MapId,
                Bosses = bosses,
                ServerUnixMs = snapshot.ServerUnixMs,
                SavedAtUtc = now,
                ObservedAtUtc = observed
            };
            try
            {
                JsonFile.Save(_path, next);
                _document = next;
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                LogFile.Error("Boss timer cache save", error);
                return false;
            }
        }
    }

    private CacheDocument Read()
    {
        if (_document is not null) return _document;
        try
        {
            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                var document = JsonSerializer.Deserialize<CacheDocument>(stream);
                if (document is null || document.Version != 1 || document.Maps is null
                    || document.Maps.Count is < 1 or > 128 || !document.Maps.ContainsKey(document.LatestMap)
                    || document.Maps.Any(pair => !ValidRecord(pair.Key, pair.Value)))
                    throw new InvalidDataException("Invalid boss timer cache.");
                return _document = document;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Preserve the unreadable file. A subsequent real list can atomically replace it.
            LogFile.Error("Boss timer cache load", error);
        }
        return _document = new();
    }

    private static bool ValidSnapshot(BossRushSnapshot snapshot) => snapshot.MapId > 0
        && ValidBosses(snapshot.Bosses) && ValidTime(snapshot.ServerUnixMs)
        && snapshot.Age >= TimeSpan.Zero && snapshot.Age <= MaximumAge;

    private static bool ValidRecord(int map, CacheRecord? record) => record is not null
        && map > 0 && map == record.MapId && ValidBosses(record.Bosses) && ValidTime(record.ServerUnixMs)
        && ValidTime(record.SavedAtUtc.ToUnixTimeMilliseconds()) && ValidTime(record.ObservedAtUtc.ToUnixTimeMilliseconds())
        && record.ObservedAtUtc <= record.SavedAtUtc && record.SavedAtUtc - record.ObservedAtUtc <= MaximumAge;

    private static bool ValidBosses(IReadOnlyList<BossSpawn>? bosses) => bosses is { Count: >= 1 and <= 128 }
        && bosses.All(b => b is not null && b.SpawnId > 0 && ValidTime(b.SpawnUnixMs))
        && bosses.Select(b => b.SpawnId).Distinct().Count() == bosses.Count;

    private static bool ValidTime(long unixMs) => unixMs is >= 1577836800000 and < 4102444800000; // 2020–2100

    private sealed class CacheDocument
    {
        public CacheDocument() { }
        public int Version { get; set; } = 1;
        public int LatestMap { get; set; }
        public Dictionary<int, CacheRecord> Maps { get; set; } = [];
    }

    private sealed class CacheRecord
    {
        public CacheRecord() { }
        public int MapId { get; set; }
        public BossSpawn[] Bosses { get; set; } = [];
        public long ServerUnixMs { get; set; }
        public DateTimeOffset SavedAtUtc { get; set; }
        public DateTimeOffset ObservedAtUtc { get; set; }
    }
}