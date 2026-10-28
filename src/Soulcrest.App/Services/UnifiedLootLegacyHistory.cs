namespace Soulcrest.App.Services;

/// <summary>Legacy sessions plus whether any original store reported damaged records.</summary>
public sealed record UnifiedLootLegacyRead(IReadOnlyList<LootTrackerSession> Sessions, bool Incomplete);

/// <summary>Reads existing histories without regrouping them. Explicit deletion keeps each original store's contract.</summary>
public sealed class UnifiedLootLegacyHistory
{
    private readonly FarmLootSessionStore _farm;
    private readonly FarmLootSessionStore _abyss;
    private readonly LifeskillSessionStore _lifeskill;
    private readonly Dictionary<string, Func<bool>> _delete = new(StringComparer.Ordinal);

    public UnifiedLootLegacyHistory(string lootRoot)
    {
        _farm = new(Path.Combine(lootRoot, "monster-sessions"));
        _abyss = new(Path.Combine(lootRoot, "abyss-sessions"));
        _lifeskill = new(Path.Combine(lootRoot, "sessions"));
    }

    internal UnifiedLootLegacyRead ReadSessions()
    {
        var farm = _farm.Read(); var abyss = _abyss.Read(); var life = _lifeskill.Read();
        var sessions = new List<LootTrackerSession>();
        var duplicate = new HashSet<string>(StringComparer.Ordinal);
        _delete.Clear();
        Add(farm.Sessions, _farm.Delete);
        Add(abyss.Sessions, _abyss.Delete);
        Add(life.Sessions, _lifeskill.Delete);
        foreach (var id in duplicate) _delete.Remove(id);
        // Each original store already marks its own damaged sessions; healthy ones stay unchanged.
        return new(sessions.Where(session => !duplicate.Contains(session.Id)).ToArray(),
            farm.Issues.Count > 0 || abyss.Issues.Count > 0 || life.Issues.Count > 0);

        void Add(IEnumerable<LootTrackerSession> rows, Func<string, bool> remove)
        {
            foreach (var row in rows)
            {
                if (!_delete.TryAdd(row.Id, () => remove(row.Id))) { duplicate.Add(row.Id); continue; }
                sessions.Add(row);
            }
        }
    }

    internal bool Delete(LootTrackerSession session)
    {
        if (session.UnifiedVersion != 0 || !_delete.TryGetValue(session.Id, out var remove))
            throw new KeyNotFoundException("The original session store is unavailable.");
        var pending = remove();
        _delete.Remove(session.Id);
        return pending;
    }
}
