using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Components;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Offline projection of an actor-free debug observation; no app, capture or profile store.</summary>
public sealed class AchievementStageReplayDiagnostic
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void EvaluateOwnObservation()
    {
        var input = TaskPath("SOULCREST_ACHIEVEMENT_STAGE_INPUT");
        var output = TaskPath("SOULCREST_ACHIEVEMENT_STAGE_OUTPUT");
        Assert.NotEqual(input, output);
        Assert.InRange(new FileInfo(input).Length, 1, 16 * 1024 * 1024);
        using var file = JsonDocument.Parse(File.ReadAllText(input));
        var observed = file.RootElement.GetProperty("observation");
        Assert.True(observed.GetProperty("isFresh").GetBoolean());
        Assert.Contains(observed.GetProperty("source").GetString(), new[] { "own-achievement-list", "own-achievement-update", "own-achievement-reward" });
        var rows = observed.GetProperty("rows").EnumerateArray().ToImmutableDictionary(row => row.GetProperty("id").GetInt32(),
            row => new CharacterAchievement(row.GetProperty("count").GetInt32(),
                long.Parse(row.GetProperty("completeTime").GetString()!, CultureInfo.InvariantCulture), row.GetProperty("rewardedStep").GetByte()));
        var raw = new CharacterAchievementState(rows, ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        var display = CombatAchievementDisplayCatalog.Default;
        var definitions = AchievementDefinitionCatalog.Default;
        var progress = new AchievementRuleEvaluator().Evaluate(raw, display.MetadataBuild, 1, 1);
        var binding = new AchievementProfileBinding("11111111111111111111111111111111", "22222222222222222222222222222222");
        var own = new AchievementOwnSnapshot(binding, new()
        {
            ProfileId = binding.ProfileId, Binding = binding, Source = AchievementViewSource.Live,
            MetadataBuild = display.MetadataBuild, Generation = 1, Sequence = 1, Progress = progress
        }, raw, 0);
        var resolver = new AchievementTrackerStageResolver();
        var result = new List<object>();
        foreach (var (id, value) in rows.OrderBy(row => row.Key))
        {
            if (definitions.Find(id) is not { Area: AchievementArea.Normal, Role: AchievementRole.PlayerFacing }) continue;
            var family = progress.Find(id);
            if (family?.CurrentStageId is not { } stageId) continue;
            var stage = family.Find(stageId)!;
            var hud = resolver.Resolve(own, id);
            Assert.Equal(stageId, hud.Row.StageId);
            Assert.Equal(value.Count, hud.Row.RawValue);
            if (stage.Unit != CombatAchievementProgressUnit.Unknown && family.Status != Soulcrest.Core.Combat.CombatKnowledgeStatus.Conflict)
            {
                Assert.Equal(stage.Current, hud.Row.Current);
                Assert.Equal(stage.Target, hud.Row.Target);
            }
            result.Add(new { id, value = value.Count, rawClaimPrefix = value.RewardedStep, stageId,
                name = hud.Row.NameEn, current = stage.Current, target = stage.Target,
                claimed = stage.Claimed, completed = stage.Completed, allClaimed = hud.AllClaimed });
        }
        Assert.NotEmpty(result);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            schema = 1, sourceObservedAt = observed.GetProperty("observedAt").GetString(),
            metadataBuild = display.MetadataBuild, rowCount = rows.Count, families = result
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string TaskPath(string variable)
    {
        var path = Path.GetFullPath(TestPrerequisites.RequiredValue(variable));
        var root = Path.GetFullPath(Path.Combine(TestPrerequisites.RepositoryRoot, "output")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase);
        return path;
    }
}
