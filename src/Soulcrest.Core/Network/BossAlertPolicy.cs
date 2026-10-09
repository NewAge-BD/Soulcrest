namespace Soulcrest.Core.Network;

public sealed record BossAlertTrigger(int MapId, BossSpawn Boss, bool Spawned);

/// <summary>At most one warning and one confirmed-spawn alert per boss cycle. Never infer a spawn from a timer.</summary>
public sealed class BossAlertPolicy
{
    private readonly HashSet<(int Map, int Id, long At, bool Spawned)> _seen = [];
    private readonly Queue<(int Map, int Id, long At, bool Spawned)> _history = [];

    public IReadOnlyList<BossAlertTrigger> Evaluate(BossRushSnapshot snapshot, IReadOnlySet<string> selected, int leadSeconds)
    {
        if (snapshot.Cached || snapshot.Age > TimeSpan.FromSeconds(15)) return [];
        var alerts = new List<BossAlertTrigger>();
        foreach (var boss in snapshot.Bosses.OrderBy(b => b.SpawnUnixMs).ThenBy(b => b.SpawnId))
        {
            if (!selected.Contains($"{snapshot.MapId}:{boss.SpawnId}")) continue;
            var remaining = boss.SpawnUnixMs - snapshot.ServerUnixMs;
            if (!boss.Spawned && (remaining <= 0 || remaining > Math.Clamp(leadSeconds, 0, 3600) * 1000L)) continue;
            var key = (snapshot.MapId, boss.SpawnId, boss.SpawnUnixMs, boss.Spawned);
            if (!_seen.Add(key)) continue;
            _history.Enqueue(key);
            while (_history.Count > 1024) _seen.Remove(_history.Dequeue());
            alerts.Add(new(snapshot.MapId, boss, boss.Spawned));
        }
        return alerts;
    }
}
