using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorRecordingVisibilityTests
{
    [Fact]
    public async Task SelectedEditorDiagnosisReportsRequestedRecordingPolicyWithoutStartingCaptureOrWritingLayout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SoulcrestRecordingPolicyTests", Guid.NewGuid().ToString("N"));
        using var editor = new OverlayService(directory);
        Assert.True((await editor.SaveAsync(editor.Settings with { CaptureExcluded = false })).Succeeded);
        Assert.True((await editor.CreateOverlayAsync("Synthetic private window")).Succeeded);
        var before = File.ReadAllBytes(Path.Combine(directory, "overlay.json"));
        var contributors = DiagnosticContributors.Create(new ExistingEditor(editor));
        var diagnostic = contributors.Single(item => item.FeatureIds.Contains("overlay-editor")).Collect("overlay-editor");
        Assert.Equal(1, diagnostic.Facts["recordingRequestedOverlays"]);
        Assert.Equal(editor.Overlays.Count - 1, diagnostic.Facts["recordingExcludedOverlays"]);
        Assert.Null(diagnostic.Facts["recordingSourceSafe"]);
        Assert.DoesNotContain("Synthetic private window", System.Text.Json.JsonSerializer.Serialize(diagnostic));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "overlay.json")));
    }

    private sealed class ExistingEditor(IOverlayService editor) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IOverlayService) ? editor : null;
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    public void PlacementRecordingPolicyKeepsEditorChoiceIndependentUntilStaticContentSharesTheSurface(
        bool staticIncluded, bool hasStatic, bool editorExcluded, bool expected)
    {
        var editors = new[] { new OverlaySettings { CaptureExcluded = editorExcluded } };
        Assert.Equal(expected, OverlayPlacementForm.RecordingsAllowed(staticIncluded, hasStatic, editors));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneExcludedEditorProtectsTheEntireCompositeRegardlessOfTheStaticSwitch(bool staticIncluded)
    {
        var included = new OverlaySettings { CaptureExcluded = false };
        var excluded = new OverlaySettings { CaptureExcluded = true };
        Assert.False(OverlayPlacementForm.RecordingsAllowed(staticIncluded, false, [included, excluded]));
        Assert.False(OverlayPlacementForm.RecordingsAllowed(staticIncluded, false, [excluded, included]));
        Assert.True(OverlayPlacementForm.RecordingsAllowed(staticIncluded, false, [included, included]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticOnlyAndEmptyPlacementKeepTheExistingPermissionAndFailClosedBeforeContent(bool staticIncluded)
    {
        Assert.Equal(staticIncluded, OverlayPlacementForm.RecordingsAllowed(staticIncluded, true, []));
        Assert.False(OverlayPlacementForm.RecordingsAllowed(staticIncluded, false, []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnonymousPolicyProjectionIncludesTheWindowRecordingChoiceWithoutChangingVisibility(bool excluded)
    {
        var settings = new OverlaySettings
        {
            Enabled = true, CaptureExcluded = excluded,
            Widgets = [OverlayCatalog.CreateWidget("clock")],
            PositionMonitorId = "synthetic-private-monitor",
        };
        var snapshot = new OverlaySnapshot { SessionId = "synthetic-private-session" };
        var context = new OverlayEditorLiveContext(true, true, false, null, new());
        var inputs = OverlayEditorLiveDiagnostics.Inputs(settings, snapshot, context, true);
        Assert.Equal(excluded, inputs.CaptureExcluded);
        Assert.True(inputs.Result);
        var buffer = new OverlayLiveDiagnosticBuffer();
        buffer.Policy(settings, inputs);
        var json = OverlayEditorLiveDiagnostics.Serialize(buffer.Capture());
        Assert.Contains($"\"CaptureExcluded\":{excluded.ToString().ToLowerInvariant()}", json);
        Assert.DoesNotContain("synthetic-private", json);
        Assert.Equal(excluded, settings.CaptureExcluded);
    }

    [Fact]
    public void ChangingOnlyTheRecordingChoiceProducesOneNewAnonymousPolicyObservation()
    {
        var settings = new OverlaySettings();
        var context = new OverlayEditorLiveContext(true, true, false, null, new());
        var inputs = OverlayEditorLiveDiagnostics.Inputs(settings, new(), context, false);
        Assert.True(inputs.CaptureExcluded);
        var buffer = new OverlayLiveDiagnosticBuffer();
        buffer.Policy(settings, inputs);
        buffer.Policy(settings, inputs);
        buffer.Policy(settings, inputs with { CaptureExcluded = false });
        buffer.Policy(settings, inputs with { CaptureExcluded = false });
        var observations = buffer.Capture().Entries;
        Assert.Equal(2, observations.Count);
        Assert.True(observations[0].Policy!.CaptureExcluded);
        Assert.False(observations[1].Policy!.CaptureExcluded);
        Assert.Equal(observations[0].Window, observations[1].Window);
    }
}
