using System.Text.Json;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Explicit offline-only probe of name-free skill-list bodies; never starts capture or the app.</summary>
public sealed class OwnSkillOfflineExploration
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void ExistingPrivateSkillListsUseTheProductionDecoderAndExplicitComponentArithmetic()
    {
        var path = Environment.GetEnvironmentVariable("SOULCREST_SKILL_LIST_FIXTURE");
        Assert.False(string.IsNullOrWhiteSpace(path), "Set SOULCREST_SKILL_LIST_FIXTURE to the private offline JSON.");
        var info = new FileInfo(path!);
        Assert.InRange(info.Length, 1, 1 << 20);
        using var json = JsonDocument.Parse(File.ReadAllBytes(path!));
        var lists = json.RootElement.GetProperty("messages").EnumerateArray().Select(item =>
            Assert.IsType<OwnSkillList>(OwnSkillMessage.TryParseList(Convert.FromBase64String(item.GetString()!)))).ToArray();
        Assert.Equal(new[] { 64, 64, 52, 64 }, lists.Select(item => item.Skills.Count));
        Assert.Equal(new[] { 27, 27, 13, 27 }, lists.Select(item => item.Skills.Values.Count(skill => skill.AdditionalLevels?.Daevanion > 0)));
        foreach (var skill in lists.SelectMany(item => item.Skills.Values))
        {
            var b = Assert.IsType<OwnSkillLevelBonuses>(skill.AdditionalLevels);
            Assert.Equal(skill.TotalLevel, skill.OriginalLevel + b.Daevanion + b.Abnormal + b.Equipment + b.Arcana + b.Fixed);
        }
    }
}
