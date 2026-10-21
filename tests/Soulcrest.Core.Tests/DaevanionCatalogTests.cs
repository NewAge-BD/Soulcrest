using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class DaevanionCatalogTests
{
    [Fact]
    public void CompleteSixFamilyCatalogKeepsAllClassesNodesAndEffects()
    {
        var catalog = DaevanionCatalog.Default;
        Assert.Equal(8, catalog.Classes.Count);
        Assert.Equal(new[] { "nezekan", "zikel", "vaizel", "triniel", "ariel", "azphel" }, DaevanionCatalog.Families);
        Assert.Equal(48, catalog.Boards.Count);
        Assert.Equal(176, catalog.Skills.Count);
        Assert.Equal(34, catalog.Stats.Count);
        var nodes = catalog.Boards.SelectMany(board => board.Nodes).ToArray();
        Assert.Equal(5520, nodes.Length);
        Assert.Equal(4768, nodes.Count(node => node.Kind == DaevanionNodeKind.Stat));
        Assert.Equal(5152, nodes.SelectMany(node => node.Effects).Count(effect => effect.Kind == DaevanionEffectKind.StatBonus));
        Assert.Equal(704, nodes.Count(node => node.Kind == DaevanionNodeKind.Skill));
        Assert.Equal(48, nodes.Count(node => node.Kind == DaevanionNodeKind.Start));
        Assert.Equal(384, nodes.Count(node => node.Effects.Count == 2));
        foreach (var characterClass in catalog.Classes)
            foreach (var family in DaevanionCatalog.Families)
                Assert.NotNull(catalog.GetBoard(characterClass.Id, family));
    }

    [Fact]
    public void ClericScreenshotUsesOriginalCoordinatesAndExactLocalizedValues()
    {
        var catalog = DaevanionCatalog.Default;
        var board = catalog.GetBoard("Cleric", "nezekan")!;
        Assert.Equal(71, board.Id);
        var speed = board.Nodes.Single(node => node.Id == 710033);
        Assert.Equal((3, 3), (speed.Row, speed.Column));
        var bonus = Assert.Single(speed.Effects);
        Assert.Equal("CombatSpeed", bonus.StatId);
        Assert.Equal("1.5", bonus.Amount);
        Assert.Equal("+1,5%", bonus.DisplayAmount.De);
        Assert.Equal("+1.5%", bonus.DisplayAmount.En);
        var grace = board.Nodes.Single(node => node.Row == 3 && node.Column == 11);
        var skillBonus = Assert.Single(grace.Effects);
        Assert.Equal(DaevanionEffectKind.SkillLevelBonus, skillBonus.Kind);
        Assert.Equal(17730000, skillBonus.SkillId);
        Assert.Equal("Empyrean Lord's Grace", catalog.GetSkill(skillBonus.SkillId!.Value)!.Name.En);
        Assert.Equal("Skill Level Up - Empyrean Lords' Grace", grace.Title.En);
        var start = board.Nodes.Single(node => node.Kind == DaevanionNodeKind.Start);
        Assert.Equal((8, 8), (start.Row, start.Column));
        Assert.Empty(start.Effects);
    }

    [Fact]
    public void AzphelRetainsBothOriginalBonusesAndDoesNotConfuseColoredStatsWithSkills()
    {
        var catalog = DaevanionCatalog.Default;
        var board = catalog.GetBoard("Cleric", "azphel")!;
        Assert.Equal(76, board.Id);
        Assert.Equal("Azphel", board.Name.En);
        Assert.Equal(153, board.Nodes.Count);
        Assert.Equal(24, board.Nodes.Count(node => node.Effects.Count == 2));
        Assert.DoesNotContain(board.Nodes, node => node.Kind == DaevanionNodeKind.Skill);
        var critical = board.Nodes.Single(node => node.Id == 760006);
        Assert.Equal(DaevanionNodeQuality.Rare, critical.Quality);
        Assert.Equal("Krit.-Treffer (PvP), Krit.-Widerstand (PvP)", critical.Title.De);
        Assert.Collection(critical.Effects,
            effect => { Assert.Equal("PvPCritical", effect.StatId); Assert.Equal("+5", effect.DisplayAmount.En); },
            effect => { Assert.Equal("PvPCriticalResist", effect.StatId); Assert.Equal("+5", effect.DisplayAmount.En); });
        Assert.All(board.Nodes.Where(node => node.Quality == DaevanionNodeQuality.Legend),
            node => Assert.Equal(DaevanionNodeKind.Stat, node.Kind));
        Assert.All(board.Nodes.SelectMany(node => node.Effects).Where(effect => effect.StatId is "AbnormalAccuracy" or "AbnormalResistance"),
            effect => Assert.Equal("+1%", effect.DisplayAmount.En));
        Assert.All(board.Nodes.SelectMany(node => node.Effects).Where(effect => effect.StatId is "PvPAmplifyDamage" or "PvPDecreaseDamage"),
            effect => Assert.Equal("+1.5%", effect.DisplayAmount.En));
    }

    [Fact]
    public void ArielRetainsOriginalPveAndBossPairsAndStatFramesAlongsideAzphel()
    {
        var catalog = DaevanionCatalog.Default;
        var board = catalog.GetBoard("Cleric", "ariel")!;
        Assert.Equal(75, board.Id);
        Assert.Equal("Ariel", board.Name.De);
        Assert.Equal("Ariel", board.Name.En);
        Assert.Equal(153, board.Nodes.Count);
        Assert.Equal(24, board.Nodes.Count(node => node.Effects.Count == 2));
        Assert.Equal(24, board.Nodes.Count(node => node.Quality == DaevanionNodeQuality.Rare));
        Assert.Equal(16, board.Nodes.Count(node => node.Quality == DaevanionNodeQuality.Legend));
        Assert.Equal(8, board.Nodes.Count(node => node.Quality == DaevanionNodeQuality.Unique));
        Assert.Equal(76, catalog.GetBoard("Cleric", "azphel")!.Id);
        Assert.DoesNotContain(board.Nodes, node => node.Kind == DaevanionNodeKind.Skill);
        var start = Assert.Single(board.Nodes, node => node.Kind == DaevanionNodeKind.Start);
        Assert.Equal((750113, 8, 8), (start.Id, start.Row, start.Column));
        Assert.Empty(start.Effects);

        var attack = board.Nodes.Single(node => node.Id == 750003);
        Assert.Equal((1, 3), (attack.Row, attack.Column));
        Assert.Equal("Angriffskraft (PvE), Verteidigung (PvE)", attack.Title.De);
        Assert.Equal(DaevanionNodeQuality.Rare, attack.Quality);
        Assert.Equal("ut_fwindow_daevanion_node_rare_sprite", attack.FrameAssetId);
        Assert.Collection(attack.Effects,
            effect => { Assert.Equal("PvEAddDamage", effect.StatId); Assert.Equal("+3", effect.DisplayAmount.En); },
            effect => { Assert.Equal("PvEDamageDefense", effect.StatId); Assert.Equal("+30", effect.DisplayAmount.En); });
        var boss = board.Nodes.Single(node => node.Id == 750024);
        Assert.Equal((2, 9), (boss.Row, boss.Column));
        Assert.Equal("Boss Attack, Boss Defense", boss.Title.En);
        Assert.Collection(boss.Effects,
            effect => { Assert.Equal("BossNpcAddDamage", effect.StatId); Assert.Equal("+3", effect.DisplayAmount.En); },
            effect => { Assert.Equal("BossNpcDefense", effect.StatId); Assert.Equal("+30", effect.DisplayAmount.En); });
        Assert.Equal("ut_fwindow_daevanion_node_unique_sprite", board.Nodes.Single(node => node.Id == 750001).FrameAssetId);
        Assert.Equal("ut_fwindow_daevanion_node_legend_sprite", board.Nodes.Single(node => node.Id == 750032).FrameAssetId);
        Assert.All(board.Nodes.Where(node => node.Quality is DaevanionNodeQuality.Rare or DaevanionNodeQuality.Legend),
            node => Assert.Equal(DaevanionNodeKind.Stat, node.Kind));
    }

    [Theory]
    [InlineData("PvEAddDamage", "3", DaevanionStatUnit.Points, "+3", "+3", 24)]
    [InlineData("PvEDamageDefense", "30", DaevanionStatUnit.Points, "+30", "+30", 24)]
    [InlineData("PvEAccuracy", "5", DaevanionStatUnit.Points, "+5", "+5", 24)]
    [InlineData("PvEEvasion", "5", DaevanionStatUnit.Points, "+5", "+5", 24)]
    [InlineData("PvEAmplifyDamage", "1.5", DaevanionStatUnit.Percent, "+1,5%", "+1.5%", 4)]
    [InlineData("PvEDecreaseDamage", "1.5", DaevanionStatUnit.Percent, "+1,5%", "+1.5%", 4)]
    [InlineData("BossNpcAddDamage", "3", DaevanionStatUnit.Points, "+3", "+3", 24)]
    [InlineData("BossNpcDefense", "30", DaevanionStatUnit.Points, "+30", "+30", 24)]
    [InlineData("BossNpcAmplifyDamage", "1", DaevanionStatUnit.Percent, "+1%", "+1%", 4)]
    [InlineData("BossNpcDecreaseDamage", "1", DaevanionStatUnit.Percent, "+1%", "+1%", 4)]
    public void ArielUsesQualifiedPveAndBossValuesForEveryClass(string statId, string amount,
        DaevanionStatUnit unit, string german, string english, int perBoardCount)
    {
        var catalog = DaevanionCatalog.Default;
        Assert.Equal(unit, catalog.GetStat(statId)!.Unit);
        foreach (var characterClass in catalog.Classes)
        {
            var board = catalog.GetBoard(characterClass.Id, "ariel")!;
            var effects = board.Nodes.SelectMany(node => node.Effects).Where(effect => effect.StatId == statId).ToArray();
            Assert.Equal(perBoardCount, effects.Length);
            Assert.All(effects, effect =>
            {
                Assert.Equal(DaevanionEffectKind.StatBonus, effect.Kind);
                Assert.Null(effect.SkillId);
                Assert.Equal(amount, effect.Amount);
                Assert.Equal(german, effect.DisplayAmount.De);
                Assert.Equal(english, effect.DisplayAmount.En);
            });
        }
    }

    [Fact]
    public void RecognitionMasksAreSharedByClassAndDistinctBetweenFamilies()
    {
        var catalog = DaevanionCatalog.Default;
        var masks = new List<string>();
        foreach (var family in DaevanionCatalog.Families)
        {
            var first = catalog.GetBoard(catalog.Classes[0].Id, family)!;
            var mask = Mask(first);
            Assert.All(catalog.Boards.Where(board => board.FamilyId == family), board => Assert.Equal(mask, Mask(board)));
            masks.Add(string.Join(";", first.Nodes.OrderBy(node => node.Row).ThenBy(node => node.Column)
                .Select(node => $"{node.Row},{node.Column}")));
        }
        Assert.Equal(6, masks.Distinct().Count());
        foreach (var family in new[] { "ariel", "azphel" })
        {
            Assert.Equal(1, catalog.Boards.Where(board => board.FamilyId == family).Min(board => board.Nodes.Min(node => node.Row)));
            Assert.Equal(15, catalog.Boards.Where(board => board.FamilyId == family).Max(board => board.Nodes.Max(node => node.Column)));
        }
    }

    [Fact]
    public void EveryOriginalPngIsLocalHashVerifiedAndReferenced()
    {
        var catalog = DaevanionCatalog.Default;
        Assert.Equal(170, catalog.Assets.Count);
        Assert.Equal(160, catalog.Skills.Select(skill => skill.ImageAssetId).Distinct().Count());
        var root = RepositoryRoot();
        foreach (var asset in catalog.Assets)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, "src", "Soulcrest.App", "wwwroot", asset.Path));
            Assert.True(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.Equal(asset.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        Assert.NotNull(catalog.GetAsset("ut_fwindow_daevanion_node_rare_disabled_sprite"));
        Assert.NotNull(catalog.GetAsset("ut_fwindow_daevanion_node_startbg_sprite"));
    }

    [Fact]
    public void PublicCollectionsCannotMutateTheSharedCatalog()
    {
        var catalog = DaevanionCatalog.Default;
        var boards = Assert.IsAssignableFrom<IList<DaevanionBoard>>(catalog.Boards);
        Assert.Throws<NotSupportedException>(() => boards.Clear());
        var nodes = Assert.IsAssignableFrom<IList<DaevanionNode>>(catalog.Boards[0].Nodes);
        Assert.Throws<NotSupportedException>(() => nodes.Clear());
        var effects = Assert.IsAssignableFrom<IList<DaevanionEffect>>(catalog.Boards[0].Nodes[0].Effects);
        Assert.Throws<NotSupportedException>(() => effects.Clear());
        Assert.Null(catalog.GetBoard("Fighter", "nezekan"));
        Assert.Null(catalog.GetBoard("Cleric", "unknown"));
        Assert.Null(catalog.GetSkill(-1));
        Assert.Null(catalog.GetStat("unknown"));
        Assert.Null(catalog.GetAsset("unknown"));
        Assert.Equal("Kleriker", catalog.Classes.Single(value => value.Id == "Cleric").Name.For("de-DE"));
        Assert.Equal("Cleric", catalog.Classes.Single(value => value.Id == "Cleric").Name.For(null));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("coordinate")]
    [InlineData("skill")]
    [InlineData("assetPath")]
    [InlineData("arielIncomplete")]
    public void InvalidCatalogFailsClosed(string corruption)
    {
        using var embedded = typeof(DaevanionCatalog).Assembly.GetManifestResourceStream("Soulcrest.DaevanionCatalog.json")!;
        var document = JsonNode.Parse(embedded)!;
        if (corruption == "version") document["schemaVersion"] = 100;
        if (corruption == "coordinate") document["boards"]![0]!["nodes"]![0]!["row"] = 0;
        if (corruption == "skill") document["skills"]![0]!["imageAssetId"] = "unknown";
        if (corruption == "assetPath") document["assets"]![0]!["path"] = "daevanion/../../secret.png";
        if (corruption == "arielIncomplete")
        {
            var ariel = document["boards"]!.AsArray().First(board => board!["familyId"]!.GetValue<string>() == "ariel")!;
            ariel["nodes"]!.AsArray().RemoveAt(0);
        }
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => DaevanionCatalog.Load(source));
    }

    private static string Mask(DaevanionBoard board) => string.Join(";", board.Nodes
        .OrderBy(node => node.Row).ThenBy(node => node.Column)
        .Select(node => $"{node.Row},{node.Column},{node.Kind},{node.Quality}"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Soulcrest.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
