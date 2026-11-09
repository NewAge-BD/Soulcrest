using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Soulcrest.Core.Daevanion;

public sealed record DaevanionNodeRule(DaevanionNode Node, int Cost, IReadOnlyList<int> NeighborIds);
public sealed record DaevanionBoardRules(DaevanionBoard Board, DaevanionBudgetPool Pool,
    int MinimumLevel, IReadOnlyList<DaevanionNodeRule> Nodes)
{
    public int MaximumCost => Nodes.Sum(node => node.Cost);
}

/// <summary>Offline planning rules qualified separately from display/observed activation data.</summary>
public sealed class DaevanionRuleCatalog
{
    private readonly FrozenDictionary<int, DaevanionBoardRules> _byId;
    public static DaevanionRuleCatalog Default { get; } = new();
    public string Version => "2026-11-02-v4";
    public string CatalogFingerprint { get; }
    public IReadOnlyList<DaevanionBoardRules> Boards { get; }

    private DaevanionRuleCatalog()
    {
        var catalog = DaevanionCatalog.Default;
        CatalogFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(catalog.Boards))));
        Boards = Array.AsReadOnly(catalog.Boards.Select(board =>
        {
            var positions = board.Nodes.ToDictionary(node => (node.Row, node.Column));
            var nodes = board.Nodes.Select(node => new DaevanionNodeRule(node, Cost(node),
                Array.AsReadOnly(new[] { (node.Row - 1, node.Column), (node.Row, node.Column - 1),
                    (node.Row, node.Column + 1), (node.Row + 1, node.Column) }
                    .Where(positions.ContainsKey).Select(position => positions[position].Id).ToArray()))).ToArray();
            return new DaevanionBoardRules(board, board.FamilyId switch
            {
                "ariel" => DaevanionBudgetPool.Ariel,
                "azphel" => DaevanionBudgetPool.Azphel,
                _ => DaevanionBudgetPool.Normal
            }, board.FamilyId switch { "nezekan" => 12, "zikel" => 20, "vaizel" => 30,
                "triniel" => 40, "ariel" or "azphel" => 45, _ => throw new InvalidDataException("Unknown board family.") },
                Array.AsReadOnly(nodes));
        }).ToArray());
        _byId = Boards.ToFrozenDictionary(board => board.Board.Id);
    }

    public DaevanionBoardRules? GetBoardRules(int boardId) => _byId.GetValueOrDefault(boardId);
    public IReadOnlyList<DaevanionBoardRules> ForClass(string classId) => Boards
        .Where(board => board.Board.ClassId == classId).OrderBy(board =>
            Array.IndexOf(DaevanionCatalog.Families.ToArray(), board.Board.FamilyId)).ToArray();

    // User confirmation 2026-10-31: normal stats 1, green/passive 2, blue/active 3,
    // orange 4, including the green/blue/orange stat tiers on Ariel and Azphel.
    private static int Cost(DaevanionNode node) => node.Kind == DaevanionNodeKind.Start ? 0 : node.Quality switch
    {
        DaevanionNodeQuality.Common => 1,
        DaevanionNodeQuality.Rare => 2,
        DaevanionNodeQuality.Legend => 3,
        DaevanionNodeQuality.Unique => 4,
        _ => throw new InvalidDataException("Unqualified Daevanion node cost.")
    };
}

public static class DaevanionPlanningDefaults
{
    public static DaevanionOptimizationRequest CreateRequest(string classId)
    {
        var catalog = DaevanionCatalog.Default;
        if (!catalog.Classes.Any(row => row.Id == classId)) throw new ArgumentException("Unknown class.", nameof(classId));
        var effects = catalog.Boards.Where(board => board.ClassId == classId).SelectMany(board => board.Nodes)
            .SelectMany(node => node.Effects).ToArray();
        var statIds = effects.Where(effect => effect.StatId is not null).Select(effect => effect.StatId!).ToHashSet();
        var skillIds = effects.Where(effect => effect.SkillId is not null).Select(effect => effect.SkillId!.Value).ToHashSet();
        var rows = catalog.Stats.Where(stat => statIds.Contains(stat.Id))
            .OrderBy(stat => stat.Name.En, StringComparer.OrdinalIgnoreCase).ThenBy(stat => stat.Id, StringComparer.Ordinal)
            .Select(stat => new DaevanionPriority(DaevanionPriorityKind.Stat, stat.Id)).ToList();
        foreach (var kind in new[] { DaevanionSkillKind.Active, DaevanionSkillKind.Passive })
            rows.AddRange(catalog.Skills.Where(skill => skillIds.Contains(skill.Id) && skill.Kind == kind)
                .OrderBy(skill => skill.Name.En, StringComparer.OrdinalIgnoreCase).ThenBy(skill => skill.Id)
                .Select(skill => new DaevanionPriority(kind == DaevanionSkillKind.Active ? DaevanionPriorityKind.Skill
                    : DaevanionPriorityKind.Passive, skill.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return new() { ClassId = classId, Priorities = rows.ToArray() };
    }
}
