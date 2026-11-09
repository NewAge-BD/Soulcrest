using System.Globalization;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Components;

public sealed record CombatTitleFilter(string Id, string Label);
public sealed record CombatTitleEffectSlot(byte Key, string Category, string Label);
public sealed record CombatTitleListRow(int Id, string Name);
public readonly record struct CombatTitlePassiveKey(string StatId, string? Unit);
public sealed record CombatTitlePassiveTotal(CombatTitlePassiveKey Key, string LabelDe, string LabelEn,
    decimal? Value, decimal? KnownSubtotal, int TitleCount, CombatKnowledgeStatus Status);
public sealed record CombatTitlePassiveSummary(CombatKnowledgeStatus Status, IReadOnlyList<CombatTitlePassiveTotal> Rows);
public sealed record CombatTitleMissingPassive(CombatTitlePassiveKey Key, string LabelDe, string LabelEn, int CatalogVariantCount)
{
    public decimal? EstimatedValue { get; init; }
    public CombatKnowledgeStatus ValueStatus { get; init; } = CombatKnowledgeStatus.Unknown;
}

/// <summary>Read-only display filters over independently observed title IDs and named metadata.</summary>
public static class CombatTitlesPresentation
{
    public static IReadOnlyList<CombatTitleFilter> CategoryFilters { get; } = Array.AsReadOnly(new[]
    {
        new CombatTitleFilter("All", "Alle"), new("Attack", "Offensiv"), new("Defense", "Defensiv"), new("Etc", "Sonstige")
    });
    public static IReadOnlyList<CombatTitleFilter> OriginFilters { get; } = Array.AsReadOnly(new[]
    {
        new CombatTitleFilter("All", "Alle"), new("Quest", "Quests"), new("Growth", "Fortschritt"),
        new("Challenge", "Herausforderung"), new("Adventure", "Erkundung"), new("Dungeon", "Dungeon"), new("Item", "Ressourcen")
    });
    // Original title source and matching named metadata establish these three effect slots.
    public static IReadOnlyList<CombatTitleEffectSlot> EffectSlots { get; } = Array.AsReadOnly(new[]
    {
        new CombatTitleEffectSlot(1, "Attack", "Offensiv"), new(2, "Defense", "Defensiv"), new(3, "Etc", "Sonstige")
    });

    public static string Name(CombatTitleDisplayCatalog catalog, int id, string language) =>
        catalog.Name(id, language) ?? (language == "de" ? "Nicht zugeordneter Titel" : "Unmapped title");

    public static IReadOnlyList<CombatTitleListRow> Rows(CombatTitleDisplayCatalog catalog, IEnumerable<int> ownedIds,
        string language, string category = "All", string origin = "All", CombatTitlePassiveKey? passive = null) => Array.AsReadOnly(ownedIds.Distinct()
        .Where(id => category == "All" || catalog.Find(id)?.EquipCategory == category)
        .Where(id => origin == "All" || catalog.Find(id)?.Origin == origin)
        .Where(id => passive is not { } filter || catalog.Find(id)?.OwnedEffects?.Any(effect => Key(effect) == filter) == true)
        .Select(id => new CombatTitleListRow(id, Name(catalog, id, language)))
        .OrderBy(row => GradeSortRank(catalog.Find(row.Id)?.Grade))
        .ThenBy(row => row.Name, StringComparer.Create(Culture(language), true)).ThenBy(row => row.Id).ToArray());

    public static int GradeSortRank(string? grade) => grade switch
    {
        "Unique" => 0, "Legend" => 1, "Rare" => 2, "Common" => 3, _ => 4
    };

    public static CombatTitlePassiveSummary OwnedPassives(CombatTitleDisplayCatalog catalog, IEnumerable<int>? ownedIds,
        bool ownershipComplete, string language)
    {
        if (ownedIds is null) return new(CombatKnowledgeStatus.Unknown, []);
        var titles = ownedIds.Distinct().Select(id => (Id: id, Definition: catalog.Find(id))).ToArray();
        var unknownMetadata = titles.Any(title => title.Definition?.OwnedEffects is null);
        var effects = titles.SelectMany(title => (title.Definition?.OwnedEffects ?? [])
            .Select(effect => (title.Id, Effect: effect))).ToArray();
        var unknownUnitStats = effects.Where(row => row.Effect.Unit is null).Select(row => row.Effect.StatId).ToHashSet(StringComparer.Ordinal);
        var rows = effects.GroupBy(row => Key(row.Effect)).Select(group =>
        {
            var sample = group.First().Effect;
            var qualified = group.Where(row => row.Effect.Value is not null && (row.Effect.Unit is "Points" or "Percent")).ToArray();
            decimal? subtotal = qualified.Length == 0 ? null : qualified.Sum(row => row.Effect.Value!.Value);
            var complete = ownershipComplete && !unknownMetadata && !unknownUnitStats.Contains(group.Key.StatId)
                && qualified.Length == group.Count();
            return new CombatTitlePassiveTotal(group.Key, sample.LabelDe, sample.LabelEn, complete ? subtotal : null,
                subtotal, group.Select(row => row.Id).Distinct().Count(), complete ? CombatKnowledgeStatus.Complete : CombatKnowledgeStatus.Partial);
        }).OrderBy(row => PassiveLabel(row.LabelDe, row.LabelEn, language), StringComparer.Create(Culture(language), true))
            .ThenBy(row => row.Key.StatId, StringComparer.Ordinal).ThenBy(row => row.Key.Unit, StringComparer.Ordinal).ToArray();
        var status = ownershipComplete && !unknownMetadata && rows.All(row => row.Status == CombatKnowledgeStatus.Complete)
            ? CombatKnowledgeStatus.Complete : CombatKnowledgeStatus.Partial;
        return new(status, Array.AsReadOnly(rows));
    }

    /// <summary>Potential stat types from unobserved catalog variants, never an eligible-title or missing-value total.</summary>
    public static IReadOnlyList<CombatTitleMissingPassive> MissingPassivePreview(CombatTitleDisplayCatalog catalog,
        IEnumerable<int>? observedOwnedIds, string language)
    {
        var observed = (observedOwnedIds ?? []).ToHashSet();
        return Array.AsReadOnly(catalog.Titles.Values.Where(title => !observed.Contains(title.Id))
            .SelectMany(title => (title.OwnedEffects ?? []).Select(effect => (title.Id, Effect: effect)))
            .GroupBy(row => Key(row.Effect)).Select(group =>
            {
                var effect = group.First().Effect;
                return new CombatTitleMissingPassive(group.Key, effect.LabelDe, effect.LabelEn,
                    group.Select(row => row.Id).Distinct().Count());
            }).OrderBy(row => PassiveLabel(row.LabelDe, row.LabelEn, language), StringComparer.Create(Culture(language), true))
            .ThenBy(row => row.Key.StatId, StringComparer.Ordinal).ThenBy(row => row.Key.Unit, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Metadata totals inside an explicitly estimated missing-ID set; never a native availability total.</summary>
    public static IReadOnlyList<CombatTitleMissingPassive> EstimatedMissingPassives(CombatTitleDisplayCatalog catalog,
        IEnumerable<int> missingIds, string language) => Array.AsReadOnly(OwnedPassives(catalog, missingIds, true, language).Rows
        .Select(row => new CombatTitleMissingPassive(row.Key, row.LabelDe, row.LabelEn, row.TitleCount)
        {
            EstimatedValue = row.Value, ValueStatus = row.Status
        }).ToArray());

    public static CombatTitlePassiveKey Key(CombatTitleDisplayEffect effect) => new(effect.StatId, effect.Unit);
    public static string PassiveKeyText(CombatTitlePassiveKey key) => key.StatId + "|" + (key.Unit ?? "Unknown");
    public static string PassiveLabel(string german, string english, string language) => language == "de" ? german : english;
    public static string PassiveValue(decimal? value, string? unit, string language) =>
        value is { } number && (unit is "Points" or "Percent")
            ? (number > 0 ? "+" : "") + number.ToString("0.##", Culture(language)) + (unit == "Percent" ? " %" : "") : "—";

    public static string GradeClass(string? grade) => grade switch
    {
        "Common" => "ct-grade-common", "Rare" => "ct-grade-rare", "Legend" => "ct-grade-legend",
        "Unique" => "ct-grade-unique", "Epic" => "ct-grade-epic", "Special" => "ct-grade-special", _ => "ct-grade-unknown"
    };

    public static string EffectLabel(CombatTitleDisplayEffect effect, string language) =>
        language == "de" ? effect.LabelDe : effect.LabelEn;
    public static string? MachineValue(CombatTitleDisplayEffect effect) => effect.Unit is "Points" or "Percent"
        ? effect.Value?.ToString(CultureInfo.InvariantCulture) : null;
    public static string EffectValue(CombatTitleDisplayEffect effect, string language) =>
        (effect.Unit is "Points" or "Percent") && effect.Value is { } value
            ? value.ToString("0.##", Culture(language)) + (effect.Unit == "Percent" ? " %" : "") : "—";

    public static string? CategoryIcon(string category) => category switch
    {
        "Attack" => "icons/combat/titles/offensive-tab.png", "Defense" => "icons/combat/titles/defensive-tab.png",
        "Etc" => "icons/combat/titles/other-tab.png", _ => null
    };
    private static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
}
