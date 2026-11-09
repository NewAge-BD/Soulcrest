using System.Globalization;

namespace Soulcrest.Core.Daevanion;

public sealed record DaevanionPlannedStat(string StatId, decimal Amount, DaevanionStatUnit Unit);
public sealed record DaevanionPlannedSkill(int SkillId, DaevanionSkillKind Kind, int Levels);

/// <summary>Actual selected bonuses, with raw stat quantities and skill IDs kept separate from priority scores.</summary>
public sealed record DaevanionPlanSummary(
    IReadOnlyList<DaevanionPlannedStat> Stats, IReadOnlyList<DaevanionPlannedSkill> Skills)
{
    public static DaevanionPlanSummary Create(string classId, IEnumerable<DaevanionBoardPlan> plans, string? familyId = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var catalog = DaevanionCatalog.Default;
        var boards = catalog.Boards.Where(board => board.ClassId == classId && (familyId is null || board.FamilyId == familyId))
            .ToDictionary(board => board.Id);
        var seen = new HashSet<int>();
        var stats = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var skills = new Dictionary<int, int>();
        foreach (var plan in plans)
        {
            if (!boards.TryGetValue(plan.BoardId, out var board)) continue;
            var selected = plan.NodeIds.ToHashSet();
            foreach (var node in board.Nodes.Where(node => selected.Contains(node.Id) && seen.Add(node.Id)))
            foreach (var effect in node.Effects)
            {
                if (effect.StatId is { } statId)
                    stats[statId] = stats.GetValueOrDefault(statId) + decimal.Parse(effect.Amount, CultureInfo.InvariantCulture);
                else if (effect.SkillId is int skillId)
                    skills[skillId] = skills.GetValueOrDefault(skillId) + effect.LevelIncrement;
            }
        }
        return new(Array.AsReadOnly(stats.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new DaevanionPlannedStat(item.Key, item.Value, catalog.GetStat(item.Key)!.Unit)).ToArray()),
            Array.AsReadOnly(skills.OrderBy(item => item.Key)
                .Select(item => new DaevanionPlannedSkill(item.Key, catalog.GetSkill(item.Key)!.Kind, item.Value)).ToArray()));
    }
}
