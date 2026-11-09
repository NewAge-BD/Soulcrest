using Soulcrest.App.Services;

namespace Soulcrest.App.Components;

public sealed record CombatWorkspaceSection(string Id, string Label);

/// <summary>Presentation routes only. Source availability and score eligibility remain in their own models.</summary>
public static class CombatWorkspaceCatalog
{
    public static IReadOnlyList<CombatWorkspaceSection> Sections { get; } = Array.AsReadOnly(new[]
    {
        new CombatWorkspaceSection("stat-overview", "Stat Overview"),
        new CombatWorkspaceSection("daevanion", "Daevanion Board"),
        new CombatWorkspaceSection("wings", "Wings"),
        new CombatWorkspaceSection("gear", "Gear"),
        new CombatWorkspaceSection("pets", "Pets"),
        new CombatWorkspaceSection("genus-insight", "Genus Insight"),
        new CombatWorkspaceSection("pantheon", "Pantheon"),
        new CombatWorkspaceSection("achievements", "Achievements"),
        new CombatWorkspaceSection("titles", "Titles")
    });

    public static string NormalizeSection(string? section) => Sections.Any(item => item.Id == section) ? section! : "stat-overview";
    public static string SectionLabel(string? section) => Sections.First(item => item.Id == NormalizeSection(section)).Label;
    public static string? IconPath(string? key) => key is not null && Icons.TryGetValue(key, out var path) ? path : null;
    public static string StatLabel(CombatStatOverviewRowDefinition row) => UiText.Language == "de" ? row.LabelDe : row.LabelEn;
    public static string GroupLabel(CombatStatOverviewGroupDefinition group) => UiText.Language == "de" ? group.LabelDe : group.LabelEn;
    // A native overview symbol is the neutral presentation fallback, never a stat-ID binding.
    public static string? GroupIconPath(string key) => IconPath(key switch
    {
        "primary-attributes" => "primary", "god-attributes" => "god", "main-stats" => "main",
        "combat" => "subcombat", _ => key
    }) ?? IconPath("stat-overview");

    private static readonly IReadOnlyDictionary<string, string> Icons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["stat-overview"] = "combat-icons/stat-overview.png", ["daevanion"] = "combat-icons/daevanion.png",
        ["wings"] = "combat-icons/wings.png", ["gear"] = "combat-icons/gear.png", ["pets"] = "combat-icons/pets.png",
        ["genus-insight"] = "combat-icons/pets.png", ["pantheon"] = "combat-icons/pantheon.png",
        ["achievements"] = "combat-icons/achievements.png", ["titles"] = "combat-icons/titles.png",
        ["main"] = "combat-icons/stat-overview.png", ["primary"] = "combat-icons/stat-overview.png", ["god"] = "combat-icons/stat-overview.png",
        ["Might"] = "combat-icons/might.png", ["Dexterity"] = "combat-icons/dexterity.png",
        ["Intelligence"] = "combat-icons/intelligence.png", ["Constitution"] = "combat-icons/constitution.png",
        ["Precision"] = "combat-icons/precision.png", ["Willpower"] = "combat-icons/willpower.png",
        ["Justice"] = "combat-icons/justice.png", ["Freedom"] = "combat-icons/freedom.png",
        ["Illusion"] = "combat-icons/illusion.png", ["Life"] = "combat-icons/life.png", ["Time"] = "combat-icons/time.png",
        ["Destruction"] = "combat-icons/destruction.png", ["Death"] = "combat-icons/death.png",
        ["Wisdom"] = "combat-icons/wisdom.png", ["Destiny"] = "combat-icons/destiny.png",
        ["Space"] = "combat-icons/space.png", ["Light"] = "combat-icons/light.png", ["Dark"] = "combat-icons/dark.png",
        ["subcombat"] = "combat-icons/subcombat.png", ["hit-detection"] = "combat-icons/hit-detection.png",
        ["status-effects"] = "combat-icons/status-effects.png", ["pvp"] = "combat-icons/pvp.png", ["pve"] = "combat-icons/pve.png",
        ["genus"] = "combat-icons/pets.png"
    };
}
