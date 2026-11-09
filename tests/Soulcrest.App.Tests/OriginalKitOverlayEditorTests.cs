using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OriginalKitOverlayEditorTests
{
    [Theory]
    [InlineData("de", "Overlay aktivieren", "Overlay deaktivieren")]
    [InlineData("en", "Activate Overlay", "Deactivate Overlay")]
    public async Task HeaderActivationWorksWithoutNativePlacementAndPersistsOnlyItsSelectedWindow(string language, string activate, string deactivate)
    {
        await using var fixture = await Fixture.Create(language: language, seedLegacy: false);
        Assert.Null(fixture.Placement);
        var first = fixture.Service.SelectedOverlayId;
        Assert.True((await fixture.Service.CreateOverlayAsync("Header window")).Succeeded);
        await fixture.Refresh();
        var selected = fixture.Service.SelectedOverlayId;
        var acquisitions = fixture.Source.Acquisitions.Count;
        var html = await fixture.Html();
        Assert.Contains(activate, html);
        Assert.Contains("aria-pressed=\"false\"", ActivationMarkup(html));
        Assert.DoesNotContain("disabled", ActivationMarkup(html));
        Assert.True(html.IndexOf("data-activate-overlay", StringComparison.Ordinal) < html.IndexOf("data-start-place-mode", StringComparison.Ordinal));
        await fixture.ClickAttribute("data-activate-overlay", "true");
        Assert.True(fixture.Service.Settings.Enabled);
        Assert.Equal(selected, fixture.Service.SelectedOverlayId);
        Assert.False(fixture.Service.Overlays.Single(window => window.Id == first).Settings.Enabled);
        Assert.False(fixture.Service.State.Previewing);
        Assert.Equal(acquisitions, fixture.Source.Acquisitions.Count);
        html = await fixture.Html();
        Assert.Contains(deactivate, html);
        Assert.Contains("aria-pressed=\"true\"", ActivationMarkup(html));
        Assert.Contains("aria-busy=\"false\"", ActivationMarkup(html));
        using var reopened = new OverlayService(fixture.Directory);
        Assert.True(reopened.Overlays.Single(window => window.Id == selected).Settings.Enabled);
        await fixture.ClickAttribute("data-activate-overlay", "true");
        Assert.False(fixture.Service.Settings.Enabled);
        Assert.Equal(selected, fixture.Service.SelectedOverlayId);
        Assert.Contains(activate, await fixture.Html());
    }

    [Fact]
    public async Task HeaderActivationSerializesCompletedAndLateGesturesAndRejectsADuplicateBusyClick()
    {
        var widget = OverlayCatalog.CreateWidget("clock");
        await using var fixture = await Fixture.Create(seedLegacy: false, initialSettings: new() { Widgets = [widget] });
        var selected = fixture.Service.SelectedOverlayId;
        var acquisitions = fixture.Source.Acquisitions.Count;
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task geometry, activation, lateGeometry;
        try
        {
            geometry = await fixture.BeginInvoke("CommitWidgetGeometry", widget.Id, 40d, 56d, widget.Width, widget.Height);
            activation = await fixture.BeginInvoke("ToggleWindowActivation");
            lateGeometry = await fixture.BeginInvoke("CommitWidgetGeometry", widget.Id, 72d, 88d, widget.Width, widget.Height);
            Assert.False(geometry.IsCompleted);
            Assert.False(activation.IsCompleted);
            Assert.True((await fixture.BeginInvoke("ToggleWindowActivation")).IsCompleted);
            await fixture.Refresh();
            var button = ActivationMarkup(await fixture.Html());
            Assert.Contains("disabled", button);
            Assert.Contains("aria-busy=\"true\"", button);
            Assert.False(fixture.Service.Settings.Enabled);
        }
        finally { gate.Release(); }
        await Task.WhenAll(geometry, activation, lateGeometry).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Service.Settings.Enabled);
        Assert.Equal(selected, fixture.Service.SelectedOverlayId);
        var saved = Assert.Single(fixture.Service.Settings.Widgets);
        Assert.Equal((72d, 88d), (saved.X, saved.Y));
        Assert.Equal(acquisitions, fixture.Source.Acquisitions.Count);
        Assert.False(await fixture.EditorField<bool>("_changingActivation"));
    }

    [Fact]
    public async Task AFailedPrecedingGestureKeepsItsDraftAndErrorAndBlocksTheQueuedActivation()
    {
        var widget = OverlayCatalog.CreateWidget("clock");
        await using var fixture = await Fixture.Create(seedLegacy: false, initialSettings: new() { Widgets = [widget] });
        var file = Path.Combine(fixture.Directory, "overlay.json");
        var before = await File.ReadAllBytesAsync(file);
        // Reads still work. Only atomic replacement is denied when G1 reaches storage.
        using var replacementBlocker = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task gesture, activation;
        try
        {
            gesture = await fixture.BeginInvoke("CommitWidgetGeometry", widget.Id, 40d, 56d, widget.Width, widget.Height);
            activation = await fixture.BeginInvoke("ToggleWindowActivation");
        }
        finally { gate.Release(); }
        await Task.WhenAll(gesture, activation).WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Refresh();
        Assert.NotNull(fixture.Service.LoadError);
        Assert.False(fixture.Service.Settings.Enabled);
        var draft = await fixture.EditorField<OverlaySettings>("_settings");
        var unsaved = Assert.Single(draft.Widgets);
        Assert.Equal((40d, 56d), (unsaved.X, unsaved.Y));
        Assert.False(draft.Enabled);
        var error = await fixture.EditorField<string?>("_error");
        Assert.NotNull(error);
        Assert.Contains(error, await fixture.Html());
        Assert.False(await fixture.EditorField<bool>("_saving"));
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        using var reopened = new OverlayService(fixture.Directory);
        Assert.Null(reopened.LoadError);
        Assert.False(reopened.Settings.Enabled);
        var saved = Assert.Single(reopened.Settings.Widgets);
        Assert.Equal((widget.X, widget.Y), (saved.X, saved.Y));
    }

    [Fact]
    public async Task AFailedLateGestureRetainsItsDraftAndErrorAfterTheEarlierActivationWasCommitted()
    {
        var widget = OverlayCatalog.CreateWidget("clock");
        await using var fixture = await Fixture.Create(seedLegacy: false, initialSettings: new() { Widgets = [widget] });
        var file = Path.Combine(fixture.Directory, "overlay.json");
        FileStream? replacementBlocker = null;
        var committed = new List<(bool Enabled, double X)>();
        void ObserveCommit()
        {
            if (fixture.Service.LoadError is not null) return;
            var settings = fixture.Service.Settings;
            var saved = settings.Widgets.Single();
            committed.Add((settings.Enabled, saved.X));
            if (settings.Enabled && saved.X == 40)
                replacementBlocker ??= new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        fixture.Service.Changed += ObserveCommit;
        try
        {
            var gate = await fixture.SaveGate();
            await gate.WaitAsync();
            Task firstGesture, activation, lateGesture;
            try
            {
                firstGesture = await fixture.BeginInvoke("CommitWidgetGeometry", widget.Id, 40d, 56d, widget.Width, widget.Height);
                activation = await fixture.BeginInvoke("ToggleWindowActivation");
                lateGesture = await fixture.BeginInvoke("CommitWidgetGeometry", widget.Id, 72d, 88d, widget.Width, widget.Height);
            }
            finally { gate.Release(); }
            await Task.WhenAll(firstGesture, activation, lateGesture).WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Refresh();
            Assert.NotNull(replacementBlocker);
            Assert.Equal(new[] { (false, 40d), (true, 40d) }, committed);
            Assert.NotNull(fixture.Service.LoadError);
            var draft = await fixture.EditorField<OverlaySettings>("_settings");
            var unsaved = Assert.Single(draft.Widgets);
            Assert.Equal((72d, 88d), (unsaved.X, unsaved.Y));
            Assert.True(draft.Enabled);
            var error = await fixture.EditorField<string?>("_error");
            Assert.NotNull(error);
            Assert.Contains(error, await fixture.Html());
            Assert.False(await fixture.EditorField<bool>("_saving"));
            using var reopened = new OverlayService(fixture.Directory);
            Assert.Null(reopened.LoadError);
            Assert.True(reopened.Settings.Enabled);
            var saved = Assert.Single(reopened.Settings.Widgets);
            Assert.Equal((40d, 56d), (saved.X, saved.Y));
        }
        finally
        {
            fixture.Service.Changed -= ObserveCommit;
            replacementBlocker?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedHeaderActivationCannotFollowAnExternalSelectionOrRecreateADeletedWindow(bool delete)
    {
        await using var fixture = await Fixture.Create(seedLegacy: false);
        Assert.True((await fixture.Service.CreateOverlayAsync("Other selected window")).Succeeded);
        await fixture.Refresh();
        var selected = fixture.Service.SelectedOverlayId;
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task activation;
        byte[] afterExternalChange;
        try
        {
            activation = await fixture.BeginInvoke("ToggleWindowActivation");
            Assert.False(activation.IsCompleted);
            Assert.True((delete ? await fixture.Service.DeleteOverlayAsync(selected)
                : await fixture.Service.SelectOverlayAsync("default")).Succeeded);
            await fixture.Refresh();
            afterExternalChange = await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "overlay.json"));
        }
        finally { gate.Release(); }
        await activation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("default", fixture.Service.SelectedOverlayId);
        Assert.Equal("default", await fixture.EditorField<string>("_editingOverlayId"));
        Assert.All(fixture.Service.Overlays, window => Assert.False(window.Settings.Enabled));
        Assert.Equal(afterExternalChange, await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "overlay.json")));
    }

    [Fact]
    public async Task FailedQueuedGestureSaveBlocksActivationAndKeepsThePersistedButtonState()
    {
        await using var fixture = await Fixture.Create();
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task change, activation;
        var file = Path.Combine(fixture.Directory, "overlay.json");
        const string broken = "{ damaged before activation";
        try
        {
            change = await fixture.BeginInvoke("Change", (Func<OverlaySettings, OverlaySettings>)(settings => settings with { Width = 512 }));
            activation = await fixture.BeginInvoke("ToggleWindowActivation");
            await File.WriteAllTextAsync(file, broken);
        }
        finally { gate.Release(); }
        await Task.WhenAll(change, activation).WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Refresh();
        Assert.NotNull(fixture.Service.LoadError);
        Assert.False(fixture.Service.Settings.Enabled);
        Assert.False((await fixture.EditorField<OverlaySettings>("_settings")).Enabled);
        var button = ActivationMarkup(await fixture.Html());
        Assert.Contains("disabled", button);
        Assert.Contains("aria-pressed=\"false\"", button);
        Assert.Equal(broken, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task AnActivationSaveFailureDoesNotLeaveTheHeaderPressed()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false);
        System.IO.Directory.CreateDirectory(fixture.Directory);
        var file = Path.Combine(fixture.Directory, "overlay.json");
        const string broken = "{ invalid activation target";
        await File.WriteAllTextAsync(file, broken);
        await fixture.ClickAttribute("data-activate-overlay", "true");
        Assert.NotNull(fixture.Service.LoadError);
        Assert.False(fixture.Service.Settings.Enabled);
        Assert.False((await fixture.EditorField<OverlaySettings>("_settings")).Enabled);
        Assert.Contains("aria-pressed=\"false\"", ActivationMarkup(await fixture.Html()));
        Assert.Equal(broken, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task NativePlacementEndingBlocksHeaderActivationWithoutChangingItsScope()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false, placementServices: true);
        fixture.Placement!.SetEnding(true);
        await fixture.Refresh();
        Assert.Contains("disabled", ActivationMarkup(await fixture.Html()));
        await fixture.ClickAttribute("data-activate-overlay", "true");
        Assert.False(fixture.Service.Settings.Enabled);
        Assert.False(fixture.Placement.Active);
        Assert.False(Directory.Exists(fixture.Directory));
        fixture.Placement.SetEnding(false);
        await fixture.ClickAttribute("data-activate-overlay", "true");
        Assert.True(fixture.Service.Settings.Enabled);
        Assert.False(fixture.Placement.Active);
    }

    private static string ActivationMarkup(string html)
    {
        var button = System.Text.RegularExpressions.Regex.Match(html, "<button[^>]*data-activate-overlay=\"true\"[^>]*>").Value;
        Assert.NotEmpty(button);
        return button;
    }

    [Theory]
    [InlineData("de", "Place Mode starten", "Place Mode beenden")]
    [InlineData("en", "Start Place Mode", "Exit Place Mode")]
    public async Task HeaderStartsAndStopsTheSharedPlacementWithoutOpeningTheBrowserPreview(string language, string startLabel, string stopLabel)
    {
        await using var fixture = await Fixture.Create(language: language, seedLegacy: false, placementServices: true);
        var placement = Assert.IsType<OverlayPlacementService>(fixture.Placement);
        var acquisitions = fixture.Source.Acquisitions.Count;
        Assert.Contains(startLabel, await fixture.Html());
        await fixture.ClickAttribute("data-start-place-mode", "true");
        Assert.True(placement.Active);
        Assert.Equal(OverlayPlacementService.EditorKey(fixture.Service.SelectedOverlayId), placement.SelectedOverlayId);
        Assert.False(fixture.Service.State.Previewing);
        Assert.False(fixture.Service.Settings.Enabled);
        Assert.False(Directory.Exists(fixture.Directory));
        Assert.Equal(acquisitions, fixture.Source.Acquisitions.Count);
        Assert.DoesNotContain(fixture.Js.Calls, call => call.Identifier == "soulcrestOverlayDialogs.showDialog"
            && call.Arguments.Contains("kit-overlay-preview"));
        var active = await fixture.Html();
        Assert.Contains(stopLabel, active);
        Assert.Contains(language == "de" ? "Place Mode aktiv" : "Place Mode active", active);
        Assert.DoesNotContain(language == "de" ? "Editor-Vorschau geöffnet." : "Editor preview open.", active);
        await fixture.ClickAttribute("data-start-place-mode", "true");
        Assert.False(placement.Active);
        Assert.Contains(startLabel, await fixture.Html());
    }

    [Fact]
    public async Task HeaderLaunchWaitsForQueuedSavesAndSelectsItsCommittedKitWindowInTheCombinedRun()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false, placementServices: true);
        Assert.True((await fixture.Service.CreateOverlayAsync("Second")).Succeeded);
        await fixture.Refresh();
        var selected = fixture.Service.SelectedOverlayId;
        var placement = Assert.IsType<OverlayPlacementService>(fixture.Placement);
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task change, start;
        try
        {
            change = await fixture.BeginInvoke("Change", (Func<OverlaySettings, OverlaySettings>)(settings => settings with { Width = 512 }));
            start = await fixture.BeginInvoke("TogglePlaceMode");
            Assert.False(change.IsCompleted);
            Assert.False(start.IsCompleted);
            Assert.False(placement.Active);
        }
        finally { gate.Release(); }
        await Task.WhenAll(change, start).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(placement.Active);
        Assert.Equal(OverlayPlacementService.EditorKey(selected), placement.SelectedOverlayId);
        Assert.Equal(512, placement.Items.Single(item => item.Editor?.Id == selected).Editor!.Settings.Width);
        Assert.Equal(fixture.Service.Overlays.Count, placement.Items.Count(item => item.IsEditor));
        Assert.Equal(new[] { "achievements", "alerts", "boss", "event-timeline", "leveling", "loot", "notifications" },
            placement.Items.Where(item => item.Native is not null && item.CanPlace).Select(item => item.Id).Order().ToArray());
        Assert.DoesNotContain(placement.Items, item => item.Native?.Id is "farm-loot" or "abyss-rank");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AQueuedHeaderLaunchCannotTargetAnExternallyChangedOrDeletedSelection(bool delete)
    {
        await using var fixture = await Fixture.Create(seedLegacy: false, placementServices: true);
        Assert.True((await fixture.Service.CreateOverlayAsync("Second")).Succeeded);
        await fixture.Refresh();
        var selected = fixture.Service.SelectedOverlayId;
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task start;
        try
        {
            start = await fixture.BeginInvoke("TogglePlaceMode");
            Assert.False(start.IsCompleted);
            Assert.True((delete ? await fixture.Service.DeleteOverlayAsync(selected)
                : await fixture.Service.SelectOverlayAsync("default")).Succeeded);
            await fixture.Refresh();
        }
        finally { gate.Release(); }
        await start.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(fixture.Placement!.Active);
        Assert.False(fixture.Service.GetState("default").Previewing);
    }

    [Fact]
    public async Task FailedQueuedSavePreventsPlacementAndPreservesTheDamagedFile()
    {
        await using var fixture = await Fixture.Create(placementServices: true);
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task change, start;
        var file = Path.Combine(fixture.Directory, "overlay.json");
        const string broken = "{ broken external edit";
        try
        {
            change = await fixture.BeginInvoke("Change", (Func<OverlaySettings, OverlaySettings>)(settings => settings with { Width = 512 }));
            start = await fixture.BeginInvoke("TogglePlaceMode");
            await File.WriteAllTextAsync(file, broken);
        }
        finally { gate.Release(); }
        await Task.WhenAll(change, start).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(fixture.Service.LoadError);
        Assert.False(fixture.Placement!.Active);
        Assert.Equal(broken, await File.ReadAllTextAsync(file));
        Assert.False(fixture.Service.State.Previewing);
    }

    [Fact]
    public async Task PlacementErrorsAndExternalStopRefreshTheHeaderFromTheSharedService()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false, placementServices: true);
        fixture.Monitors = [];
        await fixture.ClickAttribute("data-start-place-mode", "true");
        Assert.False(fixture.Placement!.Active);
        Assert.Contains("Der gewählte Monitor ist nicht mehr verfügbar.", await fixture.Html());
        fixture.Monitors = [new("fixture", "Fixture", new(0, 0, 1280, 720), new(0, 0, 1280, 680))];
        await fixture.ClickAttribute("data-start-place-mode", "true");
        Assert.True(fixture.Placement.Active);
        fixture.Placement.Stop();
        await fixture.Refresh();
        Assert.Contains("Place Mode starten", await fixture.Html());
        Assert.DoesNotContain("Place Mode beenden", await fixture.Html());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementStartDoesNotChangeTheEditorsPresentationModeOrAcquireAnotherMarketConsumer(bool examples)
    {
        await using var fixture = await Fixture.Create(seedLegacy: false, placementServices: true,
            initialSettings: new OverlaySettings { Widgets = [OverlayCatalog.CreateWidget("market-kina")] });
        if (examples) await fixture.Invoke("ChangePreviewMode", new ChangeEventArgs { Value = true });
        var acquisitions = fixture.Source.Acquisitions.ToArray();
        var activeMarkets = fixture.Source.ActiveMarketLeases;
        await fixture.ClickAttribute("data-start-place-mode", "true");
        Assert.True(fixture.Placement!.Active);
        Assert.Equal(acquisitions, fixture.Source.Acquisitions);
        Assert.Equal(activeMarkets, fixture.Source.ActiveMarketLeases);
        Assert.Equal(examples ? 0 : 1, activeMarkets);
    }

    [Fact]
    public async Task StoreFailureEndsSharedPlacementAndDisablesRestartUntilTheLayoutIsRepaired()
    {
        await using var fixture = await Fixture.Create(placementServices: true);
        await fixture.ClickAttribute("data-start-place-mode", "true");
        Assert.True(fixture.Placement!.Active);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "overlay.json"), "{ damaged during placement");
        Assert.False((await fixture.Service.SaveAsync(fixture.Service.Settings with { Width = 500 })).Succeeded);
        Assert.NotNull(fixture.Service.LoadError);
        Assert.False(fixture.Placement.Active);
        await fixture.Refresh();
        var html = await fixture.Html();
        var button = System.Text.RegularExpressions.Regex.Match(html, "<button[^>]*data-start-place-mode=\"true\"[^>]*>");
        Assert.True(button.Success);
        Assert.Contains("disabled", button.Value);
        await fixture.ClickAttribute("data-start-place-mode", "true");
        Assert.False(fixture.Placement.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedLayoutSavePreservesNewerPlacementScaleUnlessThatScaleWasExplicitlyEdited(bool editScale)
    {
        await using var fixture = await Fixture.Create(seedLegacy: false, initialSettings: new OverlaySettings { PositionMonitorId = "old" });
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task pending;
        try
        {
            pending = await fixture.BeginInvoke("Change", (Func<OverlaySettings, OverlaySettings>)(settings => settings with
            {
                Width = 500, Scale = editScale ? 1.25 : settings.Scale,
            }));
            Assert.False(pending.IsCompleted);
            Assert.True((await fixture.Service.SavePlacementAsync(fixture.Service.SelectedOverlayId, "new-monitor", .7, .8, 1.6)).Succeeded);
            await fixture.Refresh();
        }
        finally { gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = fixture.Service.Settings;
        Assert.Equal(500, saved.Width);
        Assert.Equal(.7, saved.PositionX);
        Assert.Equal(.8, saved.PositionY);
        Assert.Equal("new-monitor", saved.PositionMonitorId);
        Assert.Equal(editScale ? 1.25 : 1.6, saved.Scale);
        using var reopened = new OverlayService(fixture.Directory);
        Assert.Null(reopened.LoadError);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(saved), System.Text.Json.JsonSerializer.Serialize(reopened.Settings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AQueuedSaveCannotMixTheEditedWindowWithAnExternallySelectedWindowsDraft(bool delete)
    {
        await using var fixture = await Fixture.Create(seedLegacy: false);
        var first = fixture.Service.SelectedOverlayId;
        Assert.True((await fixture.Service.SaveAsync(first, fixture.Service.Settings with
        {
            Widgets = [OverlayCatalog.CreateWidget("duration")],
        })).Succeeded);
        Assert.True((await fixture.Service.CreateOverlayAsync("Other")).Succeeded);
        var other = fixture.Service.SelectedOverlayId;
        Assert.True((await fixture.Service.SaveAsync(other, fixture.Service.Settings with { Width = 720 })).Succeeded);
        Assert.True((await fixture.Service.SelectOverlayAsync(first)).Succeeded);
        await fixture.Refresh();
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task pending;
        try
        {
            pending = await fixture.BeginInvoke("Change", (Func<OverlaySettings, OverlaySettings>)(settings => settings with { Width = 500 }));
            Assert.True((delete ? await fixture.Service.DeleteOverlayAsync(first)
                : await fixture.Service.SelectOverlayAsync(other)).Succeeded);
            await fixture.Refresh();
        }
        finally { gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(other, await fixture.EditorField<string>("_editingOverlayId"));
        var draft = await fixture.EditorField<OverlaySettings>("_settings");
        Assert.Equal(720, draft.Width);
        Assert.Empty(draft.Widgets);
        if (!delete)
        {
            var committed = fixture.Service.Overlays.Single(window => window.Id == first).Settings;
            Assert.Equal(500, committed.Width);
            Assert.Equal("duration", Assert.Single(committed.Widgets).Kind);
        }
    }

    [Theory]
    [InlineData("game")]
    [InlineData("session")]
    [InlineData("always")]
    public async Task HeaderAndDisplayPersistOnlyTheSelectedWindowsActivationAndVisibility(string visibility)
    {
        await using var fixture = await Fixture.Create(seedLegacy: false, placementServices: true, section: "display");
        var first = fixture.Service.SelectedOverlayId;
        Assert.True((await fixture.Service.CreateOverlayAsync("Live window")).Succeeded);
        await fixture.Refresh();
        var selected = fixture.Service.SelectedOverlayId;
        await fixture.ClickAttribute("data-activate-overlay", "true");
        await fixture.ChangeAttribute("data-kit-overlay-visibility", "true", visibility);
        Assert.True(fixture.Service.Settings.Enabled);
        Assert.Equal(visibility, fixture.Service.Settings.Visibility);
        Assert.False(fixture.Service.Overlays.Single(window => window.Id == first).Settings.Enabled);
        Assert.Equal("game", fixture.Service.Overlays.Single(window => window.Id == first).Settings.Visibility);
        await fixture.ClickAttribute("data-start-place-mode", "true");
        Assert.True(fixture.Placement!.Active);
        Assert.Equal(OverlayPlacementService.EditorKey(selected), fixture.Placement.SelectedOverlayId);
        await fixture.ClickAttribute("data-start-place-mode", "true");
        Assert.True(fixture.Service.Settings.Enabled);
        Assert.Equal(visibility, fixture.Service.Settings.Visibility);
        using var reopened = new OverlayService(fixture.Directory);
        Assert.True(reopened.Overlays.Single(window => window.Id == selected).Settings.Enabled);
        Assert.Equal(visibility, reopened.Overlays.Single(window => window.Id == selected).Settings.Visibility);
    }

    [Theory]
    [InlineData("de", "In Aufnahmen anzeigen")]
    [InlineData("en", "Show in recordings")]
    public async Task RecordingTogglePersistsOnlyItsSelectedWindowWithoutChangingGlobalSettingsOrSources(string language, string label)
    {
        using var preferences = new ThemePreferenceScope();
        await using var fixture = await Fixture.Create(language: language, settings: preferences.Settings, seedLegacy: false,
            section: "display", initialSettings: new() { Enabled = true, Widgets = [OverlayCatalog.CreateWidget("clock")] });
        var first = fixture.Service.Overlays.Single();
        var untouched = System.Text.Json.JsonSerializer.Serialize(first);
        Assert.True((await fixture.Service.CreateOverlayAsync("Recording choice")).Succeeded);
        Assert.True((await fixture.Service.SaveAsync(fixture.Service.Settings with
        {
            Enabled = true, Visibility = "session", Interaction = "locked", CaptureExcluded = true,
            TriggerRules = new() { WorldMap = OverlayTriggerRequirement.Excluded },
            Widgets = [OverlayCatalog.CreateWidget("market-kina")],
        })).Succeeded);
        await fixture.Refresh();
        var selected = fixture.Service.SelectedOverlayId;
        var original = fixture.Service.Settings;
        var global = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        var acquisitions = fixture.Source.Acquisitions.ToArray();
        var leases = (fixture.Source.ActiveLeases, fixture.Source.ActiveMarketLeases);
        var globalChanges = 0;
        preferences.Settings.Changed += () => globalChanges++;
        Assert.Contains(label, await fixture.Html());
        Assert.DoesNotContain("checked", RecordingToggleMarkup(await fixture.Html()));

        await fixture.ChangeAttribute("data-kit-overlay-recordings", "true", true);

        Assert.False(fixture.Service.Settings.CaptureExcluded);
        Assert.Contains("checked", RecordingToggleMarkup(await fixture.Html()));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original with { CaptureExcluded = false }),
            System.Text.Json.JsonSerializer.Serialize(fixture.Service.Settings));
        using (var restarted = new OverlayService(fixture.Directory, new OverlayCollectionSettings()))
        {
            Assert.Null(restarted.LoadError);
            Assert.False(restarted.Overlays.Single(window => window.Id == selected).Settings.CaptureExcluded);
            Assert.Equal(untouched, System.Text.Json.JsonSerializer.Serialize(restarted.Overlays.Single(window => window.Id == first.Id)));
        }
        await fixture.ChangeAttribute("data-kit-overlay-recordings", "true", false);
        Assert.True(fixture.Service.Settings.CaptureExcluded);
        Assert.DoesNotContain("checked", RecordingToggleMarkup(await fixture.Html()));
        Assert.Equal(selected, fixture.Service.SelectedOverlayId);
        Assert.Equal(untouched, System.Text.Json.JsonSerializer.Serialize(fixture.Service.Overlays.Single(window => window.Id == first.Id)));
        Assert.Equal(global, await File.ReadAllBytesAsync(AppPaths.SettingsFile));
        Assert.Equal(0, globalChanges);
        Assert.Equal(acquisitions, fixture.Source.Acquisitions);
        Assert.Equal(leases, (fixture.Source.ActiveLeases, fixture.Source.ActiveMarketLeases));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingATemplateKeepsTheWindowsRecordingChoice(bool captureExcluded)
    {
        await using var fixture = await Fixture.Create(seedLegacy: false,
            initialSettings: new() { CaptureExcluded = captureExcluded, Enabled = true, Widgets = [OverlayCatalog.CreateWidget("duration")] });
        Assert.True((await fixture.Service.SaveTemplateAsync("Layout only", new()
        {
            Width = 512, CaptureExcluded = !captureExcluded, Widgets = [OverlayCatalog.CreateWidget("clock")],
        })).Succeeded);
        await fixture.SetField("_selectedTemplateId", fixture.Service.Templates.Single().Id);
        await fixture.Invoke("ApplySelectedTemplate");
        Assert.Equal(captureExcluded, fixture.Service.Settings.CaptureExcluded);
        await fixture.Invoke("ConfirmLayoutChange");
        Assert.Equal(512, fixture.Service.Settings.Width);
        Assert.Equal("clock", Assert.Single(fixture.Service.Settings.Widgets).Kind);
        Assert.Equal(captureExcluded, fixture.Service.Settings.CaptureExcluded);
        Assert.True(fixture.Service.Settings.Enabled);
        using var restarted = new OverlayService(fixture.Directory, new OverlayCollectionSettings());
        Assert.Equal(captureExcluded, restarted.Settings.CaptureExcluded);
    }

    [Fact]
    public async Task QueuedEditorGeometryPreservesANewerRecordingChoice()
    {
        var widget = OverlayCatalog.CreateWidget("clock");
        await using var fixture = await Fixture.Create(seedLegacy: false,
            initialSettings: new() { CaptureExcluded = true, Widgets = [widget] });
        var acquisitions = fixture.Source.Acquisitions.ToArray();
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task geometry;
        try
        {
            geometry = await fixture.BeginInvoke("CommitWidgetGeometry", widget.Id, 72d, 48d, widget.Width, widget.Height);
            Assert.False(geometry.IsCompleted);
            Assert.True((await fixture.Service.SaveAsync(fixture.Service.Settings with { CaptureExcluded = false })).Succeeded);
        }
        finally { gate.Release(); }
        await geometry.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(fixture.Service.Settings.CaptureExcluded);
        var saved = Assert.Single(fixture.Service.Settings.Widgets);
        Assert.Equal((72d, 48d), (saved.X, saved.Y));
        Assert.Equal(acquisitions, fixture.Source.Acquisitions);
        using var restarted = new OverlayService(fixture.Directory, new OverlayCollectionSettings());
        Assert.False(restarted.Settings.CaptureExcluded);
        Assert.Equal((72d, 48d), (restarted.Settings.Widgets.Single().X, restarted.Settings.Widgets.Single().Y));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingToggleCannotOverwriteACorruptLayoutOrAnAtomicSaveFailure(bool corruptOnLoad)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SoulcrestKitEditorTests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "overlay.json");
        if (corruptOnLoad)
        {
            System.IO.Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(file, "{ broken recording choice");
        }
        await using var fixture = await Fixture.Create(directory, seedLegacy: false, section: "display",
            initialSettings: corruptOnLoad ? null : new() { CaptureExcluded = true });
        var before = await File.ReadAllBytesAsync(file);
        var acquisitions = fixture.Source.Acquisitions.ToArray();
        using var replacementBlocker = corruptOnLoad ? null : new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

        await fixture.ChangeAttribute("data-kit-overlay-recordings", "true", true);

        Assert.NotNull(fixture.Service.LoadError);
        Assert.True(fixture.Service.Settings.CaptureExcluded);
        Assert.Contains("role=\"alert\"", await fixture.Html());
        Assert.Contains("class=\"autosave-fields\" disabled", await fixture.Html());
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        await fixture.ChangeAttribute("data-kit-overlay-recordings", "true", false);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Equal(acquisitions, fixture.Source.Acquisitions);
    }

    private static string RecordingToggleMarkup(string html)
    {
        var toggle = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*data-kit-overlay-recordings=\"true\"[^>]*>");
        Assert.True(toggle.Success);
        return toggle.Value;
    }

    [Fact]
    public async Task ThemeSelectorOffersAllSixThemesAndPersistsOnlyTheGlobalPreference()
    {
        using var preferences = new ThemePreferenceScope();
        var widget = OverlayCatalog.CreateWidget("market-kina");
        await using var fixture = await Fixture.Create(settings: preferences.Settings, seedLegacy: false, section: "display",
            initialSettings: new() { Enabled = true, Widgets = [widget] });
        var selector = ThemeSelectorMarkup(await fixture.Html());
        var options = System.Text.RegularExpressions.Regex.Matches(selector, "<option[^>]*value=\"(?<id>[^\"]+)\"")
            .Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups["id"].Value).ToArray();
        Assert.Equal(6, options.Length);
        Assert.Equal(OverlayEditorThemes.All.Select(theme => theme.Id), options);
        foreach (var theme in OverlayEditorThemes.All) Assert.Contains(UiText.T(theme.Label), selector);
        Assert.Equal("obsidian", await fixture.ElementAttribute("data-kit-overlay-theme", "true", "value"));
        var layoutFile = Path.Combine(fixture.Directory, "overlay.json");
        var originalLayout = await File.ReadAllBytesAsync(layoutFile);
        var originalDraft = System.Text.Json.JsonSerializer.Serialize(await fixture.EditorField<OverlaySettings>("_settings"));
        var selected = fixture.Service.SelectedOverlayId;
        var acquisitions = fixture.Source.Acquisitions.ToArray();
        var activeLeases = (fixture.Source.ActiveLeases, fixture.Source.ActiveMarketLeases);
        var changes = 0;
        preferences.Settings.Changed += () => changes++;

        await fixture.ChangeAttribute("data-kit-overlay-theme", "true", "aurora");

        Assert.Equal("aurora", preferences.Settings.Current.OverlayTheme);
        Assert.Equal("aurora", new SettingsService().Current.OverlayTheme);
        Assert.Equal("aurora", await fixture.ElementAttribute("data-kit-overlay-theme", "true", "value"));
        Assert.Equal(1, changes);
        Assert.Equal(originalLayout, await File.ReadAllBytesAsync(layoutFile));
        Assert.Equal(originalDraft, System.Text.Json.JsonSerializer.Serialize(await fixture.EditorField<OverlaySettings>("_settings")));
        Assert.Equal(selected, fixture.Service.SelectedOverlayId);
        Assert.True(fixture.Service.Settings.Enabled);
        Assert.Contains("aria-pressed=\"true\"", ActivationMarkup(await fixture.Html()));
        Assert.Equal(acquisitions, fixture.Source.Acquisitions);
        Assert.Equal(activeLeases, (fixture.Source.ActiveLeases, fixture.Source.ActiveMarketLeases));
        Assert.Null(await fixture.EditorField<string?>("_themeError"));
        Assert.False(await fixture.EditorField<bool>("_savingTheme"));
    }

    [Fact]
    public async Task ThemeSelectorSaveFailureRollsBackItsSelectionAndKeepsThePendingLayoutDraftAndError()
    {
        using var preferences = new ThemePreferenceScope();
        var widget = OverlayCatalog.CreateWidget("market-kina");
        await using var fixture = await Fixture.Create(settings: preferences.Settings, seedLegacy: false, section: "display",
            initialSettings: new() { Enabled = true, Widgets = [widget] });
        var savedPreferences = await File.ReadAllBytesAsync(AppPaths.SettingsFile);
        var layoutFile = Path.Combine(fixture.Directory, "overlay.json");
        var savedLayout = await File.ReadAllBytesAsync(layoutFile);
        var acquisitions = fixture.Source.Acquisitions.ToArray();
        var activeLeases = (fixture.Source.ActiveLeases, fixture.Source.ActiveMarketLeases);
        var changes = 0;
        preferences.Settings.Changed += () => changes++;
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task? geometry = null;
        try
        {
            geometry = await fixture.BeginInvoke("CommitWidgetGeometry", widget.Id, 64d, 48d, widget.Width, widget.Height);
            await fixture.Invoke("CanvasDimension", new ChangeEventArgs { Value = "not-a-width" }, true);
            var draft = System.Text.Json.JsonSerializer.Serialize(await fixture.EditorField<OverlaySettings>("_settings"));
            var layoutError = await fixture.EditorField<string?>("_error");
            Assert.NotNull(layoutError);
            using var replacementBlocker = new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);

            await fixture.ChangeAttribute("data-kit-overlay-theme", "true", "aurora");

            Assert.Equal("obsidian", preferences.Settings.Current.OverlayTheme);
            Assert.Equal("obsidian", await fixture.ElementAttribute("data-kit-overlay-theme", "true", "value"));
            Assert.Equal(savedPreferences, await File.ReadAllBytesAsync(AppPaths.SettingsFile));
            Assert.Equal(savedLayout, await File.ReadAllBytesAsync(layoutFile));
            Assert.Equal(draft, System.Text.Json.JsonSerializer.Serialize(await fixture.EditorField<OverlaySettings>("_settings")));
            Assert.Equal(layoutError, await fixture.EditorField<string?>("_error"));
            var html = await fixture.Html();
            Assert.Contains(layoutError, html);
            Assert.Contains("Overlay-Einstellungen konnten nicht gespeichert werden.", html);
            Assert.Contains("role=\"alert\"", html);
            Assert.False(await fixture.EditorField<bool>("_savingTheme"));
            Assert.False(geometry.IsCompleted);
            Assert.True(fixture.Service.Settings.Enabled);
            Assert.Equal(acquisitions, fixture.Source.Acquisitions);
            Assert.Equal(activeLeases, (fixture.Source.ActiveLeases, fixture.Source.ActiveMarketLeases));
            Assert.Equal(0, changes);
        }
        finally
        {
            gate.Release();
            if (geometry is not null) await geometry.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static string ThemeSelectorMarkup(string html)
    {
        var selector = System.Text.RegularExpressions.Regex.Match(html,
            "<select[^>]*data-kit-overlay-theme=\"true\"[^>]*>.*?</select>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(selector.Success);
        return selector.Value;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedLayoutEditRetainsNewerVisibilityUnlessItsOwnVisibilityWasChanged(bool editVisibility)
    {
        await using var fixture = await Fixture.Create(seedLegacy: false);
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task pending;
        try
        {
            pending = await fixture.BeginInvoke("Change", (Func<OverlaySettings, OverlaySettings>)(settings => settings with
            {
                Width = 500, Visibility = editVisibility ? "always" : settings.Visibility,
            }));
            Assert.True((await fixture.Service.SaveAsync(fixture.Service.Settings with { Visibility = "session" })).Succeeded);
            await fixture.Refresh();
        }
        finally { gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(500, fixture.Service.Settings.Width);
        Assert.Equal(editVisibility ? "always" : "session", fixture.Service.Settings.Visibility);
    }

    [Fact]
    public async Task HostErrorsAreShownOnlyForTheEditingWindowAndDisappearWhenItRecovers()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false);
        var first = fixture.Service.SelectedOverlayId;
        Assert.True((await fixture.Service.CreateOverlayAsync("Current")).Succeeded);
        await fixture.Refresh();
        var current = fixture.Service.SelectedOverlayId;
        fixture.Service.SetRuntimeState(first, false, "Other window host failure");
        await fixture.Refresh();
        Assert.DoesNotContain("Other window host failure", await fixture.Html());
        fixture.Service.SetRuntimeState(current, false, "Current window host failure");
        await fixture.Refresh();
        Assert.Contains("Current window host failure", await fixture.Html());
        fixture.Service.SetRuntimeState(current, true);
        await fixture.Refresh();
        var recovered = await fixture.Html();
        Assert.DoesNotContain("Current window host failure", recovered);
        Assert.Contains("status-dot running", recovered);
    }

    [Theory]
    [InlineData("presets")]
    [InlineData("windows")]
    [InlineData("templates")]
    public async Task PresetsUnifiesTheWindowListAndTemplatesWithoutDuplicateLegacyTabs(string section)
    {
        await using var fixture = await Fixture.Create(section: section, seedLegacy: false);
        Assert.True((await fixture.Service.CreateOverlayAsync("Other window")).Succeeded);
        await fixture.Refresh();
        var html = await fixture.Html();
        Assert.Contains("section=presets", html);
        Assert.Contains("data-overlay-section=\"presets\"", html);
        Assert.DoesNotContain("section=windows", html);
        Assert.DoesNotContain("section=templates", html);
        Assert.DoesNotContain("data-overlay-section=\"windows\"", html);
        Assert.DoesNotContain("data-overlay-section=\"templates\"", html);
        Assert.Contains("id=\"oe-templates-title\"", html);
        Assert.Contains("Als Vorlage speichern", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "data-preset-window=\"").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "data-example-only=\"true\"").Count);
        Assert.Contains("Leeres Overlay", html);
        Assert.Equal(0, fixture.Source.ActiveLeases);
    }

    [Theory]
    [InlineData("de", "1 Modul")]
    [InlineData("en", "1 module")]
    public async Task PresetModuleCountIsLocalizedForOneModule(string language, string expected)
    {
        await using var fixture = await Fixture.Create(section: "presets", language: language, seedLegacy: false,
            initialSettings: new() { Widgets = [OverlayCatalog.CreateWidget("duration")] });
        Assert.Contains(expected, System.Net.WebUtility.HtmlDecode(await fixture.Html()));
    }

    [Fact]
    public async Task EveryPresetThumbnailUsesTheProductionWidgetDomAndOnlyExamplesWithoutAcquiringLiveData()
    {
        await using var fixture = await Fixture.Create(section: "presets", seedLegacy: false, initialSettings: new()
        {
            Width = 420, Height = 180,
            Widgets = [OverlayCatalog.CreateWidget("duration"), OverlayCatalog.CreateWidget("market-kina", 180, 0)],
        });
        fixture.Source.DistinctModes = true;
        var before = await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "overlay.json"));
        fixture.Source.Publish();
        await fixture.Refresh();
        var html = await fixture.Html();
        Assert.Contains("data-preset-widget-kind=\"duration\"", html);
        Assert.Contains("data-preset-widget-kind=\"market-kina\"", html);
        Assert.Contains("preserveAspectRatio=\"xMidYMid meet\"", html);
        Assert.Contains("viewBox=\"0 0 420 180\"", html);
        Assert.Contains("EXAMPLE-ONLY", html);
        Assert.DoesNotContain("LIVE-ONLY", html);
        Assert.Equal(0, fixture.Source.ActiveLeases);
        Assert.Equal(0, fixture.Source.ActiveMarketLeases);
        Assert.Empty(fixture.Source.Acquisitions);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "overlay.json")));
        var card = System.Text.RegularExpressions.Regex.Match(html, "<article[^>]*data-preset-window=.*?</article>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        Assert.DoesNotContain("class=\"oe-stage", card);
        Assert.DoesNotContain("class=\"oe-widget", card);
        Assert.DoesNotContain("data-widget-id", card);
        Assert.DoesNotContain("<img class=\"oe-preset", card);
    }

    [Fact]
    public async Task PresetCardsShowStatusAndOnlyTheHeaderActivatesTheSelectedWindow()
    {
        await using var fixture = await Fixture.Create(section: "presets", seedLegacy: false);
        var first = fixture.Service.SelectedOverlayId;
        Assert.True((await fixture.Service.SavePositionAsync(first, .25, .4)).Succeeded);
        Assert.True((await fixture.Service.CreateOverlayAsync("Second window")).Succeeded);
        var second = fixture.Service.SelectedOverlayId;
        await fixture.Refresh();
        await fixture.ClickAttribute("data-select-preset-window", first);
        Assert.Equal(first, fixture.Service.SelectedOverlayId);
        Assert.Equal("presets", await fixture.EditorField<string?>("<Section>k__BackingField"));
        Assert.DoesNotContain("data-enable-preset-window", await fixture.Html());
        await fixture.ClickAttribute("data-activate-overlay", "true");
        Assert.Equal(first, fixture.Service.SelectedOverlayId);
        Assert.True(fixture.Service.Overlays.Single(window => window.Id == first).Settings.Enabled);
        Assert.False(fixture.Service.Overlays.Single(window => window.Id == second).Settings.Enabled);
        var html = await fixture.Html();
        var firstStatus = System.Text.RegularExpressions.Regex.Match(html,
            "<div[^>]*data-preset-window-state=\"" + first + "\"[^>]*>.*?</div>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        var secondStatus = System.Text.RegularExpressions.Regex.Match(html,
            "<div[^>]*data-preset-window-state=\"" + second + "\"[^>]*>.*?</div>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        Assert.Contains("Aktiv", firstStatus);
        Assert.Contains("Inaktiv", secondStatus);
        Assert.DoesNotContain("<input", firstStatus + secondStatus);
        Assert.Equal(.25, fixture.Service.Overlays.Single(window => window.Id == first).Settings.PositionX);
        Assert.Equal(.4, fixture.Service.Overlays.Single(window => window.Id == first).Settings.PositionY);
        await fixture.ClickAttribute("data-edit-preset-window", second);
        Assert.Equal(second, fixture.Service.SelectedOverlayId);
        Assert.Equal("layout", await fixture.EditorField<string?>("<Section>k__BackingField"));
        Assert.Equal(1, fixture.Source.ActiveLeases);
    }

    [Fact]
    public async Task PresetWindowManagementRetainsCreateRenameDuplicateAndDelete()
    {
        await using var fixture = await Fixture.Create(section: "presets", seedLegacy: false);
        await fixture.ClickAttribute("data-create-preset-window", "true");
        Assert.Equal(2, fixture.Service.Overlays.Count);
        await fixture.Invoke("RenameOverlay", new ChangeEventArgs { Value = "Reusable window" });
        Assert.Equal("Reusable window", fixture.Service.Overlays.Single(window => window.Id == fixture.Service.SelectedOverlayId).Name);
        await fixture.Invoke("DuplicateOverlay");
        Assert.Equal(3, fixture.Service.Overlays.Count);
        Assert.Contains("Reusable window", fixture.Service.Overlays.Single(window => window.Id == fixture.Service.SelectedOverlayId).Name);
        await fixture.Invoke("RequestOverlayDelete");
        await fixture.Invoke("DeleteOverlay");
        Assert.Equal(2, fixture.Service.Overlays.Count);
        Assert.Equal(fixture.Service.SelectedOverlayId, await fixture.EditorField<string>("_editingOverlayId"));
    }

    [Theory]
    [InlineData("de", "Deine Fenster und wiederverwendbaren Layouts an einem Ort.", "Gebiete und zusätzliche Bedingungen")]
    [InlineData("en", "Your windows and reusable layouts in one place.", "Areas and additional conditions")]
    public async Task PresetsAndCompactDisplayUseTheUiLanguageAndRetainTheVisibilityControls(string language, string description, string conditions)
    {
        await using var fixture = await Fixture.Create(language: language, section: "presets", seedLegacy: false);
        Assert.Contains(description, await fixture.Html());
        Assert.False(Directory.Exists(fixture.Directory));
        await fixture.SetSection("display");
        var html = await fixture.Html();
        Assert.Contains("class=\"oe-display-grid\"", html);
        Assert.Contains("id=\"oe-visibility-title\"", html);
        Assert.Contains("id=\"oe-appearance-title\"", html);
        Assert.Contains(conditions, html);
        var advanced = System.Text.RegularExpressions.Regex.Match(html, "<details[^>]*data-visibility-advanced=\"true\"[^>]*>").Value;
        Assert.NotEmpty(advanced);
        Assert.DoesNotContain("open", advanced);
        Assert.DoesNotContain("data-kit-overlay-enabled", html);
        Assert.Contains("data-activate-overlay=\"true\"", html);
        Assert.Contains("data-kit-overlay-visibility=\"true\"", html);
        Assert.Contains("overlay-window-triggers", html);
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public async Task EnglishLibraryUsesThePublicMarketKinaLabel()
    {
        await using var fixture = await Fixture.Create(language: "en", seedLegacy: false);
        var html = await fixture.Html();
        Assert.Contains("<strong>Market Kina (net)</strong>", html);
        Assert.DoesNotContain("<strong>Market-Kina (netto)</strong>", html);
    }

    [Fact]
    public async Task EmptyDefaultsAndLibraryDoNotCreateAStoredLayout()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false);
        Assert.Empty(fixture.Service.Settings.Widgets);
        Assert.Equal(22, OverlayCatalog.Widgets.Count);
        Assert.False(Directory.Exists(fixture.Directory));
        await fixture.Invoke("ShowModulePreview", "drop-grid");
        Assert.Contains("oe-module-preview", await fixture.Html());
        Assert.False(fixture.Source.Acquisitions.Last().Market);
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public async Task CanvasModeAndPlacedMarketModulesControlOnlyTheReadOnlyPresentationLease()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false);
        fixture.Source.DistinctModes = true;
        await fixture.Invoke("AddModuleAt", "duration", 8d, 8d);
        Assert.Contains("LIVE-ONLY", await fixture.Html());
        await fixture.Invoke("ShowModulePreview", "duration");
        var liveWithLibrary = await fixture.Html();
        Assert.Contains("LIVE-ONLY", liveWithLibrary);
        Assert.Contains("EXAMPLE-ONLY", liveWithLibrary);
        Assert.False(fixture.Source.Acquisitions.Last().Market);
        await fixture.Invoke("AddModuleAt", "market-kina", 184d, 8d);
        Assert.Equal((false, true), fixture.Source.Acquisitions.Last());
        await fixture.Invoke("ChangePreviewMode", new ChangeEventArgs { Value = true });
        Assert.Equal((true, false), fixture.Source.Acquisitions.Last());
        Assert.DoesNotContain("LIVE-ONLY", await fixture.Html());
        await fixture.Invoke("ChangePreviewMode", new ChangeEventArgs { Value = false });
        Assert.Equal((false, true), fixture.Source.Acquisitions.Last());
        await fixture.Invoke("CreateOverlay");
        Assert.Empty(fixture.Service.Settings.Widgets);
        Assert.Equal((false, false), fixture.Source.Acquisitions.Last());
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.DoesNotContain(fixture.Source.Acquisitions, lease => lease.Examples && lease.Market);
    }

    [Fact]
    public async Task StaticEntryNeverAcquiresTheHiddenMarketLayoutAndSectionChangesSuspendThenResumeItsLease()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false, section: "static", staticServices: true,
            initialSettings: new OverlaySettings { Widgets = [OverlayCatalog.CreateWidget("market-kina")] });
        Assert.Empty(fixture.Source.Acquisitions);
        Assert.Equal(0, fixture.Source.ActiveLeases);
        Assert.Equal(0, fixture.Source.ActiveMarketLeases);
        Assert.Contains("data-static-overlays", await fixture.Html());
        await fixture.SetSection("layout");
        Assert.Equal(1, fixture.Source.ActiveMarketLeases);
        Assert.Equal((false, true), fixture.Source.Acquisitions.Last());
        await fixture.Invoke("SelectSection", "static");
        Assert.Equal(0, fixture.Source.ActiveLeases);
        Assert.Equal(0, fixture.Source.ActiveMarketLeases);
        var acquisitions = fixture.Source.Acquisitions.Count;
        fixture.Source.Publish();
        await fixture.Refresh();
        await fixture.Invoke("ShowModulePreview", "market-kina");
        await fixture.Service.SaveAsync(fixture.Service.Settings with { BackgroundOpacity = .6 });
        await fixture.Refresh();
        Assert.Equal(acquisitions, fixture.Source.Acquisitions.Count);
        Assert.Equal(0, fixture.Source.ActiveMarketLeases);
        await fixture.SetSection("layout");
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.Equal(1, fixture.Source.ActiveMarketLeases);
        await fixture.SetSection("static");
        Assert.Equal(0, fixture.Source.ActiveLeases);
        await fixture.Invoke("ChangePreviewMode", new ChangeEventArgs { Value = true });
        Assert.Equal(0, fixture.Source.ActiveLeases);
        await fixture.Invoke("SelectSection", "layout");
        Assert.Equal(1, fixture.Source.ActiveLeases);
        Assert.Equal(0, fixture.Source.ActiveMarketLeases);
        Assert.Equal((true, false), fixture.Source.Acquisitions.Last());
    }

    [Fact]
    public void TriggerRuleComparisonUsesMeaningRatherThanAreaOrderOrNeutralRepresentation()
    {
        Assert.True(OverlayTriggerRules.Equivalent(null, new()));
        Assert.True(OverlayTriggerRules.Equivalent(new(), null));
        var first = new OverlayTriggerRules
        {
            Areas = [OverlayTriggerArea.OpenWorld, OverlayTriggerArea.Abyss], Foreground = OverlayTriggerRequirement.Required,
        };
        var reordered = first with { Areas = [OverlayTriggerArea.Abyss, OverlayTriggerArea.OpenWorld] };
        Assert.True(OverlayTriggerRules.Equivalent(first, reordered));
        Assert.False(OverlayTriggerRules.Equivalent(first, reordered with { EscMenu = OverlayTriggerRequirement.Excluded }));
        Assert.False(OverlayTriggerRules.Equivalent(first, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedUiLayoutSavesPreserveNewerTriggersUnlessTheUserExplicitlyChangedThem(bool editTriggers)
    {
        var originalRules = new OverlayTriggerRules
        {
            Areas = [OverlayTriggerArea.OpenWorld], Foreground = OverlayTriggerRequirement.Required,
        };
        var newerRules = new OverlayTriggerRules
        {
            Areas = [OverlayTriggerArea.Abyss], SessionActive = OverlayTriggerRequirement.Required,
        };
        var editedRules = new OverlayTriggerRules
        {
            Areas = [OverlayTriggerArea.Instances], WorldMap = OverlayTriggerRequirement.Excluded,
        };
        await using var fixture = await Fixture.Create(seedLegacy: false,
            initialSettings: new OverlaySettings { TriggerRules = originalRules });
        var gate = await fixture.SaveGate();
        await gate.WaitAsync();
        Task pending;
        try
        {
            pending = await fixture.BeginInvoke("Change", (Func<OverlaySettings, OverlaySettings>)(settings => settings with
            {
                Width = 500, TriggerRules = editTriggers ? editedRules : settings.TriggerRules,
            }));
            Assert.False(pending.IsCompleted);
            // Publish through the same service while the UI draft waits. Its change
            // callback deliberately skips reloading the draft while _saving is true.
            Assert.True((await fixture.Service.SaveAsync(fixture.Service.Settings with { TriggerRules = newerRules })).Succeeded);
            await fixture.Refresh();
            Assert.True(OverlayTriggerRules.Equivalent(newerRules, fixture.Service.Settings.TriggerRules));
        }
        finally { gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(500, fixture.Service.Settings.Width);
        var expected = editTriggers ? editedRules : newerRules;
        Assert.True(OverlayTriggerRules.Equivalent(expected, fixture.Service.Settings.TriggerRules));
        using var reopened = new OverlayService(fixture.Directory);
        Assert.Null(reopened.LoadError);
        Assert.True(OverlayTriggerRules.Equivalent(expected, reopened.Settings.TriggerRules));
    }

    [Fact]
    public async Task DropInspectorStoresExactKeysAndKeepsTheOriginalOptions()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false);
        await fixture.Invoke("AddModuleAt", "drop-grid", 8d, 8d);
        var key = fixture.Source.Capture(true, "de").ItemCatalog[0].Key;
        await fixture.Invoke("AddItem", key);
        var selected = fixture.Service.Settings.Widgets.Single();
        Assert.Equal("selected", selected.ItemFilter);
        Assert.Equal(new[] { key }, selected.ItemKeys);
        await fixture.Invoke("WidgetNumber", new ChangeEventArgs { Value = "64" }, "itemSize");
        await fixture.Invoke("WidgetNumber", new ChangeEventArgs { Value = "4" }, "itemLimit");
        var html = await fixture.Html();
        Assert.Contains("value=\"grid\"", html);
        Assert.Contains("value=\"strip\"", html);
        Assert.Contains("value=\"list\"", html);
        Assert.Contains("value=\"card\"", html);
        Assert.DoesNotContain("Trashloot", html);
        using var reloaded = new OverlayService(fixture.Directory);
        Assert.Equal(64, reloaded.Settings.Widgets.Single().ItemSize);
        Assert.Equal(4, reloaded.Settings.Widgets.Single().ItemLimit);
        Assert.Equal(new[] { key }, reloaded.Settings.Widgets.Single().ItemKeys);
    }

    [Fact]
    public async Task LargeItemPickerVirtualizesItsRowsKeepsItsCacheAndFindsTheLastItem()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false);
        fixture.Source.CatalogOverride = Enumerable.Range(1, 10_000).Select(index =>
            new OverlayLootItem("Item:" + (900_000_000 + index), $"Sample item {index:D5}", "0")).ToArray();
        var acquisitions = fixture.Source.Acquisitions.Count;
        await fixture.Invoke("ShowModulePreview", "market-kina");
        Assert.Equal(acquisitions, fixture.Source.Acquisitions.Count);
        Assert.Equal(0, fixture.Source.ActiveMarketLeases);
        await fixture.Invoke("AddModuleAt", "drop-grid", 8d, 8d);
        await fixture.Invoke("AddItem", fixture.Source.CatalogOverride[0].Key);
        var all = await fixture.VirtualizedItems();
        Assert.Equal(9_999, all.Count);
        var html = await fixture.Html();
        Assert.InRange(System.Text.RegularExpressions.Regex.Matches(html, "class=\"oe-item-result\"").Count, 0, 32);
        fixture.Source.Publish();
        await fixture.Refresh();
        Assert.Same(all, await fixture.VirtualizedItems());
        var last = fixture.Source.CatalogOverride[^1];
        await fixture.SetField("_itemSearch", last.Key);
        await fixture.Refresh();
        Assert.Equal(last.Key, Assert.Single(await fixture.VirtualizedItems()).Key);
        await fixture.Invoke("AddItem", last.Key);
        Assert.Contains(last.Key, fixture.Service.Settings.Widgets.Single().ItemKeys);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task HorizontalLibraryGroupsEveryModuleOnceAndFiltersWithoutChangingLayoutOrSelection(string language)
    {
        await using var fixture = await Fixture.Create(language: language);
        var selected = fixture.Service.Settings.Widgets.Single().Id;
        await fixture.Invoke("SelectWidget", selected);
        var file = Path.Combine(fixture.Directory, "overlay.json");
        var before = await File.ReadAllBytesAsync(file);
        var leases = fixture.Source.Acquisitions.ToArray();
        var originalHtml = await fixture.Html();
        Assert.True(originalHtml.IndexOf("class=\"panel oe-library\"", StringComparison.Ordinal)
            < originalHtml.IndexOf("class=\"oe-workbench\"", StringComparison.Ordinal));
        Assert.Equal(22, LibraryKinds(originalHtml).Distinct().Count());
        var collected = new List<string>();
        foreach (var (category, count) in new[] { ("drops", 3), ("kina", 5), ("ap", 2), ("rank", 8), ("time", 2), ("notices", 1), ("pets", 1) })
        {
            await fixture.ClickAttribute("data-library-category", category);
            var kinds = LibraryKinds(await fixture.Html());
            Assert.Equal(count, kinds.Length);
            collected.AddRange(kinds);
        }
        Assert.Equal(22, collected.Distinct().Count());
        await fixture.ClickAttribute("data-library-category", "ap");
        await fixture.Invoke("ShowModulePreview", "abyss-points");
        await fixture.Invoke("ChangeModuleSearch", new ChangeEventArgs { Value = " /H " });
        Assert.Equal("abyss-points-hour", Assert.Single(LibraryKinds(await fixture.Html())));
        Assert.Null(await fixture.EditorField<string?>("_previewModule"));
        await fixture.Invoke("ChangeModuleSearch", new ChangeEventArgs { Value = "no-such-widget" });
        Assert.Empty(LibraryKinds(await fixture.Html()));
        Assert.Contains("role=\"status\"", await fixture.Html());
        await fixture.Invoke("ChangeModuleSearch", new ChangeEventArgs { Value = "" });
        await fixture.ClickAttribute("data-library-category", "all");
        Assert.Equal(22, LibraryKinds(await fixture.Html()).Length);
        Assert.Equal(selected, await fixture.EditorField<string?>("_selectedId"));
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Equal(leases, fixture.Source.Acquisitions);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task FilteredModuleStillAddsWithTheOriginalContentReferenceAndHonorsTheLimit(string language)
    {
        await using var fixture = await Fixture.Create(language: language, seedLegacy: false);
        await fixture.ClickAttribute("data-library-category", "kina");
        await fixture.Invoke("ChangeModuleSearch", new ChangeEventArgs { Value = "market /h" });
        Assert.Equal("market-kina-hour", Assert.Single(LibraryKinds(await fixture.Html())));
        await fixture.ClickAttribute("data-module-kind", "market-kina-hour");
        var added = Assert.Single(fixture.Service.Settings.Widgets);
        var definition = OverlayCatalog.Find(added.Kind)!;
        Assert.Equal((definition.Width, definition.Height), (added.Width, added.Height));
        await fixture.Invoke("CommitWidgetGeometry", added.Id, 24d, 40d, definition.Width * 1.5, definition.Height * 1.5);
        var resized = Assert.Single(fixture.Service.Settings.Widgets);
        Assert.Equal(definition.Width, resized.ContentWidth);
        Assert.Equal(definition.Height, resized.ContentHeight);
        for (var index = 1; index < OverlayCatalog.MaximumWidgets; index++) await fixture.Invoke("AddModule", "duration");
        await fixture.ClickAttribute("data-module-kind", "market-kina-hour");
        Assert.Equal(OverlayCatalog.MaximumWidgets, fixture.Service.Settings.Widgets.Count);
        Assert.Contains("disabled", (await fixture.Html()).Split("data-module-kind=\"market-kina-hour\"")[1].Split('>')[0]);
    }

    [Fact]
    public async Task BuiltinStandardsRequireConfirmationAndPreserveWindowBehaviorWhenApplied()
    {
        await using var fixture = await Fixture.Create(section: "presets");
        Assert.True((await fixture.Service.SavePositionAsync(.7, .4)).Succeeded);
        var initial = fixture.Service.Settings with { Enabled = true, PositionX = .7, PositionY = .4,
            Visibility = "always", TriggerRules = new() { WorldMap = OverlayTriggerRequirement.Required } };
        Assert.True((await fixture.Service.SaveAsync(initial)).Succeeded);
        await fixture.Refresh();
        var before = await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "overlay.json"));
        await fixture.ClickAttribute("data-default-preset", OverlayEditorDefaults.OpenWorld);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "overlay.json")));
        Assert.Contains("Open World", await fixture.Html());
        await fixture.Invoke("ConfirmLayoutChange");
        var applied = fixture.Service.Settings;
        Assert.Equal((336, 616), (applied.Width, applied.Height));
        Assert.Equal(8, applied.Widgets.Count);
        Assert.Equal(initial.Enabled, applied.Enabled);
        Assert.Equal((initial.PositionX, initial.PositionY, initial.Visibility), (applied.PositionX, applied.PositionY, applied.Visibility));
        Assert.True(OverlayTriggerRules.Equivalent(initial.TriggerRules, applied.TriggerRules));
        Assert.Equal(.85, applied.BackgroundOpacity);
    }

    private static string[] LibraryKinds(string html) => System.Text.RegularExpressions.Regex.Matches(html,
        "data-module-kind=\"(?<kind>[^\"]+)\"").Cast<System.Text.RegularExpressions.Match>()
        .Select(match => match.Groups["kind"].Value).ToArray();

    [Theory]
    [InlineData("de", 0)]
    [InlineData("en", OverlayCatalog.MaximumWidgets)]
    public async Task MandatoryLockRemainsAboveAnEmptyOrFullCanvasAndOutsideTheOptionalLibrary(string language, int contentCount)
    {
        var contents = Enumerable.Range(0, contentCount).Select(_ => OverlayCatalog.CreateWidget("duration", 8, 8)).ToArray();
        await using var fixture = await Fixture.Create(language: language, seedLegacy: false,
            initialSettings: new() { Widgets = contents });
        var html = (await fixture.Html()).Split("<dialog", 2, StringSplitOptions.None)[0];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "data-widget-id=\"overlay-lock\""));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "data-required-widget=\"true\""));
        Assert.Contains("data-select-lock-widget=\"true\"", html);
        Assert.Equal(22, LibraryKinds(html).Length);
        Assert.DoesNotContain(OverlayLockWidget.Kind, LibraryKinds(html));
        if (contents.Length > 0)
            Assert.True(html.IndexOf("data-widget-id=\"overlay-lock\"", StringComparison.Ordinal)
                > html.IndexOf($"data-widget-id=\"{contents[^1].Id}\"", StringComparison.Ordinal));

        await fixture.ClickAttribute("data-select-lock-widget", "true");
        await fixture.Invoke("AddModule", OverlayLockWidget.Kind);
        await fixture.Invoke("AddModuleAt", OverlayLockWidget.Kind, 40d, 32d);
        Assert.Equal(OverlayLockWidget.Id, await fixture.EditorField<string?>("_selectedId"));
        Assert.Equal(contents, fixture.Service.Settings.Widgets);
        await fixture.Invoke("ChangeModuleSearch", new ChangeEventArgs { Value = "no-such-widget" });
        var filtered = (await fixture.Html()).Split("<dialog", 2, StringSplitOptions.None)[0];
        Assert.Empty(LibraryKinds(filtered));
        Assert.Contains("data-select-lock-widget=\"true\"", filtered);
        Assert.Equal(contentCount, fixture.Service.Settings.Widgets.Count);
    }

    [Fact]
    public async Task MandatoryLockMovesAndNudgesWithinItsOwnCanvasAndRestartsAtTheSavedPosition()
    {
        await using var fixture = await Fixture.Create(seedLegacy: false,
            initialSettings: new() { Width = 240, Height = 120, LockWidget = new() { X = 10, Y = 10 } });
        await fixture.Invoke("CommitWidgetGeometry", OverlayLockWidget.Id, 100d, 40d, 28d, 28d);
        Assert.Equal(new System.Drawing.RectangleF(100, 40, 28, 28), OverlayLockWidget.Bounds(fixture.Service.Settings));
        await fixture.Invoke("NudgeWidget", OverlayLockWidget.Id, 1d, 8d);
        Assert.Equal(new System.Drawing.RectangleF(101, 48, 28, 28), OverlayLockWidget.Bounds(fixture.Service.Settings));
        await fixture.Invoke("CommitWidgetGeometry", OverlayLockWidget.Id, 10_000d, -100d, 160d, 80d);
        Assert.Equal(new System.Drawing.RectangleF(212, 0, 28, 28), OverlayLockWidget.Bounds(fixture.Service.Settings));
        await fixture.Invoke("NudgeWidget", OverlayLockWidget.Id, 8d, -8d);
        Assert.Equal(new System.Drawing.RectangleF(212, 0, 28, 28), OverlayLockWidget.Bounds(fixture.Service.Settings));
        await fixture.Invoke("NudgeWidget", OverlayLockWidget.Id, -8d, 8d);
        using var restarted = new OverlayService(fixture.Directory, new OverlayCollectionSettings());
        Assert.Equal(new System.Drawing.RectangleF(204, 8, 28, 28), OverlayLockWidget.Bounds(restarted.Settings));
        Assert.Empty(restarted.Settings.Widgets);
    }

    [Fact]
    public async Task MandatoryLockCannotBeDeletedResizedOrHiddenThroughInspectorOrInteractionCallbacks()
    {
        await using var fixture = await Fixture.Create();
        var contents = fixture.Service.Settings.Widgets.ToArray();
        var bounds = OverlayLockWidget.Bounds(fixture.Service.Settings);
        await fixture.ClickAttribute("data-select-lock-widget", "true");
        var html = (await fixture.Html()).Split("<dialog", 2, StringSplitOptions.None)[0];
        Assert.DoesNotContain("data-widget-delete=\"overlay-lock\"", html);
        Assert.DoesNotContain("data-widget-resize=\"overlay-lock\"", html);
        Assert.DoesNotContain("aria-label=\"Modulbreite\"", html);
        Assert.DoesNotContain("aria-label=\"Modulhöhe\"", html);
        Assert.DoesNotContain("<span>Schriftgröße", html);
        Assert.DoesNotContain("<span>Beschriftung</span>", html);

        await fixture.Invoke("RemoveSelected");
        await fixture.Invoke("RemoveWidget", OverlayLockWidget.Id);
        await fixture.Invoke("WidgetNumber", new ChangeEventArgs { Value = "160" }, "width");
        await fixture.Invoke("WidgetNumber", new ChangeEventArgs { Value = "80" }, "height");
        await fixture.Invoke("WidgetNumber", new ChangeEventArgs { Value = "2" }, "fontScale");
        await fixture.Invoke("ChangeWidget", (Func<OverlayWidget, OverlayWidget>)(widget => widget with { ShowIcon = false, ShowLabel = true }));
        var control = OverlayLockWidget.Layout(fixture.Service.Settings);
        Assert.Equal(bounds, OverlayLockWidget.Bounds(fixture.Service.Settings));
        Assert.True(control.ShowIcon);
        Assert.False(control.ShowLabel);
        Assert.Equal(1, control.FontScale);
        Assert.Equal(contents, fixture.Service.Settings.Widgets);
        Assert.Contains("data-widget-id=\"overlay-lock\"", await fixture.Html());
    }

    [Fact]
    public async Task OriginalEditorDomShowsNineteenModulesAndPreservesLegacyContent()
    {
        await using var fixture = await Fixture.Create();
        var html = await fixture.Html();
        Assert.Contains("class=\"settings-workspace menu-workspace\"", html);
        Assert.Contains("class=\"oe-stage-viewport\"", html);
        Assert.Contains("data-module-kind=\"duration\"", html);
        Assert.DoesNotContain("data-module-kind=\"preview-metric\"", html);
        Assert.Equal(22, OverlayCatalog.Widgets.Count);
        Assert.Contains("data-content-width=\"168\"", html);
        Assert.Contains("data-canvas-resize=\"nw\"", html);
        Assert.Contains("data-canvas-resize=\"sw\"", html);
        Assert.Contains("<span class=\"oe-data-kind\">Live-Daten</span>", html);
        Assert.Contains("1234", html.Replace(".", "", StringComparison.Ordinal));
        Assert.DoesNotContain("data-overlay-section=\"shortcuts\"", html);
        Assert.DoesNotContain("Black Desert", html);
        Assert.DoesNotContain("Grindcrest", html);
        Assert.DoesNotContain("Klicks ans Spiel", html);
        Assert.DoesNotContain(">Overlay aktiv<", html);
        Assert.DoesNotContain("Bildschirmaufnahmen", html);

    }

    [Fact]
    public async Task LibraryClickAddsAWidgetAndResizeRetainsItsReferenceContent()
    {
        await using var fixture = await Fixture.Create();
        var initial = fixture.Service.Settings.Widgets.Single();
        await fixture.ClickAttribute("data-module-kind", "duration");
        Assert.Equal(2, fixture.Service.Settings.Widgets.Count);
        var added = fixture.Service.Settings.Widgets.Single(widget => widget.Id != initial.Id);
        Assert.False(added.X < initial.X + initial.Width && added.X + added.Width > initial.X &&
            added.Y < initial.Y + initial.Height && added.Y + added.Height > initial.Y);
        await fixture.Invoke("CommitWidgetGeometry", added.Id, 200d, 104d, 252d, 108d);
        var resized = fixture.Service.Settings.Widgets.Single(widget => widget.Id == added.Id);
        Assert.Equal(168, resized.ContentWidth);
        Assert.Equal(72, resized.ContentHeight);
        Assert.Equal(252, resized.Width);
        Assert.Contains("data-content-width=\"168\"", await fixture.Html());
        using var reloaded = new OverlayService(fixture.Directory);
        Assert.Equal(resized, reloaded.Settings.Widgets.Single(widget => widget.Id == added.Id));
    }

    [Fact]
    public async Task CanvasShrinkClipsWidgetsAndNorthWestResizePreservesTheirSpacing()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Invoke("AddModuleAt", "duration", 280d, 160d);
        var before = fixture.Service.Settings.Widgets.ToArray();
        await fixture.Invoke("CommitCanvasSize", 160d, 64d);
        Assert.Equal(before, fixture.Service.Settings.Widgets);
        await fixture.Invoke("CommitCanvasSize", 600d, 360d);
        await fixture.Invoke("CommitCanvasCorner", 620d, 384d, "nw");
        var after = fixture.Service.Settings.Widgets;
        for (var index = 0; index < before.Length; index++)
        {
            Assert.Equal(before[index].X + 20, after[index].X);
            Assert.Equal(before[index].Y + 24, after[index].Y);
            Assert.Equal(before[index].Width, after[index].Width);
            Assert.Equal(before[index].Height, after[index].Height);
        }
    }

    [Fact]
    public async Task ClearingRequiresConfirmationAndPreviewIsTemporary()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Invoke("ClearLayout");
        Assert.Single(fixture.Service.Settings.Widgets);
        Assert.Contains(fixture.Js.Calls, call => call.Identifier == "soulcrestOverlayDialogs.showDialog" &&
            Equals(call.Arguments[0], "overlay-layout-confirm"));
        await fixture.Invoke("CloseLayoutConfirmation");
        Assert.Single(fixture.Service.Settings.Widgets);
        await fixture.Invoke("ClearLayout");
        await fixture.Invoke("ConfirmLayoutChange");
        Assert.Empty(fixture.Service.Settings.Widgets);
        var saved = await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "overlay.json"));
        await fixture.Invoke("TogglePreview");
        Assert.True(fixture.Service.State.Previewing);
        Assert.Equal(saved, await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "overlay.json")));
        await fixture.Invoke("TogglePreview");
        Assert.False(fixture.Service.State.Previewing);
    }

    [Fact]
    public async Task NamedTemplatesAndWindowDuplicationUseTheOriginalEditorFlows()
    {
        await using var fixture = await Fixture.Create();
        var original = fixture.Service.SelectedOverlayId;
        await fixture.Invoke("OpenTemplateSave", new object?[] { null });
        await fixture.SetField("_templateNameDraft", "Example layout");
        await fixture.Invoke("SaveTemplate");
        Assert.Single(fixture.Service.Templates);
        await fixture.Invoke("DuplicateOverlay");
        Assert.Equal(2, fixture.Service.Overlays.Count);
        Assert.NotEqual(original, fixture.Service.SelectedOverlayId);
        var copy = fixture.Service.Settings;
        Assert.Equal(fixture.Service.Overlays.Single(window => window.Id == original).Settings.Width, copy.Width);
        await fixture.Invoke("ClearLayout");
        await fixture.Invoke("ConfirmLayoutChange");
        await fixture.SetField("_selectedTemplateId", fixture.Service.Templates.Single().Id);
        await fixture.Invoke("ApplySelectedTemplate");
        Assert.Single(fixture.Service.Settings.Widgets);
        Assert.Equal(2, fixture.Service.Overlays.Count);
    }

    [Fact]
    public async Task PreviewUsesTheSameModuleDomAndAppliesWindowScale()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Service.SaveAsync(fixture.Service.Settings with { Scale = 1.5, BackgroundOpacity = .35, ShowBorder = false });
        var html = await fixture.Html();
        var preview = html[html.IndexOf("id=\"kit-overlay-preview\"", StringComparison.Ordinal)..];
        Assert.Contains("overlay-widget-preview overlay-widget-preview-metric", preview);
        Assert.Contains("transform:scale(1.5)", preview);
        Assert.Contains("data-width=\"540\"", preview);
        Assert.DoesNotContain("data-widget-drag", preview);
        Assert.DoesNotContain("data-widget-resize", preview);
        Assert.DoesNotContain("data-widget-delete", preview);
    }

    [Fact]
    public async Task LoadFailureIsVisibleAndCannotOverwriteTheBrokenLayout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SoulcrestKitEditorTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "overlay.json");
        await File.WriteAllTextAsync(file, "{ broken layout");
        await using var fixture = await Fixture.Create(directory);
        var before = await File.ReadAllBytesAsync(file);
        Assert.Contains("role=\"alert\"", await fixture.Html());
        Assert.Contains("class=\"autosave-fields\" disabled", await fixture.Html());
        Assert.Contains("disabled", ActivationMarkup(await fixture.Html()));
        await fixture.ClickAttribute("data-activate-overlay", "true");
        Assert.False(fixture.Service.Settings.Enabled);
        await fixture.Invoke("AddModuleAt", "duration", 100d, 100d);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task OpenEditorFollowsUiLanguageChangesWithoutADataTracker()
    {
        var originalLanguage = UiText.Language;
        var settings = new SettingsService();
        try
        {
            await using var fixture = await Fixture.Create(settings: settings);
            Assert.Contains("Auf die Fläche ziehen oder anklicken.", await fixture.Html());
            await fixture.PublishLanguage(settings, "en");
            var english = await fixture.Html();
            Assert.Contains("Drag onto the canvas or click to add.", english);
            Assert.Contains("Example value", english);
            Assert.DoesNotContain("Beispielwert", english);
        }
        finally { UiText.Language = originalLanguage; }
    }

    [Fact]
    public async Task TemplateOnlyLoadFailureCanBeRepairedAndRetriedFromItsOwnNotice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SoulcrestKitEditorTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "overlay-templates.json");
        await File.WriteAllTextAsync(file, "{ broken templates");
        await using var fixture = await Fixture.Create(directory);
        Assert.Null(fixture.Service.LoadError);
        Assert.NotNull(fixture.Service.TemplateError);
        var broken = await File.ReadAllBytesAsync(file);
        await fixture.Invoke("SelectSection", "templates");
        Assert.Contains("data-template-reload=\"true\"", await fixture.Html());
        Assert.DoesNotContain("class=\"autosave-fields\" disabled", await fixture.Html());
        await fixture.Invoke("OpenTemplateSave", new object?[] { null });
        await fixture.SetField("_templateNameDraft", "Blocked template");
        await fixture.Invoke("SaveTemplate");
        Assert.Equal(broken, await File.ReadAllBytesAsync(file));
        await fixture.Invoke("CloseTemplateSave");
        await File.WriteAllTextAsync(file, "{\"Version\":2,\"Templates\":[]}");
        await fixture.ClickAttribute("data-template-reload", "true");
        Assert.Null(fixture.Service.TemplateError);
        Assert.DoesNotContain("data-template-reload=\"true\"", await fixture.Html());
        await fixture.Invoke("OpenTemplateSave", new object?[] { null });
        await fixture.SetField("_templateNameDraft", "Repaired template");
        await fixture.Invoke("SaveTemplate");
        Assert.Equal("Repaired template", Assert.Single(fixture.Service.Templates).Name);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportProductionRazorForVisualReview(string language)
    {
        var output = Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_KIT_EXPORT");
        if (string.IsNullOrWhiteSpace(output)) return;
        await using var fixture = await Fixture.Create(language: language, placementServices: true);
        var modules = new List<OverlayWidget>
        {
            OverlayCatalog.CreateWidget("drop-grid",8,8), OverlayCatalog.CreateWidget("drop-strip",8,184),
            OverlayCatalog.CreateWidget("drop-list",8,304), OverlayCatalog.CreateWidget("daily-kina",368,8),
            OverlayCatalog.CreateWidget("clock",664,8), OverlayCatalog.CreateWidget("rare-notice",664,88),
        };
        var index = 0;
        foreach (var definition in OverlayCatalog.Widgets.Where(definition => !modules.Any(module => module.Kind == definition.Kind)))
        {
            modules.Add(OverlayCatalog.CreateWidget(definition.Kind,368 + index % 4 * 176,208 + index / 4 * 80));
            index++;
        }
        await fixture.Service.SaveAsync(new OverlaySettings { Width=1104, Height=552, Widgets=modules });
        await fixture.Invoke("ChangePreviewMode", new ChangeEventArgs { Value=true });
        await fixture.Invoke("SelectWidget", modules[0].Id);
        await fixture.Export(output, language);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportPresetAndDisplayWorkspacesForVisualReview(string language)
    {
        var output = Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_KIT_EXPORT");
        if (string.IsNullOrWhiteSpace(output)) return;
        await using var fixture = await Fixture.Create(language: language, section: "presets", placementServices: true,
            initialSettings: new() { Width = 540, Height = 180, Widgets =
                [OverlayCatalog.CreateWidget("duration", 8, 8), OverlayCatalog.CreateWidget("daily-kina", 184, 8), OverlayCatalog.CreateWidget("drop-strip", 8, 96)] });
        var first = fixture.Service.SelectedOverlayId;
        Assert.True((await fixture.Service.RenameOverlayAsync(first, language == "de" ? "Sessionübersicht" : "Session overview")).Succeeded);
        Assert.True((await fixture.Service.CreateOverlayAsync(language == "de" ? "Abyss-Fortschritt" : "Abyss progress")).Succeeded);
        Assert.True((await fixture.Service.SaveAsync(new() { Width = 560, Height = 180, Enabled = true, Widgets =
            [OverlayCatalog.CreateWidget("abyss-rank-progress", 8, 8), OverlayCatalog.CreateWidget("abyss-killfeed", 8, 128)] })).Succeeded);
        Assert.True((await fixture.Service.CreateOverlayAsync(language == "de" ? "Meine Drops" : "My drops")).Succeeded);
        Assert.True((await fixture.Service.SaveAsync(new() { Width = 360, Height = 180, Widgets =
            [OverlayCatalog.CreateWidget("drop-grid", 8, 8)] })).Succeeded);
        Assert.True((await fixture.Service.SaveTemplateAsync(language == "de" ? "Sessionlayout" : "Session layout",
            fixture.Service.Overlays.Single(window => window.Id == first).Settings)).Succeeded);
        Assert.True((await fixture.Service.SelectOverlayAsync(first)).Succeeded);
        await fixture.Refresh();
        await fixture.Export(output, language, "-presets");
        await fixture.SetSection("display");
        await fixture.Export(output, language, "-display");
    }

    private sealed class ThemePreferenceScope : IDisposable
    {
        private readonly byte[]? _backup;
        private readonly string _language;
        public SettingsService Settings { get; }
        public ThemePreferenceScope()
        {
            var redirected = Environment.GetEnvironmentVariable("SOULCREST_DATA");
            Assert.False(string.IsNullOrWhiteSpace(redirected));
            Assert.Equal(Path.GetFullPath(redirected!), Path.GetFullPath(AppPaths.DataDirectory), ignoreCase: true);
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");
            Assert.False(string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(userData)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataDirectory)), StringComparison.OrdinalIgnoreCase));
            _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
            _language = UiText.Language;
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings { SettingsRevision = 5, OverlayTheme = "obsidian", UiLanguage = "de" });
            Settings = new SettingsService();
        }
        public void Dispose()
        {
            if (_backup is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _language;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        private CapturedKitEditor _editor = null!;
        public string Directory { get; }
        public OverlayService Service { get; }
        public StubJs Js { get; } = new();
        public SamplePresentation Source { get; } = new();
        public OverlayPlacementService? Placement => _provider.GetService<OverlayPlacementService>();
        public IReadOnlyList<OverlayMonitor> Monitors { get; set; } = [new("fixture", "Fixture", new(0, 0, 1280, 720), new(0, 0, 1280, 680))];
        private Fixture(string directory, string language, SettingsService? settings, bool staticServices, bool placementServices)
        {
            Directory = directory;
            Service = new(directory, new OverlayCollectionSettings());
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOverlayService>(Service);
            services.AddSingleton<IJSRuntime>(Js);
            services.AddSingleton<IOverlayEditorPresentationSource>(Source);
            if (settings is not null) services.AddSingleton(settings);
            else if (staticServices || placementServices) services.AddSingleton<SettingsService>();
            if (staticServices)
            {
                services.AddSingleton<UiState>();
                services.AddSingleton<OverlayHotkeyService>();
                services.AddSingleton(new Soulcrest.App.Overlay.OverlayPreviewRenderer(new Soulcrest.Core.Pets.PetCatalog([])));
            }
            if (staticServices || placementServices)
                services.AddSingleton(provider => new OverlayPlacementService(provider.GetRequiredService<SettingsService>(),
                    () => Monitors, () => null, Service));
            _provider = services.BuildServiceProvider();
            // SettingsService initializes the process UI language in its constructor.
            // Resolve it before applying this fixture's explicit rendering language;
            // the component must not lazily reset German fixtures to the host default.
            _ = _provider.GetService<SettingsService>();
            UiText.Language = language;
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public static async Task<Fixture> Create(string? directory = null, string language = "de", SettingsService? settings = null, bool seedLegacy = true,
            string? section = null, OverlaySettings? initialSettings = null, bool staticServices = false, bool placementServices = false)
        {
            var fixture = new Fixture(directory ?? Path.Combine(Path.GetTempPath(), "SoulcrestKitEditorTests", Guid.NewGuid().ToString("N")), language, settings, staticServices, placementServices);
            if (initialSettings is not null) await fixture.Service.SaveAsync(initialSettings);
            else if (seedLegacy && fixture.Service.LoadError is null) await fixture.Service.SaveAsync(OverlayCatalog.Preset("demo"));
            await fixture._renderer.Dispatcher.InvokeAsync(async () => fixture._root = await fixture._renderer.RenderComponentAsync<CapturedKitEditor>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(CapturedKitEditor.Captured)] = (Action<CapturedKitEditor>)(view => fixture._editor = view),
                    [nameof(OriginalKitOverlayEditor.Section)] = section })));
            return fixture;
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task Refresh() => _renderer.Dispatcher.InvokeAsync(() => _editor.Refresh());
        public Task SetSection(string section) => _renderer.Dispatcher.InvokeAsync(() => _editor.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(OriginalKitOverlayEditor.Section)] = section })));
        public Task PublishLanguage(SettingsService settings, string language) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            // Only publish the UI-configuration event; no settings file or tracker is written.
            UiText.Language = language;
            (typeof(SettingsService).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings) as Action)?.Invoke();
        });
        public Task<SemaphoreSlim> SaveGate() => _renderer.Dispatcher.InvokeAsync(() =>
            (SemaphoreSlim)typeof(OriginalKitOverlayEditor).GetField("_saveGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_editor)!);
        public Task<Task> BeginInvoke(string name, params object?[] arguments) => _renderer.Dispatcher.InvokeAsync<Task>(() =>
            (Task)typeof(OriginalKitOverlayEditor).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(_editor, arguments)!);
        public Task SetField(string name, object? value) => _renderer.Dispatcher.InvokeAsync(() =>
            typeof(OriginalKitOverlayEditor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_editor, value));
        public Task<T> EditorField<T>(string name) => _renderer.Dispatcher.InvokeAsync(() =>
            (T)typeof(OriginalKitOverlayEditor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_editor)!);
        public Task Invoke(string name, params object?[] arguments) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(OriginalKitOverlayEditor).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(_editor, arguments);
            if (result is Task task) await task;
            await _editor.Refresh();
        });
#pragma warning disable BL0006 // Exercise the actual click callback emitted by the production Razor component.
        public Task<object?> ElementAttribute(string name, string value, string attributeName) => _renderer.Dispatcher.InvokeAsync<object?>(() =>
        {
            using var tree = new RenderTreeBuilder();
            typeof(OriginalKitOverlayEditor).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_editor, [tree]);
            var frames = tree.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                if (frames.Array[index].FrameType != Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Element) continue;
                var attributes = new List<Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrame>();
                for (var next = index + 1; next < frames.Count && frames.Array[next].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute; next++) attributes.Add(frames.Array[next]);
                if (attributes.Any(attribute => attribute.AttributeName == name && Equals(attribute.AttributeValue, value)))
                    return attributes.Single(attribute => attribute.AttributeName == attributeName).AttributeValue;
            }
            throw new InvalidOperationException("Requested production element was not found.");
        });
        public Task<ICollection<OverlayLootItem>> VirtualizedItems() => _renderer.Dispatcher.InvokeAsync(() =>
        {
            using var tree = new RenderTreeBuilder();
            typeof(OriginalKitOverlayEditor).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_editor, [tree]);
            var frames = tree.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                if (frames.Array[index].FrameType != Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Component
                    || frames.Array[index].ComponentType != typeof(Microsoft.AspNetCore.Components.Web.Virtualization.Virtualize<OverlayLootItem>)) continue;
                for (var next = index + 1; next < frames.Count && frames.Array[next].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute; next++)
                {
                    if (frames.Array[next].AttributeName == "ItemSize") Assert.Equal(45f, frames.Array[next].AttributeValue);
                    if (frames.Array[next].AttributeName == "OverscanCount") Assert.Equal(4, frames.Array[next].AttributeValue);
                }
                for (var next = index + 1; next < frames.Count && frames.Array[next].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute; next++)
                    if (frames.Array[next].AttributeName == "Items") return (ICollection<OverlayLootItem>)frames.Array[next].AttributeValue;
            }
            throw new InvalidOperationException("Production picker does not expose a virtualized item collection.");
        });
        public Task ClickAttribute(string name, string value) => InvokeAttribute(name, value, "onclick", null);
        public Task ChangeAttribute(string name, string value, object? input) =>
            InvokeAttribute(name, value, "onchange", new ChangeEventArgs { Value = input });
        private Task InvokeAttribute(string name, string value, string eventName, ChangeEventArgs? change) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            using var tree = new RenderTreeBuilder();
            typeof(OriginalKitOverlayEditor).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_editor, [tree]);
            var frames = tree.GetFrames();
            for (var index = 0; index < frames.Count; index++)
            {
                if (frames.Array[index].FrameType != Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Element) continue;
                var attributes = new List<Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrame>();
                for (var next = index + 1; next < frames.Count && frames.Array[next].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute; next++) attributes.Add(frames.Array[next]);
                if (!attributes.Any(attribute => attribute.AttributeName == name && Equals(attribute.AttributeValue, value))) continue;
                var callback = attributes.Single(attribute => attribute.AttributeName == eventName).AttributeValue;
                if (callback is EventCallback<ChangeEventArgs> changed) await changed.InvokeAsync(change);
                else if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
                else if (callback is EventCallback untyped) await untyped.InvokeAsync(change);
                else if (callback is Func<ChangeEventArgs, Task> changedAsync) await changedAsync(change!);
                else if (callback is Action<ChangeEventArgs> changedAction) changedAction(change!);
                else if (callback is Func<Microsoft.AspNetCore.Components.Web.MouseEventArgs, Task> clickedAsync) await clickedAsync(new());
                else if (callback is Action<Microsoft.AspNetCore.Components.Web.MouseEventArgs> clickedAction) clickedAction(new());
                else if (callback is Func<Task> asynchronous) await asynchronous();
                else if (callback is Action synchronous) synchronous();
                else throw new InvalidOperationException("Unsupported click callback.");
                await _editor.Refresh();
                return;
            }
            throw new InvalidOperationException("Requested production click target was not found.");
        });
#pragma warning restore BL0006
        public async Task Export(string output, string language, string suffix = "")
        {
            var repo = FindRepository();
            var assets = Path.Combine(repo, "src", "Soulcrest.App", "wwwroot", "overlay-kit");
            var style = new StringBuilder();
            style.AppendLine(await File.ReadAllTextAsync(Path.Combine(repo, "src", "Soulcrest.App", "wwwroot", "css", "app.css")));
            foreach (var file in new[] { "base.css", "overlay-editor.css", "overlay-widgets.css", "browser-preview.css", "overlay-presets.css" })
                style.AppendLine(await File.ReadAllTextAsync(Path.Combine(assets, file)));
            var scripts = new StringBuilder();
            foreach (var file in new[] { "dialogs.js", "overlay-alignment.js", "overlay-editor.js" })
                scripts.AppendLine(await File.ReadAllTextAsync(Path.Combine(assets, file)));
            var markup = await _renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString());
            var document = "<!doctype html><html lang=\"" + language + "\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>" + style +
                "</style><body style=\"margin:0;background:#101318\">" + markup + "<script>" + scripts + "\nsoulcrestOverlayEditor.mount('overlay-editor',null,true);";
            System.IO.Directory.CreateDirectory(output);
            var sourceRoot = Path.GetFullPath(Path.Combine(repo, "src", "Soulcrest.App", "wwwroot")) + Path.DirectorySeparatorChar;
            var targetRoot = Path.GetFullPath(output) + Path.DirectorySeparatorChar;
            var iconPaths = System.Text.RegularExpressions.Regex.Matches(markup,
                "\\bsrc=\"(?<path>loot-icons/[a-z0-9_-]{1,160}\\.png)\"")
                .Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups["path"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in iconPaths)
            {
                var source = Path.GetFullPath(Path.Combine(sourceRoot, relative));
                var target = Path.GetFullPath(Path.Combine(targetRoot, relative));
                if (!source.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)
                    || !target.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(source)) continue;
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "overlay-editor-kit" + suffix + "-" + language + ".html"), document + "</script></body></html>");
            await File.WriteAllTextAsync(Path.Combine(output, "overlay-editor-kit-preview" + suffix + "-" + language + ".html"), document +
                "soulcrestOverlayDialogs.showDialog('kit-overlay-preview');soulcrestOverlayEditor.mount('kit-overlay-preview',null,true);</script></body></html>");
        }
        private static string FindRepository()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Soulcrest.slnx"))) return directory.FullName;
            throw new DirectoryNotFoundException("Soulcrest repository was not found.");
        }
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            UiText.Language = _language;
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
    private sealed class SamplePresentation : IOverlayEditorPresentationSource
    {
        public event Action? Changed;
        public List<(bool Examples,bool Market)> Acquisitions { get; } = [];
        public int ActiveLeases { get; private set; }
        public int ActiveMarketLeases { get; private set; }
        public bool DistinctModes { get; set; }
        public IReadOnlyList<OverlayLootItem>? CatalogOverride { get; set; }
        public OverlaySnapshot Capture(bool examples=false,string? language=null)
        {
            var snapshot = OverlaySnapshot.DemoFor(language);
            if (CatalogOverride is { } catalog) snapshot = snapshot with { ItemCatalog = catalog };
            if (!DistinctModes) return snapshot;
            var metrics = snapshot.Metrics.ToDictionary(entry => entry.Key, entry => entry.Value);
            metrics["duration"] = new("Sessiondauer", examples ? "EXAMPLE-ONLY" : "LIVE-ONLY");
            return snapshot with { Metrics = metrics };
        }
        public IDisposable Acquire(bool examples=false,bool market=false)
        {
            Acquisitions.Add((examples,market));
            ActiveLeases++;
            if (!examples && market) ActiveMarketLeases++;
            return new Lease(this, !examples && market);
        }
        public void Publish() => Changed?.Invoke();
        public void Dispose() { }
        private sealed class Lease(SamplePresentation source, bool market) : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed=true;
                source.ActiveLeases--;
                if (market) source.ActiveMarketLeases--;
            }
        }
    }
    private sealed class StubJs : IJSRuntime
    {
        public List<(string Identifier, object?[] Arguments)> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}

public sealed class CapturedKitEditor : OriginalKitOverlayEditor
{
    [Parameter] public Action<CapturedKitEditor>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task Refresh() => InvokeAsync(StateHasChanged);
}
