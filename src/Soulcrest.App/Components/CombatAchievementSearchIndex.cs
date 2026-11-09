using System.Globalization;
using System.Text;

namespace Soulcrest.App.Components;

/// <summary>Searches presentation names and exact title reward relationships; never infers progress.</summary>
public sealed class CombatAchievementSearchIndex
{
    private readonly Dictionary<int, Entry> _entries = [];

    public CombatAchievementSearchIndex(CombatAchievementDisplayCatalog catalog,
        CombatTitleDisplayCatalog titles, CombatTitleUnlockCatalog unlocks)
    {
        var linked = new Dictionary<(int Family, int Stage), List<string>>();
        if (catalog.MetadataBuild == titles.MetadataBuild && catalog.MetadataBuild == unlocks.MetadataBuild)
            foreach (var title in unlocks.Titles.Values)
            {
                if (titles.Find(title.Id) is not { } definition) continue;
                foreach (var route in title.Routes.Where(route => route.Kind == "AchievementRewardItem" && route.StageId is > 0))
                {
                    var key = (route.SourceId, route.StageId!.Value);
                    if (!linked.TryGetValue(key, out var names)) linked[key] = names = [];
                    names.Add(definition.NameDe); names.Add(definition.NameEn);
                }
            }
        foreach (var family in catalog.Achievements)
        {
            var name = Normalize(family.NameDe + " " + family.NameEn);
            var stages = family.Stages.Select(stage =>
            {
                var titleNames = linked.GetValueOrDefault((family.Id, stage.Id)) ?? [];
                return new Stage(stage.Id, Normalize(name + " " + stage.NameDe + " " + stage.NameEn + " " + Rewards(stage.Rewards)
                    + " " + string.Join(' ', titleNames)), titleNames.Select(Canonical).ToArray());
            }).ToArray();
            _entries[family.Id] = new(name, Normalize(name + " " + Rewards(family.Rewards)), stages);
        }
    }

    public bool Matches(int familyId, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var terms = Terms(query);
        if (terms.Length == 0) return true;
        return _entries.TryGetValue(familyId, out var entry) && (Contains(entry.Fallback, terms)
            || entry.Stages.Any(stage => Contains(stage.Text, terms)));
    }

    /// <summary>Plain family-name searches keep the live stage; a particular stage or reward opens that catalogue tier.</summary>
    public int? FindStage(int familyId, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var terms = Terms(query);
        if (terms.Length == 0 || !_entries.TryGetValue(familyId, out var entry)) return null;
        var exactTitle = Canonical(query);
        if (entry.Stages.FirstOrDefault(stage => stage.TitleNames.Contains(exactTitle, StringComparer.Ordinal)) is { } titleStage)
            return titleStage.Id;
        if (Contains(entry.Name, terms)) return null;
        return entry.Stages.FirstOrDefault(stage => Contains(stage.Text, terms))?.Id;
    }

    private static string Rewards(IReadOnlyList<CombatAchievementDisplayReward> rewards) => string.Join(' ',
        rewards.Select(reward => reward.NameDe + " " + reward.NameEn + " " + reward.Quantity?.ToString(CultureInfo.InvariantCulture)));
    private static bool Contains(string text, string[] terms) => terms.All(term => text.Contains(term, StringComparison.Ordinal));
    private static string[] Terms(string? query) => Normalize(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
    private static string Canonical(string value) => string.Join(' ', Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark) continue;
            if (character == 'ß') result.Append("ss");
            else result.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }
        return result.ToString();
    }
    private sealed record Entry(string Name, string Fallback, Stage[] Stages);
    private sealed record Stage(int Id, string Text, string[] TitleNames);
}
