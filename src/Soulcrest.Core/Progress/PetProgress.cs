using System.Text.Json.Serialization;

namespace Soulcrest.Core.Progress;

public sealed class PetProgressEntry
{
    [JsonPropertyName("souls")] public int Souls { get; set; }
    [JsonPropertyName("focus")] public bool Focus { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProgressProfile
{
    [JsonPropertyName("server")] public string Server { get; set; } = "Standard";
    [JsonPropertyName("pets")] public Dictionary<string, PetProgressEntry> Pets { get; set; } = new(StringComparer.Ordinal);

    public PetProgressEntry Get(string petId) =>
        Pets.TryGetValue(petId, out var entry) ? entry : Pets[petId] = new PetProgressEntry();
}

/// <summary>
/// Souls needed per pet level, each level counted from zero (confirmed by the user, 2026-10-03):
/// level 1 (unlock) 5, level 2 another 25, level 3 (max) another 75 – 105 in total. Excess souls
/// carry over into the next level (also confirmed), so the total is the stored quantity.
/// Progress is stored as the total; display and input follow the game: level + souls in level.
/// </summary>
public sealed record SoulThresholds(IReadOnlyList<int> Steps)
{
    public static SoulThresholds Default { get; } = new([5, 25, 75]);

    public int MaxLevel => Steps.Count;

    /// <summary>Total souls to reach max level.</summary>
    public int Max => Steps.Sum();

    /// <summary>Souls needed to unlock the pet (level 1).</summary>
    public int Unlock => Steps.Count > 0 ? Steps[0] : 0;

    /// <summary>Total souls at which a level is reached (level 0 = 0).</summary>
    public int TotalForLevel(int level) => Steps.Take(Math.Clamp(level, 0, MaxLevel)).Sum();

    /// <summary>0 = not unlocked, 1..MaxLevel = level reached.</summary>
    public int Level(int totalSouls)
    {
        var level = 0;
        var needed = 0;
        foreach (var step in Steps)
        {
            needed += step;
            if (totalSouls < needed)
                break;
            level++;
        }
        return level;
    }

    public bool IsMax(int totalSouls) => Level(totalSouls) >= MaxLevel;

    /// <summary>Souls collected towards the next level (as the game shows them).</summary>
    public int SoulsInLevel(int totalSouls) =>
        IsMax(totalSouls) ? 0 : Math.Max(0, totalSouls - TotalForLevel(Level(totalSouls)));

    /// <summary>Souls the next level needs in total (5, 25 or 75); 0 at max level.</summary>
    public int NeededForNextLevel(int totalSouls) =>
        IsMax(totalSouls) ? 0 : Steps[Level(totalSouls)];

    /// <summary>Total from a game-style entry: reached level plus souls towards the next one.</summary>
    public int Total(int level, int soulsInLevel)
    {
        level = Math.Clamp(level, 0, MaxLevel);
        if (level >= MaxLevel)
            return Max;
        return TotalForLevel(level) + Math.Clamp(soulsInLevel, 0, Steps[level] - 1);
    }
}
