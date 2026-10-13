namespace Soulcrest.Core.Network;

/// <summary>A confirmed connection-bound list, or a retained cached list after explicit invalidation.</summary>
/// <param name="Age">Elapsed time since the full list was observed; an information age, not a validity timeout.</param>
/// <param name="Cached">True after restoration or invalidation; only a new list with its own connection clock confirms it.</param>
public sealed record BossRushSnapshot(int MapId, IReadOnlyList<BossSpawn> Bosses, long ServerUnixMs, TimeSpan Age, bool Cached = false)
{
    // A timer expiring alone does not prove a spawn. The server's Spawned flag has priority.
    public BossSpawn? Next => Bosses.OrderByDescending(b => b.Spawned).ThenBy(b => b.SpawnUnixMs).ThenBy(b => b.SpawnId).FirstOrDefault();
}

/// <summary>Connection-bound live clocks with a retained monotonic schedule during loading screens.</summary>
public sealed class BossRushState(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, (long UnixMs, long Received)> _clocks = [];
    private sealed record Schedule(string? Stream, BossSpawnList List, long Received, long UnixMs, long ClockAt, TimeSpan InitialAge, bool Cached);
    private Schedule? _latest;

    public bool Accept(string stream, ReadOnlySpan<byte> message)
    {
        var now = _time.GetTimestamp();
        if (BossSpawnMessage.ReadServerClock(message) is { } time)
        {
            _clocks[stream] = (time, now);
            if (_latest is { Cached: false } latest && latest.Stream == stream)
                // Clock updates rebase countdowns without changing the list's observation age or confirming cached data.
                _latest = latest with { UnixMs = time, ClockAt = now };
            return false; // 20 Hz clocks must not cause UI refreshes or disk writes.
        }
        if (BossSpawnMessage.TryParse(message) is not { } list || !_clocks.TryGetValue(stream, out var clock)) return false;
        _latest = new(stream, list, now, clock.UnixMs, clock.Received, TimeSpan.Zero, false);
        return true;
    }

    public BossRushSnapshot? Snapshot
    {
        get
        {
            if (_latest is not { } latest) return null;
            var now = _time.GetTimestamp();
            return new(latest.List.MapId, latest.List.Bosses,
                latest.UnixMs + (long)_time.GetElapsedTime(latest.ClockAt, now).TotalMilliseconds,
                latest.InitialAge + _time.GetElapsedTime(latest.Received, now), latest.Cached);
        }
    }

    /// <summary>Retain the last known schedule, but require the new connection's own clock and list.</summary>
    public void Invalidate()
    {
        if (Snapshot is { } snapshot) Restore(snapshot);
        _clocks.Clear();
    }

    /// <summary>Discard a broken stream's clock; only a gap in the schedule's own stream makes it cached.</summary>
    /// <returns>True only when the confirmed schedule changed to cached; unrelated or repeated gaps are silent.</returns>
    public bool Invalidate(string stream)
    {
        if (stream == "*")
        {
            var changed = _latest is { Cached: false };
            Invalidate();
            return changed;
        }
        _clocks.Remove(stream);
        if (_latest is not { Cached: false } latest || latest.Stream != stream) return false;
        if (Snapshot is { } snapshot) Restore(snapshot);
        return true;
    }

    /// <summary>Restores an extrapolated disk snapshot; from now on elapsed time is monotonic.</summary>
    public void Restore(BossRushSnapshot snapshot)
    {
        var now = _time.GetTimestamp();
        _latest = new(null, new(snapshot.MapId, snapshot.Bosses.ToArray()), now, snapshot.ServerUnixMs, now, snapshot.Age, true);
    }

    public void Reset()
    {
        _latest = null;
        _clocks.Clear();
    }
}
