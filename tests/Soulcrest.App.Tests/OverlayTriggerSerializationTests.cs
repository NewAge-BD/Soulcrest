using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayTriggerSerializationTests
{
    [Fact]
    public async Task OldVersionTwoLayoutsRemainNeutralAndByteIdenticalUntilAnActualEdit()
    {
        using var fixture = new Fixture();
        var document = JsonNode.Parse(JsonSerializer.Serialize(new OverlayCollectionSettings()))!;
        document["Overlays"]![0]!["Settings"]!.AsObject().Remove("TriggerRules");
        fixture.Write(document);
        var before = File.ReadAllBytes(fixture.LayoutPath);
        using var service = fixture.Create();
        Assert.Null(service.LoadError);
        Assert.Null(service.Settings.TriggerRules);
        Assert.True(OverlayTriggerPolicy.Matches(service.Settings.TriggerRules, new()));
        await service.SetPreviewAsync(true);
        await service.SetPreviewAsync(false);
        Assert.Equal(before, File.ReadAllBytes(fixture.LayoutPath));
    }

    [Fact]
    public async Task RulesRoundtripPerWindowAndDuplicationNeverSharesAMutableAreaList()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        var areas = new List<OverlayTriggerArea> { OverlayTriggerArea.Abyss, OverlayTriggerArea.OpenWorld, OverlayTriggerArea.Abyss };
        var rules = new OverlayTriggerRules
        {
            Areas = areas, Foreground = OverlayTriggerRequirement.Required,
            SessionActive = OverlayTriggerRequirement.Required, WorldMap = OverlayTriggerRequirement.Excluded,
            EscMenu = OverlayTriggerRequirement.Any, MinimapMissing = OverlayTriggerRequirement.Excluded,
        };
        Assert.True((await service.SaveAsync(service.Settings with { TriggerRules = rules })).Succeeded);
        areas.Clear();
        Assert.Equal(2, service.Settings.TriggerRules!.Areas.Count);
        Assert.True((await service.CreateOverlayAsync("Kopie", "default")).Succeeded);
        var copiedId = service.SelectedOverlayId;
        Assert.True((await service.CreateOverlayAsync("Unabhängig")).Succeeded);
        Assert.Null(service.Settings.TriggerRules);
        var thirdId = service.SelectedOverlayId;
        Assert.True((await service.SaveAsync(service.Settings with
        {
            TriggerRules = new() { Areas = [OverlayTriggerArea.Instances], SessionActive = OverlayTriggerRequirement.Excluded },
        })).Succeeded);
        using var reopened = fixture.Create();
        Assert.Null(reopened.LoadError);
        var original = reopened.Overlays.Single(window => window.Id == "default").Settings.TriggerRules!;
        var copy = reopened.Overlays.Single(window => window.Id == copiedId).Settings.TriggerRules!;
        var third = reopened.Overlays.Single(window => window.Id == thirdId).Settings.TriggerRules!;
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(copy));
        Assert.NotSame(original.Areas, copy.Areas);
        Assert.Equal(new[] { OverlayTriggerArea.Instances }, third.Areas);
        Assert.Equal(OverlayTriggerRequirement.Excluded, third.SessionActive);
        Assert.Equal(OverlayTriggerRequirement.Required, original.SessionActive);
        var json = File.ReadAllText(fixture.LayoutPath);
        Assert.Contains("\"Required\"", json);
        Assert.Contains("\"OpenWorld\"", json);
        Assert.DoesNotContain("ContextSnapshot", json);
        Assert.DoesNotContain("SessionId", json);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task AppearanceTemplatesExcludeTriggersAndApplyingThemPreservesTheTargetWindowsRules()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        var source = service.Settings with
        {
            Width = 640, TriggerRules = new() { Areas = [OverlayTriggerArea.Abyss], Foreground = OverlayTriggerRequirement.Required },
        };
        Assert.True((await service.SaveTemplateAsync("Aussehen", source)).Succeeded);
        Assert.DoesNotContain("TriggerRules", File.ReadAllText(fixture.TemplatePath));
        using var reopened = fixture.Create();
        var template = Assert.Single(reopened.Templates);
        Assert.Null(template.Layout.TriggerRules);
        var target = new OverlaySettings
        {
            TriggerRules = OverlayTriggerRules.Normalize(new() { Areas = [OverlayTriggerArea.Instances], EscMenu = OverlayTriggerRequirement.Excluded }),
        };
        var applied = template.ApplyTo(target);
        Assert.Equal(640, applied.Width);
        Assert.Same(target.TriggerRules, applied.TriggerRules);
    }

    [Theory]
    [InlineData("{\"Areas\":null}")]
    [InlineData("{\"Areas\":[\"Abyss\",\"Abyss\"]}")]
    [InlineData("{\"Areas\":[\"FutureArea\"]}")]
    [InlineData("{\"Areas\":[99]}")]
    [InlineData("{\"Areas\":[null]}")]
    [InlineData("{\"Foreground\":\"FutureRule\"}")]
    [InlineData("{\"Foreground\":99}")]
    [InlineData("{\"SessionActive\":-1}")]
    [InlineData("{\"WorldMap\":null}")]
    [InlineData("{\"EscMenu\":true}")]
    [InlineData("{\"MinimapMissing\":3}")]
    [InlineData("{\"FutureCondition\":\"Required\"}")]
    [InlineData("[]")]
    [InlineData("false")]
    public async Task CorruptOrUnknownRulesAreReadOnlyAndNeverSilentlyLoseARestriction(string corrupt)
    {
        using var fixture = new Fixture();
        var document = JsonNode.Parse(JsonSerializer.Serialize(new OverlayCollectionSettings()))!;
        document["Overlays"]![0]!["Settings"]!["TriggerRules"] = JsonNode.Parse(corrupt);
        fixture.Write(document);
        var before = File.ReadAllBytes(fixture.LayoutPath);
        using var service = fixture.Create();
        Assert.NotNull(service.LoadError);
        Assert.False((await service.SaveAsync(new OverlaySettings())).Succeeded);
        Assert.False((await service.ReloadAsync()).Succeeded);
        Assert.Equal(before, File.ReadAllBytes(fixture.LayoutPath));
    }

    [Fact]
    public async Task InvalidProgrammaticRulesAreRejectedWithoutChangingACommittedLayout()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        Assert.True((await service.SaveAsync(service.Settings with { Width = 520 })).Succeeded);
        var before = File.ReadAllBytes(fixture.LayoutPath);
        var result = await service.SaveAsync(service.Settings with
        {
            TriggerRules = new() { Foreground = (OverlayTriggerRequirement)99 },
        });
        Assert.False(result.Succeeded);
        Assert.Null(service.LoadError);
        Assert.Equal(520, service.Settings.Width);
        Assert.Equal(before, File.ReadAllBytes(fixture.LayoutPath));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Soulcrest-overlay-trigger-tests-" + Guid.NewGuid().ToString("N"));
        internal string LayoutPath => Path.Combine(Root, "overlay.json");
        internal string TemplatePath => Path.Combine(Root, "overlay-templates.json");
        internal OverlayService Create() => new(Root, new OverlayCollectionSettings());
        internal void Write(JsonNode document) { Directory.CreateDirectory(Root); File.WriteAllText(LayoutPath, document.ToJsonString()); }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
