using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.Core.Daevanion;

public enum DaevanionNodeKind { Start, Stat, Skill }
public enum DaevanionNodeQuality { None, Common, Rare, Legend, Unique }
public enum DaevanionEffectKind { StatBonus, SkillLevelBonus }
public enum DaevanionSkillKind { Active, Passive }
public enum DaevanionStatUnit { Points, Percent }

public sealed record DaevanionText(string De, string En)
{
    public static DaevanionText Empty { get; } = new("", "");
    public string For(string? language) => language?.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? De : En;
}

public sealed record DaevanionClass
{
    public string Id { get; init; } = "";
    public DaevanionText Name { get; init; } = DaevanionText.Empty;
}

public sealed record DaevanionStat
{
    public string Id { get; init; } = "";
    public DaevanionText Name { get; init; } = DaevanionText.Empty;
    public DaevanionText ShortLabel { get; init; } = DaevanionText.Empty;
    public DaevanionStatUnit Unit { get; init; }
}

public sealed record DaevanionSkill
{
    public int Id { get; init; }
    public string ClassId { get; init; } = "";
    public DaevanionText Name { get; init; } = DaevanionText.Empty;
    public DaevanionSkillKind Kind { get; init; }
    public string ImageAssetId { get; init; } = "";
}

public sealed record DaevanionAsset
{
    public string Id { get; init; } = "";
    /// <summary>Local path relative to the application's wwwroot.</summary>
    public string Path { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed record DaevanionEffect
{
    public DaevanionEffectKind Kind { get; init; }
    public string? StatId { get; init; }
    public int? SkillId { get; init; }
    public string Amount { get; init; } = "";
    public int LevelIncrement { get; init; }
    public DaevanionText DisplayAmount { get; init; } = DaevanionText.Empty;
    public DaevanionText ShortLabel { get; init; } = DaevanionText.Empty;
}

public sealed record DaevanionNode
{
    public int Id { get; init; }
    /// <summary>Original one-based 15 by 15 board coordinates, including empty margins.</summary>
    public int Row { get; init; }
    public int Column { get; init; }
    public DaevanionNodeKind Kind { get; init; }
    public DaevanionNodeQuality Quality { get; init; }
    public DaevanionText Title { get; init; } = DaevanionText.Empty;
    public DaevanionText? Description { get; init; }
    public string FrameAssetId { get; init; } = "";
    public IReadOnlyList<DaevanionEffect> Effects { get; init; } = [];
}

public sealed record DaevanionBoard
{
    public int Id { get; init; }
    public string ClassId { get; init; } = "";
    public string FamilyId { get; init; } = "";
    public DaevanionText Name { get; init; } = DaevanionText.Empty;
    public IReadOnlyList<DaevanionNode> Nodes { get; init; } = [];
}

/// <summary>Verified offline board display data; contains no player state or unlock rules.</summary>
public sealed class DaevanionCatalog
{
    private const string ResourceName = "Soulcrest.DaevanionCatalog.json";
    private static readonly string[] FamilyOrder = ["nezekan", "zikel", "vaizel", "triniel", "azphel"];
    private static readonly Lazy<DaevanionCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<(string ClassId, string FamilyId), DaevanionBoard> _boards;
    private readonly FrozenDictionary<int, DaevanionSkill> _skills;
    private readonly FrozenDictionary<string, DaevanionStat> _stats;
    private readonly FrozenDictionary<string, DaevanionAsset> _assets;

    public static DaevanionCatalog Default => Embedded.Value;
    public static IReadOnlyList<string> Families { get; } = Array.AsReadOnly(FamilyOrder);
    public string DataVersion { get; }
    public IReadOnlyList<DaevanionClass> Classes { get; }
    public IReadOnlyList<DaevanionBoard> Boards { get; }
    public IReadOnlyList<DaevanionSkill> Skills { get; }
    public IReadOnlyList<DaevanionStat> Stats { get; }
    public IReadOnlyList<DaevanionAsset> Assets { get; }

    private DaevanionCatalog(CatalogDocument document)
    {
        DataVersion = document.DataVersion;
        Classes = Array.AsReadOnly(document.Classes.ToArray());
        Boards = Array.AsReadOnly(document.Boards.Select(board => board with
        {
            Nodes = Array.AsReadOnly(board.Nodes.Select(node => node with
            {
                Effects = Array.AsReadOnly(node.Effects.ToArray())
            }).ToArray())
        }).ToArray());
        Skills = Array.AsReadOnly(document.Skills.ToArray());
        Stats = Array.AsReadOnly(document.Stats.ToArray());
        Assets = Array.AsReadOnly(document.Assets.ToArray());
        _boards = Boards.ToFrozenDictionary(board => (board.ClassId, board.FamilyId));
        _skills = Skills.ToFrozenDictionary(skill => skill.Id);
        _stats = Stats.ToFrozenDictionary(stat => stat.Id, StringComparer.Ordinal);
        _assets = Assets.ToFrozenDictionary(asset => asset.Id, StringComparer.Ordinal);
        Validate();
    }

    public DaevanionBoard? GetBoard(string classId, string familyId) => _boards.GetValueOrDefault((classId, familyId));
    public DaevanionSkill? GetSkill(int id) => _skills.GetValueOrDefault(id);
    public DaevanionStat? GetStat(string id) => _stats.GetValueOrDefault(id);
    public DaevanionAsset? GetAsset(string id) => _assets.GetValueOrDefault(id);

    public static DaevanionCatalog Load(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        try
        {
            var document = JsonSerializer.Deserialize<CatalogDocument>(source, options)
                ?? throw new InvalidDataException("Missing Daevanion catalog.");
            if (document.SchemaVersion != 1) throw new InvalidDataException("Unsupported Daevanion catalog.");
            return new DaevanionCatalog(document);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException("Invalid Daevanion catalog.", error);
        }
    }

    private static DaevanionCatalog LoadEmbedded()
    {
        using var stream = typeof(DaevanionCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("Missing embedded Daevanion catalog.");
        return Load(stream);
    }

    private void Validate()
    {
        static bool HasText(DaevanionText? text) => text is not null && !string.IsNullOrWhiteSpace(text.De) && !string.IsNullOrWhiteSpace(text.En);
        void Require(bool valid)
        {
            if (!valid) throw new InvalidDataException("Inconsistent Daevanion catalog.");
        }
        Require(Classes.Count == 8 && Boards.Count == 40 && Skills.Count == 176 && Stats.Count == 24 && Assets.Count == 170);
        var classes = Classes.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        Require(classes.Count == Classes.Count && Classes.All(value => !string.IsNullOrWhiteSpace(value.Id) && HasText(value.Name)));
        Require(Boards.Select(board => board.Id).Distinct().Count() == Boards.Count);
        Require(Stats.All(stat => HasText(stat.Name) && HasText(stat.ShortLabel)));
        Require(Assets.All(asset => asset.Width > 0 && asset.Height > 0 && asset.Sha256.Length == 64 &&
            asset.Path.StartsWith("daevanion/", StringComparison.Ordinal) && asset.Path.EndsWith(".png", StringComparison.Ordinal) &&
            !asset.Path.Contains("..", StringComparison.Ordinal) && !asset.Path.Contains('\\') && !asset.Path.Contains(':')));
        Require(Skills.All(skill => classes.Contains(skill.ClassId) && HasText(skill.Name) && _assets.ContainsKey(skill.ImageAssetId)));
        var nodeIds = new HashSet<int>();
        foreach (var board in Boards)
        {
            Require(classes.Contains(board.ClassId) && FamilyOrder.Contains(board.FamilyId) && HasText(board.Name));
            Require(board.Nodes.Count == (board.FamilyId == "azphel" ? 153 : board.FamilyId == "triniel" ? 117 : 89));
            Require(board.Nodes.Select(node => (node.Row, node.Column)).Distinct().Count() == board.Nodes.Count);
            Require(board.Nodes.Count(node => node.Kind == DaevanionNodeKind.Start) == 1);
            foreach (var node in board.Nodes)
            {
                Require(nodeIds.Add(node.Id) && node.Row is >= 1 and <= 15 && node.Column is >= 1 and <= 15 &&
                    HasText(node.Title) && _assets.ContainsKey(node.FrameAssetId));
                Require(node.Kind == DaevanionNodeKind.Start ? node.Effects.Count == 0 && HasText(node.Description) : node.Effects.Count is >= 1 and <= 2);
                foreach (var effect in node.Effects)
                {
                    Require(HasText(effect.DisplayAmount) && HasText(effect.ShortLabel));
                    if (effect.Kind == DaevanionEffectKind.StatBonus)
                        Require(node.Kind == DaevanionNodeKind.Stat && effect.StatId is not null && _stats.ContainsKey(effect.StatId) && effect.SkillId is null);
                    else
                        Require(node.Kind == DaevanionNodeKind.Skill && node.Effects.Count == 1 && effect.StatId is null && effect.LevelIncrement == 1 &&
                            effect.SkillId is int skillId && _skills.TryGetValue(skillId, out var skill) && skill.ClassId == board.ClassId);
                }
            }
        }
        Require(Boards.All(board => FamilyOrder.All(family => _boards.ContainsKey((board.ClassId, family)))));
    }

    private sealed record CatalogDocument
    {
        public int SchemaVersion { get; init; }
        public string DataVersion { get; init; } = "";
        public IReadOnlyList<DaevanionClass> Classes { get; init; } = [];
        public IReadOnlyList<DaevanionBoard> Boards { get; init; } = [];
        public IReadOnlyList<DaevanionSkill> Skills { get; init; } = [];
        public IReadOnlyList<DaevanionStat> Stats { get; init; } = [];
        public IReadOnlyList<DaevanionAsset> Assets { get; init; } = [];
    }
}
