using System.Globalization;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Components;

public static class CombatScoreCalculatorPresentation
{
    public static string PetName(int id) => PetIdTable.Find(id)?.En ?? UiText.F("Pet {0}", id);
    public static string ItemName(int id) => MonsterLootItemCatalog.Default.Name(id, UiText.Language);
    // Only the existing typed gear source contract carries an item identity.
    public static int? SourceItemId(CombatSourceReport source) => source.SourceId.Split(':') is ["gear", _, _, var item, ..]
        && int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    public static string SourceTitle(CombatSourceReport source, int index)
    {
        var parts = source.SourceId.Split(':');
        if (parts is ["gear", _, _, var item, ..] && int.TryParse(item, out var itemId)) return ItemName(itemId);
        if (parts is ["pet", var pet, ..] && int.TryParse(pet, out var petId)) return PetName(petId);
        if (parts is ["daevanion", var board, ..] && int.TryParse(board, out var boardId))
            return DaevanionRuleCatalog.Default.GetBoardRules(boardId)?.Board.Name.For(UiText.Language) ?? SourceKind(source.Kind);
        if (parts is ["title-owned" or "title-equipped", var title, ..] && int.TryParse(title, out var titleId)) return UiText.F("Titel {0}", titleId);
        if (source.Skills.Count == 1) return SkillName(source.Skills[0].SkillId);
        return UiText.F("{0} · Beitrag {1}", SourceKind(source.Kind), index + 1);
    }
    public static string Reason(string value)
    {
        if (value.StartsWith("Das bestätigte Gesamtbudget fehlt: ", StringComparison.Ordinal))
        {
            var token = value["Das bestätigte Gesamtbudget fehlt: ".Length..].TrimEnd('.');
            return UiText.F("Das bestätigte Gesamtbudget fehlt: {0}.", Enum.TryParse<DaevanionBudgetPool>(token, out var pool) ? PoolName(pool) : token);
        }
        var message = value switch
        {
            "Missing qualified class/level base-stat row." => "Grundwerte für Klasse und Charakterlevel sind noch nicht qualifiziert.",
            "Current title ownership and equipped effects are unknown." => "Titelbesitz und ausgerüstete Titel sind noch unbekannt.",
            "Current equipped items and options are unknown." => "Ausgerüstete Gegenstände und Optionen sind noch unbekannt.",
            "Current achievement state is unknown." => "Der aktuelle Erfolgsstand ist noch unbekannt.",
            "Current pet levels and genus panels are unknown." => "Petstufen und Gattungspanels sind noch unbekannt.",
            "An absent board does not establish an empty node list." => "Daevanion: Nicht aufgeführte Boards sind noch unbekannt.",
            "Observed board nodes do not match the selected class catalog." => "Daevanion: Beobachtete Nodes passen nicht zum Klassenkatalog.",
            "Opened board nodes and observed Daevanion skill bonuses disagree." => "Daevanion: Aktivierte Nodes und Skillboni widersprechen sich.",
            "The complete character source scope is not known." => "Die Charakterquellen sind noch nicht vollständig bekannt.",
            "Effect values are known; their current application is not qualified." => "Effektwerte sind bekannt; ihre aktuelle Anwendung ist ungeklärt.",
            _ when value.StartsWith("Passive application", StringComparison.Ordinal) => "Passive Skills: Anwendung oder Effektkurve ist ungeklärt.",
            _ when value.StartsWith("Missing primary-stat", StringComparison.Ordinal) => "Gottheits- oder Grundattribut: Die Ableitungsregel fehlt.",
            _ when value.StartsWith("Unknown skill component", StringComparison.Ordinal) => "Skillstufen: Ein Bonusanteil ist noch unbekannt.",
            _ when value.StartsWith("Missing combat-score weight", StringComparison.Ordinal) => "Für einen Statwert fehlt eine qualifizierte Gewichtung.",
            _ when value.StartsWith("No qualified pet", StringComparison.Ordinal) => "Pet-Besitz: Die Zuordnung zur Effektgruppe fehlt.",
            _ when value.Contains("pet stat-group", StringComparison.Ordinal) => "Pet-Besitz: Die Anwendung der Effektgruppe ist ungeklärt.",
            _ when value.StartsWith("Selected genus", StringComparison.Ordinal) => "Gattungsverständnis: Aktives Panel oder Statzuordnung ist ungeklärt.",
            _ when value.StartsWith("Enchantment state", StringComparison.Ordinal) => "Verzauberung: Die passende Anwendungsregel ist ungeklärt.",
            _ when value.StartsWith("Soulbind state", StringComparison.Ordinal) => "Seelenbindung: Die Anwendungsregel ist ungeklärt.",
            _ when value.StartsWith("Achievement state", StringComparison.Ordinal) => "Erfolge: Dauerhafte Effekte und Belohnungszuordnung sind noch ungeklärt.",
            _ when value.StartsWith("Some equipment", StringComparison.Ordinal) => "Ausrüstung: Statzuordnung oder Skalierung ist noch ungeklärt.",
            _ when value.StartsWith("The observed skill", StringComparison.Ordinal) => "Skills: Für weitere beobachtete Skills fehlt eine qualifizierte Kategorie oder Effektregel.",
            _ when value.StartsWith("Skill bonus is observed", StringComparison.Ordinal) => "Daevanion: Die Skillboni sind bekannt, ihre Boardverteilung ist unvollständig.",
            _ when value.StartsWith("Missing skill IDs", StringComparison.Ordinal) => "Skills: Fehlende Einträge beweisen keine erlernte Stufe 0.",
            _ when value.StartsWith("The combat-power eligibility", StringComparison.Ordinal) => "Zustandseffekte: Ihre Berücksichtigung im CP-Modell ist ungeklärt.",
            _ when value.StartsWith("No qualified offline", StringComparison.Ordinal) || value.StartsWith("Unqualified stat", StringComparison.Ordinal)
                || value.StartsWith("Source scope", StringComparison.Ordinal) => "Für eine Quelle fehlt ein vollständiger Anwendungs- oder Effektbeleg.",
            _ when value.StartsWith("Ein aktueller", StringComparison.Ordinal) || value.StartsWith("Das aktuelle", StringComparison.Ordinal)
                || value.StartsWith("Der aktuelle", StringComparison.Ordinal) || value.StartsWith("Für die CP", StringComparison.Ordinal)
                || value.StartsWith("Quellen werden", StringComparison.Ordinal) || value.StartsWith("Die Charakterquellen", StringComparison.Ordinal) => value,
            _ => "Eine weitere Quellenregel ist noch nicht vollständig qualifiziert."
        };
        return UiText.T(message);
    }
    public static string Number(decimal? value) => value?.ToString("0.##", CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US")) ?? "—";
    public static string ExactNumber(decimal? value) => value?.ToString("0.############################", CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US")) ?? "—";
    public static string DisplayStatLabel(CharacterDisplayStatRow row) => UiText.Language == "de" ? row.LabelDe : row.LabelEn;
    public static string DisplayStatValue(CharacterDisplayStatRow row) => row.Value is null ? "—"
        : ExactNumber(row.Value) + (row.Unit == CombatStatUnit.Percent ? " %" : "");
    public static string DisplayMachineValue(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    public static string DisplayCategory(CharacterDisplayStatCategory category) => UiText.T(category == CharacterDisplayStatCategory.Main ? "Hauptwerte" : "Nebenwerte");
    public static string DisplayInputRole(CharacterDisplayInputRole role) => UiText.T(role == CharacterDisplayInputRole.IncreaseBasisPoints
        ? "Verstärkung (Basispunkte)" : "Rohbeitrag");
    public static string DisplayQualification(CharacterDisplayStatQualification qualification) => UiText.T(qualification switch
    {
        CharacterDisplayStatQualification.NativeDirect => "Direkt beobachtet",
        CharacterDisplayStatQualification.ControlledChange => "Abgeleitet · Änderungstest",
        CharacterDisplayStatQualification.ObservedMatch => "Abgeleitet · Zahlenabgleich",
        CharacterDisplayStatQualification.TableInferred => "Aus Rolltabellen abgeleitet",
        _ => "Zuordnung ungeklärt"
    });
    public static string DisplayStatStatus(CharacterDisplayStatRow row) => UiText.T(row.Status switch
    {
        CombatKnowledgeStatus.Stale => "Veralteter Stand",
        CombatKnowledgeStatus.Conflict => "Widerspruch",
        _ when row.Value is null && row.MissingInputIds.Count > 0 => "Fehlende Eingaben",
        _ when row.Value is null => "Wert unbekannt",
        _ when row.Qualification == CharacterDisplayStatQualification.Unknown => "Zuordnung ungeklärt",
        CombatKnowledgeStatus.Complete => "Aktuell",
        _ => "Teilweise belegt"
    });
    public static string DisplayStatStateClass(CharacterDisplayStatRow row) => row.Status switch
    {
        CombatKnowledgeStatus.Stale => "stale",
        CombatKnowledgeStatus.Conflict => "conflict",
        _ when row.ComparisonStatus == CombatKnowledgeStatus.Conflict => "conflict",
        _ when row.Value is null => "missing",
        _ when row.Qualification == CharacterDisplayStatQualification.Unknown => "unqualified",
        CombatKnowledgeStatus.Complete => "known",
        _ => "partial"
    };
    public static CharacterDisplayStatCounts CountDisplayStats(IEnumerable<CharacterDisplayStatRow> rows)
    {
        var values = rows.ToArray();
        return new(values.Length,
            values.Count(row => row.Value is not null && row.Status == CombatKnowledgeStatus.Complete && row.Qualification != CharacterDisplayStatQualification.Unknown),
            values.Count(row => row.Value is null), values.Count(row => row.Status == CombatKnowledgeStatus.Stale),
            values.Count(row => row.Qualification == CharacterDisplayStatQualification.Unknown),
            values.Count(row => row.Status == CombatKnowledgeStatus.Conflict || row.ComparisonStatus == CombatKnowledgeStatus.Conflict));
    }
    public sealed record CharacterDisplayStatCounts(int Total, int Known, int Missing, int Stale, int Unqualified, int Conflicts);
    public static string ScoreTermExplanation(CombatScoreStatTerm term) => UiText.T(term.IsIntermediate
        ? "Primärattribut · wirkt über seine Effekte"
        : term.Coefficient is null ? "Gewicht unbekannt"
        : term.Coefficient == 0 ? "Gewicht 0 laut eigenem Modell"
        : term.Value is null ? "Wert unbekannt" : "Rohkomponente × gemessenes Gewicht");
    /// <summary>Measured interval of a weight (own controlled contrasts, research 2026-11-09).</summary>
    public static string? WeightInterval(CombatScoreStatTerm term) => term.Coefficient is null
        || CombatScoreWeights.Get(term.StatId) is not { } weight ? null
        : UiText.F("gemessen {0}–{1}", ExactNumber(weight.Lower), ExactNumber(weight.Upper));
    public static string StatName(string id) => DaevanionCatalog.Default.GetStat(id)?.Name.For(UiText.Language) ?? CombatEffectCatalog.Default.StatLabel(id, UiText.Language);
    public static string SkillName(int id) => DaevanionCatalog.Default.GetSkill(id)?.Name.For(UiText.Language)
        ?? (CombatEffectCatalog.Default.GetSkill(id) is not null ? CombatEffectCatalog.Default.SkillLabel(id, UiText.Language) : UiText.F("Skill {0}", id));
    public static string ClassName(string? id) => DaevanionCatalog.Default.Classes.FirstOrDefault(item => item.Id == id)?.Name.For(UiText.Language) ?? "—";
    public static string PoolName(DaevanionBudgetPool pool) => pool switch { DaevanionBudgetPool.Normal => UiText.T("Daevanion-Punkte"), DaevanionBudgetPool.Ariel => "Ariel", _ => "Azphel" };
    public static string Status(NetworkCombatSourceStatus status) => UiText.T(status switch
    { NetworkCombatSourceStatus.Complete => "Vollständig beobachtet", NetworkCombatSourceStatus.Partial => "Teilweise bekannt",
        NetworkCombatSourceStatus.Stale => "Veralteter Stand", NetworkCombatSourceStatus.Conflict => "Widerspruch", _ => "Unbekannt" });
    public static string Status(CombatKnowledgeStatus status) => Status(status switch
    { CombatKnowledgeStatus.Complete => NetworkCombatSourceStatus.Complete, CombatKnowledgeStatus.Partial => NetworkCombatSourceStatus.Partial,
        CombatKnowledgeStatus.Stale => NetworkCombatSourceStatus.Stale, CombatKnowledgeStatus.Conflict => NetworkCombatSourceStatus.Conflict,
        _ => NetworkCombatSourceStatus.Unknown });
    public static string SourceKind(CombatSourceKind kind) => UiText.T(kind switch
    {
        CombatSourceKind.Base => "Klasse und Charakterlevel", CombatSourceKind.Skill => "Skillstufen", CombatSourceKind.Passive => "Passive Skills",
        CombatSourceKind.Gear => "Ausrüstung", CombatSourceKind.Enchantment => "Verzauberung und Durchbruch", CombatSourceKind.EquipmentSet => "Setboni",
        CombatSourceKind.Soulbind => "Seelenbindung", CombatSourceKind.Magicstone => "Manasteine", CombatSourceKind.Godstone => "Gottsteine",
        CombatSourceKind.Arcana => "Arcana", CombatSourceKind.Daevanion => "Daevanion", CombatSourceKind.TitleEquipped => "Ausgerüstete Titel",
        CombatSourceKind.TitleOwned => "Passive Titelboni", CombatSourceKind.Pet => "Pet-Besitz", CombatSourceKind.Genus => "Gattungsverständnis",
        CombatSourceKind.WingEquipped => "Ausgerüstete Flügel", CombatSourceKind.WingOwned => "Flügelbesitz", CombatSourceKind.Monolith => "Monolithen",
        CombatSourceKind.Skin => "Garderobe", CombatSourceKind.Achievement => "Erfolge", CombatSourceKind.Fixed => "Weitere feste Boni", _ => "Weitere Quellen"
    });
    public static string Application(CombatApplication application) => UiText.T(application switch
    { CombatApplication.Confirmed => "Anwendung bestätigt", CombatApplication.Inactive => "Nicht aktiv", _ => "Anwendung ungeklärt" });
}
