using System.Text.Json;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Theory]
    [InlineData("bound-kina")]
    [InlineData("bound-kina-hour")]
    public async Task BoundWidgetDiagnosisRequiresTheLiveQualifiedSessionAndKeepsItsFactsAnonymous(string kind)
    {
        await using var fixture = new Fixture(abyss: true, start: DateTimeOffset.UtcNow.AddMinutes(-2));
        await fixture.Ready();
        using var editor = new OverlayService(Path.Combine(fixture.Root, "bound-widget-editor"), new OverlayCollectionSettings());
        Assert.True((await editor.SaveAsync(editor.Settings with
        {
            Enabled = true, Widgets = [OverlayCatalog.CreateWidget(kind)],
        })).Succeeded);
        editor.SetRuntimeState(editor.SelectedOverlayId, true);
        var feature = "widget-" + kind;
        var contributor = DiagnosticContributors.Create(new BoundWidgetProvider(editor, fixture.Settings, fixture.Service, fixture.Source))
            .Single(item => item.FeatureIds.Contains(feature));
        var unknown = contributor.Collect(feature);
        Assert.Equal(DiagnosticStatus.Waiting, unknown.Status);
        Assert.Equal(true, unknown.Facts["sourceAvailable"]);
        Assert.Equal(false, unknown.Facts["qualified"]);
        Assert.Equal(false, unknown.Facts["boundKnown"]);

        fixture.Source.SetAbsolute(fixture.Source.AbyssSnapshot with
        {
            BoundKinaGainsQualified = true, BoundKinaGainsObservedAt = fixture.Clock.GetUtcNow(),
        });
        await fixture.Ready();
        var zero = contributor.Collect(feature);
        Assert.Equal(kind == "bound-kina" ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting, zero.Status);
        Assert.Equal(true, zero.Facts["qualified"]);
        Assert.Equal(true, zero.Facts["boundKnown"]);
        Assert.Equal(false, zero.Facts["hourlyRateAvailable"]);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Source.BoundKina(10, 1);
        await fixture.Ready();
        var confirmed = contributor.Collect(feature);
        Assert.Equal(DiagnosticStatus.Healthy, confirmed.Status);
        Assert.Equal(true, confirmed.Facts["hourlyRateAvailable"]);
        var serialized = JsonSerializer.Serialize(confirmed);
        Assert.DoesNotContain("Synthetic character", serialized);
        Assert.DoesNotContain(fixture.Root, serialized);
        Assert.DoesNotContain(fixture.Service.OverlayState.Session!.Id, serialized);
        var journalCount = fixture.Journal.Count;
        Assert.Equal(DiagnosticStatus.Healthy, contributor.Collect(feature).Status);
        Assert.Equal(journalCount, fixture.Journal.Count);

        fixture.Source.SetAbsolute(fixture.Source.AbyssSnapshot with { BoundKinaGainsQualified = false });
        await fixture.Ready();
        var revoked = contributor.Collect(feature);
        Assert.Equal(DiagnosticStatus.Waiting, revoked.Status);
        Assert.Equal(false, revoked.Facts["qualified"]);
        Assert.Equal(true, revoked.Facts["boundKnown"]);
        fixture.Settings.Current.AbyssTrackingEnabled = false;
        Assert.Equal(DiagnosticStatus.Disabled, contributor.Collect(feature).Status);
        fixture.Settings.Current.AbyssTrackingEnabled = true;
        fixture.Settings.Current.LootTrackingEnabled = false;
        Assert.Equal(DiagnosticStatus.Disabled, contributor.Collect(feature).Status);
    }

    [Theory]
    [InlineData("missing-time")]
    [InlineData("future-time")]
    [InlineData("stopped")]
    [InlineData("no-session")]
    public async Task BoundWidgetDiagnosisDoesNotTreatMissingStaleOrOutsideFactsAsHealthy(string variant)
    {
        await using var fixture = new Fixture(abyss: true, start: DateTimeOffset.UtcNow.AddMinutes(-2));
        await fixture.Ready();
        using var editor = new OverlayService(Path.Combine(fixture.Root, "bound-widget-editor"), new OverlayCollectionSettings());
        Assert.True((await editor.SaveAsync(editor.Settings with
        {
            Enabled = true, Widgets = [OverlayCatalog.CreateWidget("bound-kina")],
        })).Succeeded);
        editor.SetRuntimeState(editor.SelectedOverlayId, true);
        fixture.Source.SetAbsolute(fixture.Source.AbyssSnapshot with
        {
            BoundKinaGainsQualified = true,
            BoundKinaGainsObservedAt = variant == "missing-time" ? null
                : variant == "future-time" ? DateTimeOffset.UtcNow.AddMinutes(1) : fixture.Clock.GetUtcNow(),
        });
        await fixture.Ready();
        if (variant == "stopped") fixture.Source.Running = false;
        if (variant == "no-session") { fixture.SetWorld(false); await fixture.Ready(); }
        var contributor = DiagnosticContributors.Create(new BoundWidgetProvider(editor, fixture.Settings, fixture.Service, fixture.Source))
            .Single(item => item.FeatureIds.Contains("widget-bound-kina"));
        var result = contributor.Collect("widget-bound-kina");
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(false, result.Facts["qualified"]);
        if (variant == "stopped") Assert.Equal(false, result.Facts["sourceAvailable"]);
        if (variant == "no-session") Assert.Equal(false, result.Facts["boundKnown"]);
    }

    private sealed class BoundWidgetProvider(params object[] instances) : IServiceProvider
    {
        public object? GetService(Type serviceType) => instances.FirstOrDefault(serviceType.IsInstanceOfType);
    }

    [Fact]
    public async Task DailyBoundWidgetDiagnosisRequiresCurrentConfirmedTotalsAndDoesNotStartASession()
    {
        await using var fixture = new Fixture(start: DateTimeOffset.UtcNow.AddMinutes(-2));
        await fixture.Ready();
        using var editor = new OverlayService(Path.Combine(fixture.Root, "daily-widget"), new OverlayCollectionSettings());
        Assert.True((await editor.SaveAsync(editor.Settings with
        {
            Enabled = true, Widgets = [OverlayCatalog.CreateWidget("daily-kina")],
        })).Succeeded);
        editor.SetRuntimeState(editor.SelectedOverlayId, true);
        var contributor = DiagnosticContributors.Create(new BoundWidgetProvider(editor, fixture.Settings, fixture.Service, fixture.Source))
            .Single(item => item.FeatureIds.Contains("widget-daily-kina"));
        var unknown = contributor.Collect("widget-daily-kina");
        Assert.Equal(DiagnosticStatus.Waiting, unknown.Status);
        Assert.Equal("bound", unknown.Facts["currency"]);
        Assert.Equal(false, unknown.Facts["dailyKnown"]);
        fixture.Source.Daily(0); await fixture.Ready();
        var journalCount = fixture.Journal.Count;
        var zero = contributor.Collect("widget-daily-kina");
        Assert.Equal(DiagnosticStatus.Healthy, zero.Status);
        Assert.Equal(true, zero.Facts["dailyKnown"]);
        Assert.Equal(true, zero.Facts["dailyConfirmed"]);
        Assert.Equal(1_000_000, zero.Facts["dailyLimit"]);
        Assert.Empty(fixture.Service.Snapshot.Sessions);
        Assert.Equal(journalCount, fixture.Journal.Count);
        fixture.Settings.Current.MonsterLootTrackingEnabled = false;
        Assert.Equal(DiagnosticStatus.Disabled, contributor.Collect("widget-daily-kina").Status);
    }

    [Theory]
    [InlineData("bound-kina")]
    [InlineData("bound-kina-hour")]
    public async Task BoundWidgetDiagnosisAcceptsQualifiedDailyPveWithoutRequiringAbyssTracking(string kind)
    {
        await using var fixture = new Fixture(start: DateTimeOffset.UtcNow.AddMinutes(-2));
        fixture.Settings.Current.AbyssTrackingEnabled = false;
        fixture.Source.Daily(100);
        fixture.Source.Drop(510116011, 1, 1);
        await fixture.Ready();
        using var editor = new OverlayService(Path.Combine(fixture.Root, "daily-bound-widget"), new OverlayCollectionSettings());
        Assert.True((await editor.SaveAsync(editor.Settings with
        {
            Enabled = true, Widgets = [OverlayCatalog.CreateWidget(kind)],
        })).Succeeded);
        editor.SetRuntimeState(editor.SelectedOverlayId, true);
        var feature = "widget-" + kind;
        var contributor = DiagnosticContributors.Create(new BoundWidgetProvider(editor, fixture.Settings, fixture.Service, fixture.Source))
            .Single(item => item.FeatureIds.Contains(feature));
        var zero = contributor.Collect(feature);
        Assert.Equal(kind == "bound-kina" ? DiagnosticStatus.Healthy : DiagnosticStatus.Waiting, zero.Status);
        Assert.Equal(true, zero.Facts["qualified"]);
        Assert.Equal(true, zero.Facts["boundKnown"]);
        Assert.Equal("daily-monster", zero.Facts["boundSource"]);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Source.Daily(110);
        fixture.Source.Kina(10, 1);
        await fixture.Ready();
        var journalCount = fixture.Journal.Count;
        Assert.Equal(DiagnosticStatus.Healthy, contributor.Collect(feature).Status);
        Assert.Equal(journalCount, fixture.Journal.Count);
        var serialized = JsonSerializer.Serialize(contributor.Collect(feature));
        Assert.DoesNotContain("Synthetic character", serialized);
        Assert.DoesNotContain(fixture.Root, serialized);
        Assert.DoesNotContain(fixture.Service.OverlayState.Session!.Id, serialized);
        fixture.Source.Running = false;
        var stopped = contributor.Collect(feature);
        Assert.Equal(DiagnosticStatus.Waiting, stopped.Status);
        Assert.Equal(false, stopped.Facts["qualified"]);
        fixture.Source.Running = true;
        fixture.Settings.Current.MonsterLootTrackingEnabled = false;
        Assert.Equal(DiagnosticStatus.Disabled, contributor.Collect(feature).Status);
    }
}
