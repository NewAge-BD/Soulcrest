using System.Text.Json;
using Soulcrest.App.Capture;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class GenusInsightStaticDiagnosticsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static GenusInsightSnapshot Source => GenusInsightOverlayExamples.Create(At, "anonymous-diagnostic-profile");
    private static GenusMenuSnapshot Menu => new(true, "cogni", 1, 1, 1);
    private static DiagnosticOverlayRuntimeState Runtime => new(true, true, false, At);

    [Fact]
    public void DisabledPreferenceWinsOverMissingSourcesAndDisposedHost()
    {
        var result = DiagnosticContributors.GenusInsightOverlaySnapshot(false,
            Runtime with { Disposed = true }, null, null, At);
        Assert.Equal(DiagnosticStatus.Disabled, result.Status);
        Assert.Equal(false, result.Facts["enabled"]);
        Assert.Equal(true, result.Facts["disposed"]);
        Assert.Equal(false, result.Facts["liveDisplayAllowed"]);
    }

    [Fact]
    public void MissingHostWaitsEvenWithCurrentOwnDataAndAnOpenMenu()
    {
        var result = DiagnosticContributors.GenusInsightOverlaySnapshot(true, null, Source, Menu, At);
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(false, result.Facts["nativeVisibilityKnown"]);
        Assert.Null(result.Facts["nativeVisible"]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void HiddenOrUncreatedHostNeverClaimsHealthy(bool visible, bool handleCreated)
    {
        var result = Project(runtime: Runtime with { Visible = visible, HandleCreated = handleCreated });
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(visible, result.Facts["nativeVisible"]);
        Assert.Equal(handleCreated, result.Facts["handleCreated"]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("closed")]
    [InlineData("expired")]
    [InlineData("unbound-generation")]
    [InlineData("unknown-category")]
    [InlineData("missing-preset")]
    public void MissingExpiredOrUnqualifiedMenuCannotReuseAVisibleHost(string reason)
    {
        var menu = reason switch
        {
            "missing" => null,
            "closed" => Menu with { MenuVisible = false },
            "expired" => Menu with { MenuVisible = null, Genus = null, ViewedPreset = null },
            "unbound-generation" => Menu with { SourceGeneration = 0 },
            "unknown-category" => Menu with { Genus = "unqualified-category" },
            _ => Menu with { ViewedPreset = null }
        };
        var result = DiagnosticContributors.GenusInsightOverlaySnapshot(true, Runtime, Source, menu, At);
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(false, result.Facts["liveDisplayAllowed"]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("historical")]
    [InlineData("missing-generation")]
    [InlineData("missing-profile")]
    public void MissingExpiredOrHistoricalOwnSourceCannotReuseNativeVisibility(string reason)
    {
        var source = reason switch
        {
            "missing" => null,
            "expired" => Source with { IsCurrent = false },
            "historical" => Source with { IsHistorical = true },
            "missing-generation" => Source with { Generation = 0 },
            _ => Source with { ProfileId = null }
        };
        var result = DiagnosticContributors.GenusInsightOverlaySnapshot(true, Runtime, source, Menu, At);
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(false, result.Facts["sourceCurrent"]);
        Assert.Equal(false, result.Facts["liveDisplayAllowed"]);
    }

    [Fact]
    public void DisposedHostIsAnErrorWhileTheFeatureRemainsEnabled()
    {
        var result = Project(runtime: Runtime with { Disposed = true });
        Assert.Equal(DiagnosticStatus.Error, result.Status);
        Assert.Equal(true, result.Facts["disposed"]);
    }

    [Theory]
    [InlineData(-6, 6000)]
    [InlineData(1, -1000)]
    public void OldOrFutureHostObservationsAreWarnings(int observedSeconds, double expectedAge)
    {
        var result = Project(runtime: Runtime with { ObservedAt = At.AddSeconds(observedSeconds) });
        Assert.Equal(DiagnosticStatus.Warning, result.Status);
        Assert.Equal(expectedAge, result.Facts["runtimeAgeMs"]);
    }

    [Fact]
    public void FreshVisibleHostAndNineCurrentOwnSlotsAreHealthy()
    {
        var result = Project();
        Assert.Equal("overlay-genus-insight", result.FeatureId);
        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
        Assert.Equal(9, result.Facts["slots"]);
        Assert.Equal(9, result.Facts["confirmedSlots"]);
        Assert.Equal(true, result.Facts["sourceCurrent"]);
        Assert.Equal(true, result.Facts["liveDisplayAllowed"]);
    }

    [Fact]
    public void AnUnknownSlotIsReportedAsPartialInsteadOfAHealthyZero()
    {
        var source = Source;
        source = source with
        {
            Boards = source.Boards.Select(board => board.Genus != "cogni" ? board : board with
            {
                Slots = board.Slots.Select(slot => slot.Slot != 4 ? slot : slot with
                    { IsCurrent = false, StatId = null, Value = null, RollFraction = null }).ToArray()
            }).ToArray()
        };
        var result = Project(source: source);
        Assert.Equal(DiagnosticStatus.Warning, result.Status);
        Assert.Equal(9, result.Facts["slots"]);
        Assert.Equal(8, result.Facts["confirmedSlots"]);
    }

    [Fact]
    public void SourceIdentitiesAndPrivateStatLabelsNeverEnterSerializedFacts()
    {
        const string privateText = "Synthetic private identity C:/private/example 192.0.2.8";
        var source = Source with { ProfileId = privateText };
        source = source with
        {
            Boards = source.Boards.Select(board => board with
            {
                Slots = board.Slots.Select(slot => slot with { LabelDe = privateText, LabelEn = privateText }).ToArray()
            }).ToArray()
        };
        var result = Project(source: source);
        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
        Assert.DoesNotContain(privateText, JsonSerializer.Serialize(result.Facts));
        Assert.DoesNotContain(result.Facts.Keys, key => key.Contains("Profile", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Actor", StringComparison.OrdinalIgnoreCase) || key.Contains("CharacterId", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Facts.Values, value => Assert.True(value is null or bool or int or double or DateTimeOffset));
    }

    [Fact]
    public void SelectionIncludesTheOwnGenusSourceAndCaptureDependencies()
    {
        var feature = Assert.Single(DiagnosticFeatureCatalog.All, feature => feature.Id == "overlay-genus-insight");
        Assert.Contains("combat-genus-insight", feature.Dependencies);
        Assert.Contains("capture", feature.Dependencies);
        var ids = DiagnosticFeatureCatalog.Resolve([feature.Id]).Select(item => item.Id).ToArray();
        Assert.Contains("network", ids);
        Assert.Contains("system", ids);
        Assert.DoesNotContain("overlay-editor", ids);
        Assert.DoesNotContain("loot-market", ids);
    }

    [Fact]
    public void SettingsOnlyDiagnosisWaitsWithoutCreatingSourcesOrSavingPreferences()
    {
        var settings = new SettingsService();
        settings.Current.GenusInsightOverlayEnabled = true;
        var provider = new ExistingOnlyProvider(settings);
        var before = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var contributors = DiagnosticContributors.Create(provider);
        Assert.Equal(0, provider.Reads);
        var result = Assert.Single(contributors, item => item.FeatureIds.Contains("overlay-genus-insight"))
            .Collect("overlay-genus-insight");
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(false, result.Facts["presentationServiceAvailable"]);
        Assert.Equal(false, result.Facts["nativeVisibilityKnown"]);
        Assert.Null(provider.GetService(typeof(GameCaptureService)));
        Assert.Null(provider.GetService(typeof(GenusMenuObservationService)));
        Assert.Equal(before, File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null);
    }

    private static DiagnosticResult Project(GenusInsightSnapshot? source = null, DiagnosticOverlayRuntimeState? runtime = null) =>
        DiagnosticContributors.GenusInsightOverlaySnapshot(true, runtime ?? Runtime, source ?? Source, Menu, At);

    private sealed class ExistingOnlyProvider(params object[] instances) : IServiceProvider
    {
        public int Reads { get; private set; }
        public object? GetService(Type serviceType)
        {
            Reads++;
            return instances.FirstOrDefault(serviceType.IsInstanceOfType);
        }
    }
}
