namespace Soulcrest.App.Services;

/// <summary>User request 2026-10-26: independent opt-out and local MP3 choice for each exceptional tier.</summary>
public sealed record DropRarityAlertSettings(bool Enabled = true, bool SoundEnabled = true,
    string Sound = "standard", string? SoundFile = null)
{
    public static IReadOnlyList<LootItemRarity> SupportedRarities { get; } =
        Array.AsReadOnly(new[] { LootItemRarity.Unique, LootItemRarity.Epic, LootItemRarity.Mythic, LootItemRarity.Special });

    public static bool Supports(LootItemRarity rarity) => MonsterLootItemCatalog.IsExceptional(rarity);

    public static DropRarityAlertSettings Normalize(DropRarityAlertSettings? value)
    {
        value ??= new();
        return value with
        {
            Sound = value.Sound == "custom" ? "custom" : "standard",
            // Keep a missing file choice so playback can explain its fallback instead of silently changing it.
            SoundFile = AlertsService.ValidSoundFile(value.SoundFile) ? Path.GetFullPath(value.SoundFile!) : null
        };
    }

    internal static Dictionary<LootItemRarity, DropRarityAlertSettings> Normalize(
        IReadOnlyDictionary<LootItemRarity, DropRarityAlertSettings>? values) => values is null ? [] :
        values.Where(entry => Supports(entry.Key)).ToDictionary(entry => entry.Key, entry => Normalize(entry.Value));
}
