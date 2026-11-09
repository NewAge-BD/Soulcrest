using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatStatObservationDataTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledOrNonCurrentOwnStateEmitsNoMetadataMarkup()
    {
        Assert.Empty(await Render(Snapshot(), false));
        Assert.Empty(await Render(Snapshot() with { IsFresh = false }, true));
        Assert.Empty(await Render(CharacterCombatSnapshot.Empty, true));
        Assert.Null(CombatStatObservationPresentation.Create(null));
    }

    [Fact]
    public void ExplicitDtoIncludesOnlyAllowedStatFieldsAndPreservesNullAndZero()
    {
        var snapshot = Snapshot();
        var equipment = new CharacterEquipmentDetail(0, 0, 0, 0, 0, 0, false, 0, [], 0, [], [], [], [], [], [], [], [], 0);
        snapshot = snapshot with
        {
            ProfileId = "ACTOR_PRIVATE_NAME",
            Equipment = new(new([new CharacterEquipmentItem(1876543219, equipment) { InstanceKey = 918273645546372819 }]),
                NetworkCombatSourceStatus.Complete, true, At, 1, "PRIVATE_ENDPOINT"),
            Graph = new() { ClassId = "PRIVATE_NATIVE_ID", Sources = [new() { Id = "PRIVATE_TRANSPORT" }] },
            Breakdown = new() { Sources = [new() { SourceId = "PRIVATE_SOURCE_GRAPH" }] }
        };
        var dto = Assert.IsType<CombatStatObservationDto>(CombatStatObservationPresentation.Create(snapshot));
        var json = CombatStatObservationPresentation.Serialize(dto);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(26262663, root.GetProperty("displayReferenceBuild").GetInt32());
        Assert.Equal(CharacterDisplayStatProjection.Version, root.GetProperty("displayVersion").GetString());
        Assert.Equal(0, root.GetProperty("currentCombatScore").GetInt64());
        Assert.Equal(0, root.GetProperty("nativeStats").GetProperty("values").GetProperty("193").GetInt32());
        var projected = root.GetProperty("projectedRows");
        Assert.Equal(0, projected[0].GetProperty("value").GetDecimal());
        Assert.Equal(0, projected[0].GetProperty("rawDependencies")[0].GetProperty("rawValue").GetInt32());
        Assert.Equal(JsonValueKind.Null, projected[1].GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, projected[1].GetProperty("rawDependencies")[0].GetProperty("rawValue").ValueKind);
        Assert.DoesNotContain("ACTOR_PRIVATE_NAME", json); Assert.DoesNotContain("PRIVATE_", json);
        Assert.DoesNotContain("1876543219", json); Assert.DoesNotContain("918273645546372819", json);
        Assert.DoesNotContain("987654321", json); // Stale dependency value supplied by the row is never copied.
        Assert.DoesNotContain("profileId", json); Assert.DoesNotContain("equipment", json);
        Assert.DoesNotContain("graph", json); Assert.DoesNotContain("skills", json);
    }

    [Fact]
    public void StaleStatDomainHasNoCurrentNumbersAndResourceMaximaKeepIndependentFactTimes()
    {
        var snapshot = Snapshot();
        var hpAt = At.AddSeconds(2); var mpAt = At.AddSeconds(-3);
        snapshot = snapshot with
        {
            NativeStats = snapshot.NativeStats with { IsFresh = false, Status = NetworkCombatSourceStatus.Stale },
            HpMaximum = new(new(0), NetworkCombatSourceStatus.Complete, true, hpAt, 3, "own-resource-maxima-update"),
            MpMaximum = new(new(999), NetworkCombatSourceStatus.Stale, false, mpAt, 2, "own-entry-resource-maxima"),
            DisplayStats = snapshot.DisplayStats with
            {
                Rows = [new() { Id = "HPMax", Value = 0, Status = CombatKnowledgeStatus.Complete,
                    Qualification = CharacterDisplayStatQualification.NativeDirect, Source = "own-resource-maxima-update",
                    ObservedAt = hpAt, Inputs = [new(193, 987654321, 1)] }]
            }
        };
        var dto = Assert.IsType<CombatStatObservationDto>(CombatStatObservationPresentation.Create(snapshot));
        Assert.Null(dto.NativeStats.Values); Assert.False(dto.NativeStats.IsFresh);
        Assert.Equal(0, dto.HpMaximum.Value); Assert.Equal(hpAt, dto.HpMaximum.ObservedAt);
        Assert.Null(dto.MpMaximum.Value); Assert.Equal(mpAt, dto.MpMaximum.ObservedAt);
        var hp = Assert.Single(dto.ProjectedRows);
        Assert.Equal(0, hp.Value); Assert.Null(Assert.Single(hp.RawDependencies).RawValue);
    }

    [Fact]
    public void ProjectionFromAnotherGenerationCannotSupplyCurrentProjectedNumbersOrDependencies()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { DisplayStats = snapshot.DisplayStats with { Generation = 6 } };
        var dto = Assert.IsType<CombatStatObservationDto>(CombatStatObservationPresentation.Create(snapshot));
        Assert.All(dto.ProjectedRows, row =>
        {
            Assert.Null(row.Value); Assert.Equal(CombatKnowledgeStatus.Stale, row.Status);
            Assert.All(row.RawDependencies, dependency => Assert.Null(dependency.RawValue));
        });
        Assert.NotNull(dto.NativeStats.Values);
    }

    [Fact]
    public void UnknownSourceLabelsAreRedactedAndCannotQualifyCurrentDomainNumbers()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            NativeStats = snapshot.NativeStats with { Source = "PRIVATE_ENDPOINT" },
            HpMaximum = new(new(123), NetworkCombatSourceStatus.Complete, true, At, 1, "PRIVATE_NATIVE_ID")
        };
        var dto = Assert.IsType<CombatStatObservationDto>(CombatStatObservationPresentation.Create(snapshot));
        Assert.Null(dto.NativeStats.Values); Assert.Null(dto.HpMaximum.Value);
        Assert.Equal("unknown", dto.NativeStats.Source); Assert.Equal("unknown", dto.HpMaximum.Source);
        Assert.DoesNotContain("PRIVATE_", CombatStatObservationPresentation.Serialize(dto));
    }

    [Fact]
    public void UnknownEpochOrMissingFactTimeDoesNotQualifyNumbersAndDisplayVersionIsAReference()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Generation = 0,
            HpMaximum = new(new(123), NetworkCombatSourceStatus.Complete, true, At, 1, "own-entry-resource-maxima"),
            DisplayStats = snapshot.DisplayStats with { Version = "reference-version-for-test" }
        };
        var unknownEpoch = Assert.IsType<CombatStatObservationDto>(CombatStatObservationPresentation.Create(snapshot));
        Assert.Null(unknownEpoch.NativeStats.Values); Assert.Null(unknownEpoch.HpMaximum.Value);
        Assert.Equal("reference-version-for-test", unknownEpoch.DisplayVersion);
        snapshot = snapshot with { Generation = 7, NativeStats = snapshot.NativeStats with { ObservedAt = null },
            HpMaximum = snapshot.HpMaximum with { ObservedAt = null } };
        var noFactTime = Assert.IsType<CombatStatObservationDto>(CombatStatObservationPresentation.Create(snapshot));
        Assert.Null(noFactTime.NativeStats.Values); Assert.Null(noFactTime.HpMaximum.Value);
        Assert.All(noFactTime.ProjectedRows, row => Assert.Null(row.Value));
    }

    [Fact]
    public async Task JsonScriptUsesDefaultEscapingAndRemainsParseableWithoutHtmlEntityDecoding()
    {
        const string payload = "rule</script><script>alert(\"x\")</script>&";
        var snapshot = Snapshot();
        snapshot = snapshot with { DisplayStats = snapshot.DisplayStats with
            { Rows = [snapshot.DisplayStats.Rows[0] with { RuleId = payload }] } };
        var html = await Render(snapshot, true);
        Assert.Single(Regex.Matches(html, "</script>").Cast<Match>());
        var match = Regex.Match(html, "<script[^>]*data-stat-observation[^>]*>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success);
        var json = match.Groups[1].Value;
        Assert.DoesNotContain("</script>", json); Assert.DoesNotContain("&quot;", json);
        Assert.Contains("\\u003C", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(payload, document.RootElement.GetProperty("projectedRows")[0].GetProperty("ruleId").GetString());
    }

    private static CharacterCombatSnapshot Snapshot() => new()
    {
        IsFresh = true, Generation = 7, Sequence = 12, ClassId = "Cleric", CharacterLevel = 45, CurrentCombatScore = 0,
        NativeStats = new(ImmutableDictionary<ushort, int>.Empty.Add(193, 0), NetworkCombatSourceStatus.Partial,
            true, At, 12, "own-stat-update"),
        DisplayStats = new()
        {
            Generation = 7, ClientBuild = 26262663,
            Rows =
            [
                new() { Id = "SyntheticZero", Value = 0, Status = CombatKnowledgeStatus.Complete,
                    Qualification = CharacterDisplayStatQualification.ObservedMatch, RuleId = "synthetic-zero",
                    ObservedAt = At, Source = "own-stat-update", Inputs = [new(193, 987654321, 1)] },
                new() { Id = "SyntheticMissing", Status = CombatKnowledgeStatus.Unknown,
                    Qualification = CharacterDisplayStatQualification.Unknown, RuleId = "synthetic-missing",
                    MissingInputIds = [999], Inputs = [new(999, 987654321, 1)] }
            ]
        }
    };

    private static async Task<string> Render(CharacterCombatSnapshot snapshot, bool enabled)
    {
        var services = new ServiceCollection(); services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CombatStatObservationData>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CombatStatObservationData.Snapshot)] = snapshot, [nameof(CombatStatObservationData.Enabled)] = enabled })));
        return await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
    }
}
