using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayTriggerPolicyTests
{
    [Fact]
    public void MissingAndEmptyRulesAreNeutralEvenWithoutAnyContextObservation()
    {
        var unknown = new OverlayTriggerContextSnapshot();
        Assert.True(OverlayTriggerPolicy.Matches(null, unknown));
        Assert.True(OverlayTriggerPolicy.Matches(new(), unknown));
        Assert.Null(OverlayTriggerRules.Normalize(null));
    }

    [Theory]
    [InlineData(OverlayTriggerRequirement.Any, null, true)]
    [InlineData(OverlayTriggerRequirement.Any, false, true)]
    [InlineData(OverlayTriggerRequirement.Any, true, true)]
    [InlineData(OverlayTriggerRequirement.Required, null, false)]
    [InlineData(OverlayTriggerRequirement.Required, false, false)]
    [InlineData(OverlayTriggerRequirement.Required, true, true)]
    [InlineData(OverlayTriggerRequirement.Excluded, null, false)]
    [InlineData(OverlayTriggerRequirement.Excluded, false, true)]
    [InlineData(OverlayTriggerRequirement.Excluded, true, false)]
    public void EveryBooleanConditionDistinguishesUnknownFromObservedAbsence(OverlayTriggerRequirement rule,
        bool? observed, bool expected)
    {
        Assert.Equal(expected, OverlayTriggerPolicy.Matches(new() { Foreground = rule }, new(Foreground: observed)));
        Assert.Equal(expected, OverlayTriggerPolicy.Matches(new() { SessionActive = rule }, new(SessionActive: observed)));
        Assert.Equal(expected, OverlayTriggerPolicy.Matches(new() { WorldMap = rule }, new(WorldMap: observed)));
        Assert.Equal(expected, OverlayTriggerPolicy.Matches(new() { EscMenu = rule }, new(EscMenu: observed)));
        Assert.Equal(expected, OverlayTriggerPolicy.Matches(new() { MinimapMissing = rule }, new(MinimapMissing: observed)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(OverlayTriggerArea.OpenWorld, true)]
    [InlineData(OverlayTriggerArea.Abyss, true)]
    [InlineData(OverlayTriggerArea.Instances, false)]
    public void SelectedAreasAreAUnionAndNeverGuessAnUnknownArea(OverlayTriggerArea? area, bool expected)
    {
        var rules = new OverlayTriggerRules { Areas = [OverlayTriggerArea.OpenWorld, OverlayTriggerArea.Abyss] };
        Assert.Equal(expected, OverlayTriggerPolicy.Matches(rules, new(Area: area)));
    }

    [Fact]
    public void AllConfiguredConditionsMustMatchAlongsideTheSelectedArea()
    {
        var rules = new OverlayTriggerRules
        {
            Areas = [OverlayTriggerArea.Abyss, OverlayTriggerArea.Instances],
            Foreground = OverlayTriggerRequirement.Required, SessionActive = OverlayTriggerRequirement.Required,
            WorldMap = OverlayTriggerRequirement.Required, EscMenu = OverlayTriggerRequirement.Excluded,
            MinimapMissing = OverlayTriggerRequirement.Excluded,
        };
        var matching = new OverlayTriggerContextSnapshot(OverlayTriggerArea.Abyss, true, true, true, false, false);
        Assert.True(OverlayTriggerPolicy.Matches(rules, matching));
        Assert.True(OverlayTriggerPolicy.Matches(rules, matching with { Area = OverlayTriggerArea.Instances }));
        var nonmatching = new[]
        {
            matching with { Area = OverlayTriggerArea.OpenWorld }, matching with { Area = null },
            matching with { Foreground = false }, matching with { Foreground = null },
            matching with { SessionActive = false }, matching with { SessionActive = null },
            matching with { WorldMap = false }, matching with { WorldMap = null },
            matching with { EscMenu = true }, matching with { EscMenu = null },
            matching with { MinimapMissing = true }, matching with { MinimapMissing = null },
        };
        Assert.All(nonmatching, context => Assert.False(OverlayTriggerPolicy.Matches(rules, context)));
    }

    [Fact]
    public void NormalizationCreatesAStableUniqueReadOnlyAreaSelectionWithoutChangingItsInput()
    {
        var areas = new List<OverlayTriggerArea>
            { OverlayTriggerArea.Instances, OverlayTriggerArea.OpenWorld, OverlayTriggerArea.Instances };
        var input = new OverlayTriggerRules { Areas = areas, EscMenu = OverlayTriggerRequirement.Excluded };
        var normalized = OverlayTriggerRules.Normalize(input)!;
        Assert.Equal(new[] { OverlayTriggerArea.OpenWorld, OverlayTriggerArea.Instances }, normalized.Areas);
        Assert.IsType<System.Collections.ObjectModel.ReadOnlyCollection<OverlayTriggerArea>>(normalized.Areas);
        areas.Clear();
        Assert.Equal(2, normalized.Areas.Count);
        Assert.Equal(OverlayTriggerRequirement.Excluded, normalized.EscMenu);
        Assert.True(OverlayTriggerPolicy.Matches(normalized, new(OverlayTriggerArea.Instances, EscMenu: false)));
    }

    [Fact]
    public void UndefinedRulesCannotGrantVisibilityOrBeNormalizedIntoPermissiveDefaults()
    {
        var invalid = new[]
        {
            new OverlayTriggerRules { Areas = null! },
            new OverlayTriggerRules { Areas = [(OverlayTriggerArea)99] },
            new OverlayTriggerRules { Foreground = (OverlayTriggerRequirement)99 },
            new OverlayTriggerRules { SessionActive = (OverlayTriggerRequirement)99 },
            new OverlayTriggerRules { WorldMap = (OverlayTriggerRequirement)99 },
            new OverlayTriggerRules { EscMenu = (OverlayTriggerRequirement)99 },
            new OverlayTriggerRules { MinimapMissing = (OverlayTriggerRequirement)99 },
        };
        foreach (var rules in invalid)
        {
            Assert.False(OverlayTriggerPolicy.Matches(rules, new()));
            Assert.Throws<ArgumentException>(() => OverlayTriggerRules.Normalize(rules));
        }
        Assert.False(OverlayTriggerPolicy.Matches(new() { Areas = [OverlayTriggerArea.Abyss] }, new(Area: (OverlayTriggerArea)99)));
    }

    [Fact]
    public async Task ComponentRendersThreeAreaChoicesFiveConditionsAndItsSuppliedTranslationWithoutLiveClaims()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<OverlayWindowTriggers>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(OverlayWindowTriggers.Rules)] = new OverlayTriggerRules
                {
                    Areas = [OverlayTriggerArea.Abyss], Foreground = OverlayTriggerRequirement.Required,
                    EscMenu = OverlayTriggerRequirement.Excluded,
                },
                [nameof(OverlayWindowTriggers.UiT)] = (Func<string, string>)(text => "translated:" + text),
            }))).ToHtmlString());
        Assert.Equal(3, Regex.Matches(html, "type=\"checkbox\"").Count);
        Assert.Equal(5, Regex.Matches(html, "<select\\b").Count);
        Assert.Contains("translated:Sichtbarkeitsbedingungen", html);
        Assert.Contains("value=\"Required\" selected", html);
        Assert.Contains("value=\"Excluded\" selected", html);
        Assert.DoesNotContain("aria-live", html);
    }
}
