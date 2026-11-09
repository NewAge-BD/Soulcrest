using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementDefinitionCatalogTests
{
    private static string Document() => JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = 42, definitions = new[]
        {
            new { id = 100, area = "Normal", role = "PlayerFacing", semanticGroup = "example-group",
                resetPolicy = "None", signature = new string('a', 64), stageIds = new[] { 131, 178 }, reason = "normal-ui-member" }
        }
    });

    [Fact]
    public void ExplicitDefinitionsAndStageOrderAreImmutable()
    {
        var catalog = AchievementDefinitionCatalog.FromJson(Document());
        Assert.Equal(42, catalog.MetadataBuild);
        var definition = Assert.Single(catalog.Definitions);
        Assert.Same(definition, catalog.Find(100)); Assert.Null(catalog.Find(101));
        Assert.Equal(AchievementArea.Normal, definition.Area);
        Assert.Equal(AchievementRole.PlayerFacing, definition.Role);
        Assert.Equal(new[] { 131, 178 }, definition.StageIds);
        Assert.Throws<NotSupportedException>(() => ((IList<AchievementDefinitionInfo>)catalog.Definitions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<int>)definition.StageIds).Clear());
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("build")]
    [InlineData("missing-build")]
    [InlineData("missing-definitions")]
    [InlineData("empty-definitions")]
    [InlineData("null-definition")]
    [InlineData("zero-id")]
    [InlineData("duplicate-id")]
    [InlineData("area")]
    [InlineData("role")]
    [InlineData("promoted-module")]
    [InlineData("promoted-reference")]
    [InlineData("reset")]
    [InlineData("signature")]
    [InlineData("signature-case")]
    [InlineData("semantic-group")]
    [InlineData("reason")]
    [InlineData("stages-null")]
    [InlineData("stages-empty")]
    [InlineData("stage-zero")]
    [InlineData("stage-duplicate")]
    [InlineData("stage-shared-family")]
    [InlineData("extra-root")]
    [InlineData("extra-row")]
    public void InvalidMetadataAndScopePromotionFailClosed(string variant)
    {
        var file = JsonNode.Parse(Document())!.AsObject();
        var definitions = file["definitions"]!.AsArray(); var row = definitions[0]!.AsObject();
        switch (variant)
        {
            case "schema": file["schema"] = 2; break;
            case "build": file["metadataBuild"] = 0; break;
            case "missing-build": file.Remove("metadataBuild"); break;
            case "missing-definitions": file.Remove("definitions"); break;
            case "empty-definitions": definitions.Clear(); break;
            case "null-definition": definitions[0] = null; break;
            case "zero-id": row["id"] = 0; break;
            case "duplicate-id": definitions.Add(row.DeepClone()); break;
            case "area": row["area"] = "All"; break;
            case "role": row["role"] = "Available"; break;
            case "promoted-module": row["area"] = "Season"; break;
            case "promoted-reference": row["area"] = "Reference"; row["role"] = "Candidate"; break;
            case "reset": row["resetPolicy"] = "AlwaysAvailable"; break;
            case "signature": row["signature"] = new string('g', 64); break;
            case "signature-case": row["signature"] = new string('A', 64); break;
            case "semantic-group": row["semanticGroup"] = "Native::PrivateKey"; break;
            case "reason": row["reason"] = "a path/value"; break;
            case "stages-null": row["stageIds"] = null; break;
            case "stages-empty": row["stageIds"] = new JsonArray(); break;
            case "stage-zero": row["stageIds"]![0] = 0; break;
            case "stage-duplicate": row["stageIds"]![1] = 131; break;
            case "stage-shared-family":
                var second = row.DeepClone(); second["id"] = 200; definitions.Add(second); break;
            case "extra-root": file["sourcePath"] = "private"; break;
            case "extra-row": row["objectiveValues"] = new JsonArray(); break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }
        Assert.Throws<InvalidDataException>(() => AchievementDefinitionCatalog.FromJson(file.ToJsonString()));
    }

    [Fact]
    public void InputBoundsAndMissingFieldsCannotCreateDefinitions()
    {
        Assert.Throws<InvalidDataException>(() => AchievementDefinitionCatalog.FromJson(null!));
        Assert.Throws<InvalidDataException>(() => AchievementDefinitionCatalog.FromJson(new string(' ', 8 * 1024 * 1024 + 1)));
        Assert.Throws<InvalidDataException>(() => AchievementDefinitionCatalog.FromJson("[]"));
        var file = JsonNode.Parse(Document())!; file["definitions"]![0]!.AsObject().Remove("signature");
        Assert.Throws<InvalidDataException>(() => AchievementDefinitionCatalog.FromJson(file.ToJsonString()));
    }

    [Fact]
    public void EveryNativeDisplayDefinitionAndStageHasExactlyOneDefinitionScope()
    {
        var catalog = AchievementDefinitionCatalog.Default;
        var display = CombatAchievementDisplayCatalog.Default;
        Assert.Same(catalog, AchievementDefinitionCatalog.Default);
        Assert.Equal(display.MetadataBuild, catalog.MetadataBuild);
        Assert.Equal(6428, catalog.Definitions.Count);
        Assert.Equal(display.Achievements.Select(a => a.Id).Order(), catalog.Definitions.Select(a => a.Id).Order());
        var allStages = catalog.Definitions.SelectMany(a => a.StageIds).ToArray();
        Assert.Equal(17316, allStages.Length); Assert.Equal(allStages.Length, allStages.Distinct().Count());
        foreach (var definition in catalog.Definitions)
        {
            var native = Assert.IsType<CombatAchievementDisplayDefinition>(display.Find(definition.Id));
            Assert.Equal(native.Stages.Select(s => s.Id).Concat(native.UnknownStageIds).Order(), definition.StageIds.Order());
            Assert.Equal(64, definition.Signature.Length);
            if (definition.Role == AchievementRole.PlayerFacing)
            {
                Assert.Equal(AchievementArea.Normal, definition.Area); Assert.NotNull(native.CategoryId);
                Assert.True(native.HasNativeName); Assert.Empty(native.UnknownStageIds);
                Assert.Equal(native.Stages.Select(s => s.Id), definition.StageIds);
            }
        }
    }

    [Fact]
    public void OnlyThe676NativeNormalDefinitionsArePlayerFacing()
    {
        var rows = AchievementDefinitionCatalog.Default.Definitions;
        Assert.Equal(676, rows.Count(a => a.Role == AchievementRole.PlayerFacing));
        Assert.Equal(676, rows.Count(a => a.Area == AchievementArea.Normal));
        Assert.Equal(2153, rows.Count(a => a.Role == AchievementRole.Candidate));
        Assert.Equal(3599, rows.Count(a => a.Role == AchievementRole.Reference));
        Assert.All(rows.Where(a => a.Role == AchievementRole.Candidate), a => Assert.NotEqual(AchievementArea.Normal, a.Area));
        Assert.Equal(716, rows.Count(a => a.Area == AchievementArea.Season && a.Role == AchievementRole.Candidate));
        Assert.Equal(784, rows.Count(a => a.Area == AchievementArea.Season && a.Role == AchievementRole.Reference));
        Assert.Equal(2815, rows.Count(a => a.Area == AchievementArea.Reference && a.Role == AchievementRole.Reference));
        Assert.Equal(20, rows.Count(a => a.Area == AchievementArea.Event && a.Role == AchievementRole.Candidate));
        Assert.Equal(468, rows.Count(a => a.Area == AchievementArea.BattlePass && a.Role == AchievementRole.Candidate));
        Assert.Equal(52, rows.Count(a => a.Area == AchievementArea.Attendance && a.Role == AchievementRole.Candidate));
        Assert.Equal(897, rows.Count(a => a.Area == AchievementArea.Onboarding && a.Role == AchievementRole.Candidate));
    }

    [Fact]
    public void BundledDefinitionsContainNoRawOperandsOrPrivateProvenance()
    {
        using var stream = typeof(AchievementDefinitionCatalog).Assembly.GetManifestResourceStream("Soulcrest.AchievementDefinitionScopes.json");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!); var json = reader.ReadToEnd();
        foreach (var token in new[] { "EAchievement", "EResetCycleType", "objectiveValues", "sourcePath", "rawTarget", "lookupId", "recordByteRange" })
            Assert.DoesNotContain(token, json, StringComparison.Ordinal);
    }
}
