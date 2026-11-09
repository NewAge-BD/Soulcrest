using System.Globalization;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Components;

public sealed record CombatWingPassiveTotal(CombatTitlePassiveKey Key, string LabelDe, string LabelEn,
    decimal? Value, int WingCount, CombatKnowledgeStatus Status);
public sealed record CombatWingPassiveSummary(CombatKnowledgeStatus Status, IReadOnlyList<CombatWingPassiveTotal> Rows);

/// <summary>Base display effects only. Enchantment, appearance skins and presets never contribute here.</summary>
public static class CombatWingsPresentation
{
    public static string? Race(string? faction) => faction switch
    {
        Factions.Elyos => "Light", Factions.Asmodian => "Dark", _ => null
    };

    public static IReadOnlyList<int> CollectionIds(CombatWingDisplayCatalog catalog, IEnumerable<int>? ownedIds,
        string? faction, string language) => Order(catalog,
            catalog.Entries.Values.Where(wing => Race(faction) is not { } race || wing.Race == race)
                .Select(wing => wing.Id).Concat(ownedIds ?? []), language);

    public static IReadOnlyList<int> Order(CombatWingDisplayCatalog catalog, IEnumerable<int> ids, string language) =>
        Array.AsReadOnly(ids.Distinct().OrderBy(id => GradeSortRank(catalog.Find(id)?.Grade))
            .ThenBy(id => Name(catalog, id, language), StringComparer.Create(Culture(language), true)).ThenBy(id => id).ToArray());

    public static string Name(CombatWingDisplayCatalog catalog, int id, string language) =>
        catalog.Name(id, language) ?? UiText.T("Nicht zugeordnete Flügel");

    public static CombatWingPassiveSummary OwnedPassives(CombatWingDisplayCatalog catalog, IEnumerable<int>? ownedIds,
        bool complete, string language)
    {
        if (ownedIds is null) return new(CombatKnowledgeStatus.Unknown, []);
        var wings = ownedIds.Distinct().Select(id => (Id: id, Definition: catalog.Find(id))).ToArray();
        var unknownMetadata = wings.Any(wing => wing.Definition?.OwnedEffects is null);
        var effects = wings.SelectMany(wing => (wing.Definition?.OwnedEffects ?? [])
            .Select(effect => (wing.Id, Effect: effect))).ToArray();
        var unknownUnits = effects.Where(row => row.Effect.Unit is null).Select(row => row.Effect.StatId)
            .ToHashSet(StringComparer.Ordinal);
        var rows = effects.GroupBy(row => CombatTitlesPresentation.Key(row.Effect)).Select(group =>
        {
            var sample = group.First().Effect;
            var qualified = group.All(row => row.Effect.Value is not null && row.Effect.Unit is "Points" or "Percent");
            var known = complete && !unknownMetadata && qualified && !unknownUnits.Contains(group.Key.StatId);
            return new CombatWingPassiveTotal(group.Key, sample.LabelDe, sample.LabelEn,
                known ? group.Sum(row => row.Effect.Value!.Value) : null, group.Select(row => row.Id).Distinct().Count(),
                known ? CombatKnowledgeStatus.Complete : CombatKnowledgeStatus.Partial);
        }).OrderBy(row => CombatTitlesPresentation.PassiveLabel(row.LabelDe, row.LabelEn, language),
            StringComparer.Create(Culture(language), true)).ThenBy(row => row.Key.StatId, StringComparer.Ordinal)
            .ThenBy(row => row.Key.Unit, StringComparer.Ordinal).ToArray();
        return new(complete && !unknownMetadata && rows.All(row => row.Status == CombatKnowledgeStatus.Complete)
            ? CombatKnowledgeStatus.Complete : CombatKnowledgeStatus.Partial, Array.AsReadOnly(rows));
    }

    /// <summary>Only absent base catalogue entries; never the upgrade potential of owned wings.</summary>
    public static CombatWingPassiveSummary MissingPassives(CombatWingDisplayCatalog catalog,
        IEnumerable<int>? ownedIds, bool ownershipComplete, string? faction, string language)
    {
        var owned = (ownedIds ?? []).ToHashSet();
        var candidates = CollectionIds(catalog, null, faction, language).Where(id => !owned.Contains(id));
        return OwnedPassives(catalog, candidates, ownershipComplete && Race(faction) is not null, language);
    }

    public static bool HasOwnedStat(CombatWingDisplayCatalog catalog, int id, CombatTitlePassiveKey key) =>
        catalog.Find(id)?.OwnedEffects?.Any(effect => CombatTitlesPresentation.Key(effect) == key) == true;

    public static int GradeSortRank(string? grade) => grade switch
    {
        "Special" => 0, "Epic" => 1, "Unique" => 2, "Legend" => 3, "Rare" => 4, "Common" => 5, _ => 6
    };

    private static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
}
