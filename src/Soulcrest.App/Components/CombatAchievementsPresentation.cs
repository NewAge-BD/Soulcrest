using System.Globalization;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Components;

/// <summary>A catalogue preference or a current stage supplied by the qualified projection.</summary>
public sealed record CombatAchievementStageSelection(int? StageId, bool IsLive);

/// <summary>Read-only display helpers; raw counter qualification remains in the independent projection.</summary>
public static class CombatAchievementsPresentation
{
    public static string Label(string german, string english, string language) => language == "de" ? german : english;
    public static string Number(long? value, string language) => value?.ToString("N0",
        CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US")) ?? "—";

    public static string ProgressValue(long? value, CombatAchievementProgressUnit unit, string language)
    {
        if (value is not { } number || unit == CombatAchievementProgressUnit.Unknown) return "—";
        if (unit == CombatAchievementProgressUnit.Minutes)
        {
            var hours = number / 60; var minutes = number % 60;
            var hourLabel = language == "de" ? "Std." : "h";
            var minuteLabel = language == "de" ? "Min." : "min";
            if (hours == 0) return Number(minutes, language) + " " + minuteLabel;
            return Number(hours, language) + " " + hourLabel + (minutes == 0 ? "" : " " + Number(minutes, language) + " " + minuteLabel);
        }
        var formatted = Number(number, language);
        return unit.ToString() switch
        {
            "Level" => (language == "de" ? "Stufe " : "Level ") + formatted,
            "Seconds" => formatted + " s",
            "Items" => formatted + (language == "de" ? " Stück" : " items"),
            "Operations" => formatted + (language == "de" ? " Vorgänge" : " operations"),
            "Experience" => formatted + (language == "de" ? " EP" : " XP"),
            "Rank" => (language == "de" ? "Rang " : "Rank ") + formatted,
            "Percent" => formatted + " %",
            _ => formatted
        };
    }

    /// <summary>Formats one value; callers use a validated display pair consistently for text and progress bars.</summary>
    public static string ProgressDisplay(decimal? display, long? raw, CombatAchievementProgressUnit unit, string language)
    {
        if (display is not { } value) return ProgressValue(raw, unit, language);
        if (unit == CombatAchievementProgressUnit.Unknown) return "—";
        if (unit == CombatAchievementProgressUnit.Minutes && value == decimal.Truncate(value)
            && value is >= long.MinValue and <= long.MaxValue)
            return ProgressValue((long)value, unit, language);
        var number = value.ToString("#,0.############################",
            CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US"));
        return unit.ToString() switch
        {
            "Level" => (language == "de" ? "Stufe " : "Level ") + number,
            "Minutes" => number + (language == "de" ? " Min." : " min"),
            "Seconds" => number + " s",
            "Items" => number + (language == "de" ? " Stück" : " items"),
            "Operations" => number + (language == "de" ? " Vorgänge" : " operations"),
            "Experience" => number + (language == "de" ? " EP" : " XP"),
            "Rank" => (language == "de" ? "Rang " : "Rank ") + number,
            "Percent" => number + " %",
            _ => number
        };
    }

    public static string AreaLabel(AchievementArea area) => area switch
    {
        AchievementArea.Normal => "Sammlung", AchievementArea.Season => "Saison",
        AchievementArea.Event => "Events", AchievementArea.BattlePass => "Battle Pass",
        AchievementArea.Attendance => "Anwesenheit", AchievementArea.Onboarding => "Einführung",
        _ => "Katalogreferenzen"
    };

    public static string? HistoryLabel(AchievementMilestone milestone, CombatAchievementDisplayCatalog catalog, string language)
    {
        var family = milestone.FamilyId is { } familyId ? catalog.Find(familyId) : null;
        if (milestone.StageId is { } stageId && family?.Stages.FirstOrDefault(stage => stage.Id == stageId) is { } stage)
            return Label(stage.NameDe, stage.NameEn, language);
        if (family is { HasNativeName: true }) return Label(family.NameDe, family.NameEn, language);
        var savedLabel = language == "de" ? milestone.LabelDe : milestone.LabelEn;
        if (!string.IsNullOrWhiteSpace(savedLabel)) return savedLabel;
        return milestone.Kind == AchievementMilestoneKind.PeriodChanged
            ? Label("Neuer Erfolgszeitraum", "New achievement period", language) : null;
    }

    public static string HistoryKind(AchievementMilestoneKind kind) => kind switch
    {
        AchievementMilestoneKind.Completed => "Abschluss erstmals erkannt",
        AchievementMilestoneKind.RewardClaimed => "Belohnungsabholung erstmals erkannt",
        _ => "Zeitraumwechsel erstmals erkannt"
    };

    public static string ObservationTime(DateTimeOffset value, string language) => value.ToLocalTime().ToString("g",
        CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US"));

    /// <summary>The producer supplies real own updates; a numeric change grants no semantic completion or unit.</summary>
    public static AchievementActivity? Activity(AchievementViewState state, int familyId) => state.IsLive
        && state.Activities.TryGetValue(familyId, out var activity) && activity.FamilyId == familyId
        && (activity.PreviousValue is null || activity.PreviousValue != activity.CurrentValue) ? activity : null;

    /// <summary>A reported family value remains useful without a qualified unit, goal or completion rule.</summary>
    public static int? ObservedValue(AchievementViewState state, int familyId) =>
        state.Source is AchievementViewSource.Live or AchievementViewSource.Saved
        && state.ObservedFamilyIds.Contains(familyId) && state.ObservedValues.TryGetValue(familyId, out var value) ? value : null;

    public static string ActivityTime(DateTimeOffset value, string language) => value.ToLocalTime().ToString("G",
        CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US"));

    public static CombatAchievementStageSelection StageSelection(CombatAchievementDisplayDefinition achievement,
        CombatAchievementFamilyProgress? family, int? selectedPreference)
    {
        if (selectedPreference is { } selected && achievement.Stages.Any(stage => stage.Id == selected)) return new(selected, false);
        if (family?.CurrentStageId is { } current) return new(current, true);
        return new(achievement.Stages.FirstOrDefault()?.Id, false);
    }

    public static string? ClaimStatus(CombatAchievementStageProgress? facts) => facts?.Claimed is true ? "Belohnung abgeholt"
        : facts is null ? null : facts.Claimed is null ? "Belohnungsstatus unbekannt"
        : facts.Completed is true ? "Belohnung offen" : null;

    public static bool IsCompletedCard(CombatAchievementFamilyProgress? family, int? stageId) =>
        family is not null && family.Status != CombatKnowledgeStatus.Conflict
        && stageId is { } id && family.Find(id)?.Completed is true;

    public static string? ProgressPercent(long? current, long? target) => current is { } value && target is > 0
        ? Math.Clamp((decimal)value / target.Value * 100m, 0m, 100m).ToString("0.####", CultureInfo.InvariantCulture) + "%" : null;

    public static string? Scope(CombatAchievementGroupDefinition group, string language) => language == "de" ? group.ScopeDe : group.ScopeEn;

    public static IReadOnlyList<CombatAchievementGroupDefinition> Groups(CombatAchievementDisplayCatalog catalog,
        string category, string language = "en", IReadOnlySet<int>? scopeIds = null)
    {
        var occupied = catalog.Achievements.Where(row => !AchievementContentPolicy.IsRetired(row.Id, catalog.MetadataBuild))
            .Where(row => scopeIds is null || scopeIds.Contains(row.Id))
            .Where(row => category == "All" || row.CategoryId == category)
            .Select(row => row.GroupId).OfType<int>().ToHashSet();
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US"), true);
        return Array.AsReadOnly(catalog.Groups.Where(group => occupied.Contains(group.Id)
                && (category == "All" || group.CategoryId == category))
            .OrderBy(group => !group.HasNativeName)
            .ThenBy(group => Label(group.NameDe, group.NameEn, language), comparer)
            .ThenBy(group => Scope(group, language), comparer).ThenBy(group => group.Id).ToArray());
    }

    public static IReadOnlyList<CombatAchievementDisplayDefinition> Rows(CombatAchievementDisplayCatalog catalog,
        string category, int? group, string language, bool observedOnly = false, CombatAchievementProgressSnapshot? progress = null,
        IReadOnlySet<int>? observedIds = null, IReadOnlySet<int>? scopeIds = null) => Array.AsReadOnly(catalog.Achievements
        .Where(row => !AchievementContentPolicy.IsRetired(row.Id, catalog.MetadataBuild))
        .Where(row => scopeIds is null || scopeIds.Contains(row.Id))
        .Where(row => category == "All" || row.CategoryId == category)
        .Where(row => group is null || row.GroupId == group)
        .Where(row => !observedOnly || (observedIds is not null ? observedIds.Contains(row.Id) : progress?.Find(row.Id) is not null))
        .OrderBy(row => !row.HasNativeName)
        .ThenBy(row => Label(row.NameDe, row.NameEn, language), StringComparer.Create(
            CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US"), true))
        .ThenBy(row => row.Id).ToArray());
}
