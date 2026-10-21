using Soulcrest.Core.Alerts;

namespace Soulcrest.Core.Network;

public sealed record BossAlertTrigger(int MapId, BossSpawn Boss, bool Spawned, int LeadSeconds = 0);

/// <summary>One delivery per selected warning offset and one confirmed spawn per cycle.</summary>
public sealed class BossAlertPolicy
{
    private readonly HashSet<(int Map, int Id, long At, int Lead)> _seen = [];
    private readonly Queue<(int Map, int Id, long At, int Lead)> _history = [];

    public IReadOnlyList<BossAlertTrigger> Evaluate(BossRushSnapshot snapshot, IReadOnlySet<string> selected, int leadSeconds)
        => Evaluate(snapshot, selected, _ => leadSeconds);

    public IReadOnlyList<BossAlertTrigger> Evaluate(BossRushSnapshot snapshot, IReadOnlySet<string> selected,
        Func<string, int?> leadSecondsFor) => EvaluateMany(snapshot, selected,
            key => leadSecondsFor(key) is { } lead ? AdvanceWarnings.Resolve(null, Math.Clamp(lead, 0, 3600), 3600) : null);

    /// <summary>Null pauses the boss; an empty list still permits confirmed spawn delivery.</summary>
    public IReadOnlyList<BossAlertTrigger> EvaluateMany(BossRushSnapshot snapshot, IReadOnlySet<string> selected,
        Func<string, int[]?> leadSecondsFor)
    {
        if (snapshot.Cached) return [];
        var alerts = new List<BossAlertTrigger>();
        foreach (var boss in snapshot.Bosses.OrderBy(b => b.SpawnUnixMs).ThenBy(b => b.SpawnId))
        {
            if (boss.SpawnUnixMs is not { } at) continue;
            var bossKey = $"{snapshot.MapId}:{boss.SpawnId}";
            if (!selected.Contains(bossKey) || leadSecondsFor(bossKey) is not { } leads) continue;
            var remaining = at - snapshot.ServerUnixMs;
            if (!boss.Spawned && remaining <= 0) continue;
            var due = boss.Spawned ? [0] : AdvanceWarnings.Resolve(leads, 0, 3600)
                .Where(lead => remaining <= lead * 1000L).ToArray();
            if (due.Length == 0) continue;
            // Coalesce crossed thresholds, including late first observations. Never fall back to
            // an older threshold after the most recent one has already been consumed.
            var latest = due.Min();
            var deliver = !_seen.Contains((snapshot.MapId, boss.SpawnId, at, latest)) &&
                (latest == 0 || !_seen.Any(seen => seen.Map == snapshot.MapId && seen.Id == boss.SpawnId &&
                    seen.At == at && seen.Lead > 0 && seen.Lead < latest));
            foreach (var lead in due)
            {
                var key = (snapshot.MapId, boss.SpawnId, at, lead);
                if (!_seen.Add(key)) continue;
                _history.Enqueue(key);
                while (_history.Count > 4096) _seen.Remove(_history.Dequeue());
            }
            if (deliver) alerts.Add(new(snapshot.MapId, boss, boss.Spawned, latest));
        }
        return alerts;
    }
}
