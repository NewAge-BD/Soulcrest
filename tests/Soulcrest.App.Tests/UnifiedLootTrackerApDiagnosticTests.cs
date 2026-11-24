using System.Text.Json;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Theory]
    [InlineData("abyss-points", true)]
    [InlineData("abyss-points-hour", true)]
    [InlineData("abyss-points", false)]
    [InlineData("abyss-points-hour", false)]
    public async Task ApWidgetDiagnosisRequiresOwnQualifiedSessionIncomeWithoutStartingSources(string kind, bool abyss)
    {
        await using var fixture = new Fixture(abyss: abyss, start: DateTimeOffset.UtcNow.AddMinutes(-2));
        if (!abyss)
        {
            fixture.WorldKind = WorldContextKind.Instance;
            fixture.Category = "Party";
            fixture.Source.InInstance = true;
            fixture.Settings.Current.AbyssTrackingEnabled = false;
        }
        fixture.Source.SetAbsolute(fixture.Source.AbyssSnapshot with { AbyssPointGainsQualified = false });
        await fixture.Ready();
        using var editor = new OverlayService(Path.Combine(fixture.Root, "ap-widget-diagnosis"), new OverlayCollectionSettings());
        Assert.True((await editor.SaveAsync(editor.Settings with
        {
            Enabled = true, Widgets = [OverlayCatalog.CreateWidget(kind)],
        })).Succeeded);
        editor.SetRuntimeState(editor.SelectedOverlayId, true);
        var feature = "widget-" + kind;
        var dependencies = DiagnosticFeatureCatalog.Resolve([feature]).Select(item => item.Id).ToArray();
        Assert.Contains("loot-sessions", dependencies);
        Assert.Contains("network", dependencies);
        Assert.DoesNotContain("loot-abyss", dependencies); // AP needs no rank/farm-cache service.
        var contributor = DiagnosticContributors.Create(new BoundWidgetProvider(editor, fixture.Settings, fixture.Service, fixture.Source))
            .Single(item => item.FeatureIds.Contains(feature));
        var unknown = contributor.Collect(feature);
        Assert.Equal(DiagnosticStatus.Waiting, unknown.Status);
        Assert.Equal(false, unknown.Facts["qualified"]);
        // Abyss auto-start retained its initially qualified zero; revocation must still wait.
        Assert.Equal(abyss, unknown.Facts["pointsKnown"]);

        fixture.Source.SetAbsolute(fixture.Source.AbyssSnapshot with
        {
            AbyssPointGainsQualified = true, AbyssPointBalanceObservedAt = fixture.Clock.GetUtcNow(),
        });
        fixture.Source.Points(5, 1);
        await fixture.Ready();
        Assert.Equal(kind == "abyss-points" ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting,
            contributor.Collect(feature).Status);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Source.Points(17, 2);
        await fixture.Ready();
        Assert.Equal(22, fixture.Service.OverlayState.Session!.AbyssPoints);
        var confirmed = contributor.Collect(feature);
        Assert.Equal(DiagnosticStatus.Healthy, confirmed.Status);
        Assert.Equal(true, confirmed.Facts["sourceAvailable"]);
        Assert.Equal(true, confirmed.Facts["qualified"]);
        Assert.Equal(true, confirmed.Facts["pointsKnown"]);
        Assert.Equal(true, confirmed.Facts["hourlyRateAvailable"]);
        var journalCount = fixture.Journal.Count;
        var serialized = JsonSerializer.Serialize(contributor.Collect(feature));
        Assert.Equal(journalCount, fixture.Journal.Count);
        Assert.DoesNotContain("Synthetic character", serialized);
        Assert.DoesNotContain(fixture.Root, serialized);
        Assert.DoesNotContain(fixture.Service.OverlayState.Session.Id, serialized);

        fixture.Source.SetAbsolute(fixture.Source.AbyssSnapshot with { AbyssPointGainsQualified = false });
        await fixture.Ready();
        Assert.Equal(DiagnosticStatus.Waiting, contributor.Collect(feature).Status);
        Assert.Equal(false, contributor.Collect(feature).Facts["qualified"]);
        fixture.Settings.Current.LootTrackingEnabled = false;
        Assert.Equal(DiagnosticStatus.Disabled, contributor.Collect(feature).Status);
    }
}
