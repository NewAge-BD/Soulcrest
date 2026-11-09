using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatAchievementRewardScopeTests
{
    private const string Document = """{"schema":1,"metadataBuild":26425393,"families":[{"id":100,"maxOrdinal":2}]}""";

    [Fact]
    public void ExplicitImmutableMembershipAllowsOnlyPositiveBoundedIndividualSteps()
    {
        var scope = CombatAchievementRewardScopes.FromJson(Document);
        Assert.Equal(26425393, scope.MetadataBuild);
        Assert.IsAssignableFrom<FrozenDictionary<int, byte>>(scope.Families);
        Assert.Equal((byte)2, Assert.Single(scope.Families).Value);
        Assert.True(scope.Allows(100, 1)); Assert.True(scope.Allows(100, 2));
        Assert.False(scope.Allows(100, 0)); Assert.False(scope.Allows(100, 3));
        Assert.False(scope.Allows(100, byte.MaxValue));
        Assert.False(scope.Allows(101, 1)); Assert.False(scope.Allows(0, 1));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("build")]
    [InlineData("missing-build")]
    [InlineData("missing-families")]
    [InlineData("null-families")]
    [InlineData("empty-families")]
    [InlineData("null-family")]
    [InlineData("duplicate-family")]
    [InlineData("zero-id")]
    [InlineData("negative-id")]
    [InlineData("missing-id")]
    [InlineData("missing-ordinal")]
    [InlineData("zero-ordinal")]
    [InlineData("negative-ordinal")]
    [InlineData("overflow-ordinal")]
    [InlineData("string-ordinal")]
    [InlineData("extra-root")]
    [InlineData("extra-family")]
    public void UnsupportedVersionsMissingFieldsAndInvalidMembershipFailClosed(string variant)
    {
        var json = JsonNode.Parse(Document)!.AsObject();
        var families = json["families"]!.AsArray(); var family = families[0]!.AsObject();
        switch (variant)
        {
            case "schema": json["schema"] = 2; break;
            case "build": json["metadataBuild"] = 26425394; break;
            case "missing-build": json.Remove("metadataBuild"); break;
            case "missing-families": json.Remove("families"); break;
            case "null-families": json["families"] = null; break;
            case "empty-families": families.Clear(); break;
            case "null-family": families[0] = null; break;
            case "duplicate-family": families.Add(family.DeepClone()); break;
            case "zero-id": family["id"] = 0; break;
            case "negative-id": family["id"] = -1; break;
            case "missing-id": family.Remove("id"); break;
            case "missing-ordinal": family.Remove("maxOrdinal"); break;
            case "zero-ordinal": family["maxOrdinal"] = 0; break;
            case "negative-ordinal": family["maxOrdinal"] = -1; break;
            case "overflow-ordinal": family["maxOrdinal"] = 256; break;
            case "string-ordinal": family["maxOrdinal"] = "2"; break;
            case "extra-root": json["owned"] = true; break;
            case "extra-family": family["completed"] = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }
        Assert.Throws<InvalidDataException>(() => CombatAchievementRewardScopes.FromJson(json.ToJsonString()));
    }

    [Fact]
    public void BoundedInputsAndTheNativeByteMaximumAreEnforced()
    {
        Assert.Throws<InvalidDataException>(() => CombatAchievementRewardScopes.FromJson(" "));
        Assert.Throws<InvalidDataException>(() => CombatAchievementRewardScopes.FromJson("[1]"));
        Assert.Throws<InvalidDataException>(() => CombatAchievementRewardScopes.FromJson(new string(' ', 512 * 1024 + 1)));
        var json = JsonNode.Parse(Document)!;
        json["families"]![0]!["maxOrdinal"] = 255;
        Assert.True(CombatAchievementRewardScopes.FromJson(json.ToJsonString()).Allows(100, 255));
        json["families"] = new JsonArray(Enumerable.Range(1, 10001)
            .Select(id => (JsonNode)new JsonObject { ["id"] = id, ["maxOrdinal"] = 1 }).ToArray());
        Assert.Throws<InvalidDataException>(() => CombatAchievementRewardScopes.FromJson(json.ToJsonString()));
    }

    [Fact]
    public void EmbeddedTransportScopesExactlyMatchTheQualifiedProjectionFamilyChains()
    {
        var scopes = CombatAchievementRewardScopes.Default;
        var contracts = CombatAchievementProgressContractCatalog.Default;
        Assert.Same(scopes, CombatAchievementRewardScopes.Default);
        Assert.Equal(contracts.MetadataBuild, scopes.MetadataBuild);
        var families = contracts.Contracts.SelectMany(type => type.Families).ToDictionary(family => family.Id);
        Assert.Equal(676, families.Count);
        Assert.Equal(families.Keys.Order(), scopes.Families.Keys.Order());
        foreach (var type in contracts.Contracts)
        {
            Assert.Equal(CombatAchievementClaimRule.ContiguousRewardOrdinal, type.ClaimRule);
            foreach (var family in type.Families)
            {
                Assert.Equal(family.Stages.Count, scopes.Families[family.Id]);
                Assert.Equal(Enumerable.Range(1, family.Stages.Count), family.Stages.Select(stage => stage.Ordinal));
                var native = Assert.IsType<CombatAchievementDisplayDefinition>(CombatAchievementDisplayCatalog.Default.Find(family.Id));
                Assert.Empty(native.UnknownStageIds);
                Assert.Equal(native.Stages.Select(stage => stage.Id).Order(), family.Stages.Select(stage => stage.Id).Order());
            }
        }
        Assert.True(scopes.Allows(441100900, 2)); Assert.True(scopes.Allows(401100100, 3));
        Assert.NotNull(CombatAchievementDisplayCatalog.Default.Find(210000000));
        Assert.False(scopes.Allows(210000000, 1)); // A known seasonal family is outside the explicit contract.
    }
}
