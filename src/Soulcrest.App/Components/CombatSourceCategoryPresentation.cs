using System.Globalization;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Components;

/// <summary>Read-only selection and formatting. No source effects or combat-score contributions are calculated here.</summary>
public static class CombatSourceCategoryPresentation
{
    public static bool Supports(string section) => section is "wings" or "gear" or "pets" or "genus-insight"
        or "pantheon" or "achievements" or "titles";

    public static bool Includes(string section, CombatSourceKind kind) => section switch
    {
        "wings" => kind is CombatSourceKind.WingEquipped or CombatSourceKind.WingOwned,
        "gear" => kind is CombatSourceKind.Gear or CombatSourceKind.Enchantment or CombatSourceKind.EquipmentSet
            or CombatSourceKind.Soulbind or CombatSourceKind.Magicstone or CombatSourceKind.Godstone or CombatSourceKind.Arcana,
        "pets" => kind == CombatSourceKind.Pet,
        "genus-insight" => kind == CombatSourceKind.Genus,
        "achievements" => kind == CombatSourceKind.Achievement,
        "titles" => kind is CombatSourceKind.TitleOwned or CombatSourceKind.TitleEquipped,
        // Pantheon is the guild decoration system. It has no qualified source kind in this model.
        _ => false
    };

    public static bool IsCurrent<T>(CharacterCombatSnapshot snapshot, NetworkCombatDomain<T> domain) where T : class =>
        snapshot.IsFresh && domain.IsFresh && domain.Value is not null
        && domain.Status is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial;

    public static NetworkCombatSourceStatus ObservationStatus(CharacterCombatSnapshot snapshot, string section) => section switch
    {
        "gear" => Status(snapshot, snapshot.Equipment),
        "pets" or "genus-insight" => Status(snapshot, snapshot.Pets),
        "titles" => TitleObservationStatus(snapshot),
        "wings" => WingObservationStatus(snapshot),
        "achievements" => Status(snapshot, snapshot.Achievements),
        _ => NetworkCombatSourceStatus.Unknown
    };

    public static DateTimeOffset? ObservedAt(CharacterCombatSnapshot snapshot, string section) => section switch
    {
        "gear" => snapshot.Equipment.ObservedAt,
        "wings" => IsCurrent(snapshot, snapshot.WingOwnership) ? snapshot.WingOwnership.ObservedAt
            : snapshot.EquippedWing.ObservedAt ?? snapshot.WingOwnership.ObservedAt,
        "pets" or "genus-insight" => snapshot.Pets.ObservedAt,
        "titles" => IsCurrent(snapshot, snapshot.TitleOwnership) ? snapshot.TitleOwnership.ObservedAt
            : snapshot.Titles.ObservedAt ?? snapshot.TitleOwnership.ObservedAt,
        "achievements" => snapshot.Achievements.ObservedAt,
        _ => null
    };

    private static NetworkCombatSourceStatus Status<T>(CharacterCombatSnapshot snapshot, NetworkCombatDomain<T> domain) where T : class
    {
        if (!snapshot.IsFresh) return domain.Value is null ? NetworkCombatSourceStatus.Unknown : NetworkCombatSourceStatus.Stale;
        if (domain.Status == NetworkCombatSourceStatus.Conflict) return domain.Status;
        if (domain.Status == NetworkCombatSourceStatus.Stale || domain.Value is not null && !domain.IsFresh)
            return NetworkCombatSourceStatus.Stale;
        return domain.Value is null ? NetworkCombatSourceStatus.Unknown : domain.Status;
    }

    private static NetworkCombatSourceStatus TitleObservationStatus(CharacterCombatSnapshot snapshot)
    {
        var ownership = Status(snapshot, snapshot.TitleOwnership);
        var slots = Status(snapshot, snapshot.Titles);
        if (IsCurrent(snapshot, snapshot.TitleOwnership) || IsCurrent(snapshot, snapshot.Titles))
            return ownership == NetworkCombatSourceStatus.Complete && slots == NetworkCombatSourceStatus.Complete
                ? NetworkCombatSourceStatus.Complete : NetworkCombatSourceStatus.Partial;
        if (ownership == NetworkCombatSourceStatus.Conflict || slots == NetworkCombatSourceStatus.Conflict)
            return NetworkCombatSourceStatus.Conflict;
        return ownership == NetworkCombatSourceStatus.Stale || slots == NetworkCombatSourceStatus.Stale
            ? NetworkCombatSourceStatus.Stale : NetworkCombatSourceStatus.Unknown;
    }

    private static NetworkCombatSourceStatus WingObservationStatus(CharacterCombatSnapshot snapshot)
    {
        var owned = Status(snapshot, snapshot.WingOwnership);
        var equipped = Status(snapshot, snapshot.EquippedWing);
        if (IsCurrent(snapshot, snapshot.WingOwnership) || IsCurrent(snapshot, snapshot.EquippedWing))
            return owned == NetworkCombatSourceStatus.Complete && equipped == NetworkCombatSourceStatus.Complete
                ? NetworkCombatSourceStatus.Complete : NetworkCombatSourceStatus.Partial;
        if (owned == NetworkCombatSourceStatus.Conflict || equipped == NetworkCombatSourceStatus.Conflict)
            return NetworkCombatSourceStatus.Conflict;
        return owned == NetworkCombatSourceStatus.Stale || equipped == NetworkCombatSourceStatus.Stale
            ? NetworkCombatSourceStatus.Stale : NetworkCombatSourceStatus.Unknown;
    }

    private static bool TitleSourceCurrent(CharacterCombatSnapshot snapshot, CombatSourceKind kind) => kind switch
    {
        CombatSourceKind.TitleOwned => IsCurrent(snapshot, snapshot.TitleOwnership),
        CombatSourceKind.TitleEquipped => IsCurrent(snapshot, snapshot.Titles),
        _ => false
    };

    public static IReadOnlyList<CombatSourceReport> Reports(CharacterCombatSnapshot snapshot, string section)
    {
        if (!snapshot.IsFresh || !Supports(section) || section == "pantheon") return [];
        if (section is not "wings" && ObservationStatus(snapshot, section) is not
            (NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial)) return [];
        return snapshot.Breakdown?.Sources.Where(source => Includes(section, source.Kind)
            && source.Status != CombatKnowledgeStatus.Stale
            && (section != "titles" || TitleSourceCurrent(snapshot, source.Kind))).ToArray() ?? [];
    }

    public static decimal? ModelLoss(CharacterCombatSnapshot snapshot, string section, CombatSourceReport report) =>
        snapshot.IsFresh && snapshot.Breakdown?.Status == CombatKnowledgeStatus.Complete
            && report.Status == CombatKnowledgeStatus.Complete && Includes(section, report.Kind)
            && (section != "titles" || TitleSourceCurrent(snapshot, report.Kind))
            && (section == "wings" || ObservationStatus(snapshot, section) is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial)
                ? report.ModelLoss : null;

    public static string ItemLabel(int itemId)
    {
        var label = MonsterLootItemCatalog.Default.Name(itemId, UiText.Language);
        return label == "Item " + itemId.ToString(CultureInfo.InvariantCulture) ? UiText.T("Nicht zugeordneter Gegenstand") : label;
    }

    public static string PetLabel(int petId) => PetIdTable.Find(petId)?.En ?? UiText.T("Nicht zugeordnetes Pet");
    public static string SkillLabel(int skillId) => CombatEffectCatalog.Default.GetSkill(skillId) is not null
        ? CombatEffectCatalog.Default.SkillLabel(skillId, UiText.Language) : UiText.T("Nicht zugeordneter Skill");
    public static string SourceTitle(CombatSourceReport source, int index) =>
        UiText.F("{0} · Beitrag {1}", CombatScoreCalculatorPresentation.SourceKind(source.Kind), index + 1);
    public static string EffectValue(CombatEffect effect) => Signed(effect.Value)
        + (effect.Operation == CombatEffectOperation.IncreasePercent || CombatEffectCatalog.Default.GetStat(effect.StatId)?.Unit == CombatStatUnit.Percent ? " %" : "");
    public static string Signed(decimal? value) => value is { } number
        ? (number > 0 ? "+" : "") + CombatScoreCalculatorPresentation.ExactNumber(number) : "—";
    public static string Time(DateTimeOffset? at) => at?.ToLocalTime().ToString("g",
        CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US")) ?? "—";

    public static IReadOnlyList<ObservedGearOption> GearOptions(CharacterEquipmentDetail detail) =>
        detail.ArcanaMainStats.Select(value => new ObservedGearOption("Arcana", value))
            .Concat(detail.SubStats.Select(value => new ObservedGearOption("Statoption", value)))
            .Concat(detail.AdditionalSubStats.Select(value => new ObservedGearOption("Zusatzoption", value)))
            .Concat(detail.SoulAdditionalSubStats.Select(value => new ObservedGearOption("Seelenbindungsoption", value))).ToArray();

    public static CombatNativeNormalizedValue? OptionValue(ushort nativeId, long rawValue, CombatNativeValueDomain domain)
    {
        var value = CombatEffectCatalog.Default.GetNativeNormalized(nativeId, rawValue, domain);
        return value?.Role is CombatNativeStatRole.FlatComponent or CombatNativeStatRole.IncreasePercentComponent ? value : null;
    }

    public static string OptionDisplay(CombatNativeNormalizedValue value) => Signed(value.Value)
        + (value.Unit == CombatStatUnit.Percent || value.Role == CombatNativeStatRole.IncreasePercentComponent ? " %" : "");
    public sealed record ObservedGearOption(string Channel, CharacterStatValue Value);
}
