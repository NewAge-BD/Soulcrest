using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DaevanionHighlightIntegrationTests
{
    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task SavedHighlightsRemainReachableWhenNodeContentStartsHidden() => OnSta(() =>
    {
        using var f = new Fixture();
        var snapshot = f.Publish();
        var revision = f.Service.ObservationRevision;
        f.Form.Present();
        Assert.False(f.Form.Visible);
        Assert.True(f.Form.ToggleWindow.Visible);
        Assert.True(f.Form.StatRailWindow.Visible);
        Assert.True(f.Form.HighlightWindow.Visible);
        Assert.Null(f.Form.StatRailWindow.Owner);
        Assert.Null(f.Form.HighlightWindow.Owner);
        Assert.Contains(f.Form.StatRailWindow.RailLayout.Items, item => item.Id == f.InitialStat && item.Selected);
        Assert.Equal(f.Settings.Current.DaevanionHighlightedStatIds, new SettingsService().Current.DaevanionHighlightedStatIds);
        Assert.Same(snapshot, f.Service.Snapshot);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task IndependentStatClicksAndClearOnlyChangeTheSavedSelection() => OnSta(() =>
    {
        using var f = new Fixture(); var snapshot = f.Publish(); f.Form.Present();
        var revision = f.Service.ObservationRevision;
        var second = f.Form.StatRailWindow.RailLayout.Items.First(item => item.Kind == DaevanionStatRailItemKind.Stat && !item.Selected).Id;
        ClickRail(f.Form.StatRailWindow, second);
        Assert.Contains(f.InitialStat, f.Settings.Current.DaevanionHighlightedStatIds);
        Assert.Contains(second, f.Settings.Current.DaevanionHighlightedStatIds);
        Assert.Equal(2, f.Settings.Current.DaevanionHighlightedStatIds.Count);
        Assert.Same(snapshot, f.Service.Snapshot);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Assert.False(f.Form.Visible);
        Assert.True(f.Form.HighlightWindow.Visible);
        ClickRail(f.Form.StatRailWindow, DaevanionStatRailLayout.ClearId);
        Assert.Empty(f.Settings.Current.DaevanionHighlightedStatIds);
        Assert.Empty(new SettingsService().Current.DaevanionHighlightedStatIds);
        Assert.False(f.Form.HighlightWindow.Visible);
        Assert.True(f.Form.StatRailWindow.Visible);
        Assert.True(f.Form.ToggleWindow.Visible);
        Assert.Same(snapshot, f.Service.Snapshot);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task NodeContentToggleDoesNotHideOrResetTheIndependentHighlightLayer() => OnSta(() =>
    {
        using var f = new Fixture();
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var matching = board.Nodes.First(node => node.Kind == DaevanionNodeKind.Stat && node.Effects.Any(effect => effect.StatId == f.InitialStat));
        var snapshot = f.Publish(visibleNodes: [matching]); f.Form.Present();
        var revision = f.Service.ObservationRevision;
        ClickToggle(f.Form.ToggleWindow);
        Assert.True(f.Settings.Current.DaevanionNodeContentVisible);
        Assert.True(f.Form.Visible);
        Assert.True(f.Form.HighlightWindow.Visible);
        Assert.True(f.Form.StatRailWindow.Visible);
        ClickToggle(f.Form.ToggleWindow);
        Assert.False(f.Settings.Current.DaevanionNodeContentVisible);
        Assert.False(f.Form.Visible);
        Assert.True(f.Form.HighlightWindow.Visible);
        Assert.True(f.Form.StatRailWindow.Visible);
        Assert.Equal([f.InitialStat], f.Settings.Current.DaevanionHighlightedStatIds);
        Assert.Same(snapshot, f.Service.Snapshot);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task TooltipMasksAndActivationChangesUseFreshAnchorsWithoutResettingSourceContext() => OnSta(() =>
    {
        using var f = new Fixture();
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var matching = board.Nodes.First(node => node.Kind == DaevanionNodeKind.Stat && node.Effects.Any(effect => effect.StatId == f.InitialStat));
        var first = f.Publish(visibleNodes: [matching]); f.Form.Present();
        var railBounds = f.Form.StatRailWindow.Bounds;
        var toggleBounds = f.Form.ToggleWindow.Bounds;
        var revision = f.Service.ObservationRevision;
        var masked = f.Publish(visibleNodes: []); f.Form.CheckVisibility();
        Assert.Same(first.Context, masked.Context);
        Assert.Same(masked, f.Service.Snapshot);
        Assert.Empty(ShownHighlights(f.Form.HighlightWindow)!.Detection.Nodes);
        Assert.Equal(railBounds, f.Form.StatRailWindow.Bounds);
        Assert.Equal(toggleBounds, f.Form.ToggleWindow.Bounds);
        Assert.True(f.Form.StatRailWindow.Visible);
        var returned = f.Publish(visibleNodes: [matching], appearance: DaevanionNodeAppearance.Active);
        f.Form.CheckVisibility();
        var shown = ShownHighlights(f.Form.HighlightWindow)!;
        Assert.True(f.Form.HighlightWindow.Visible);
        Assert.Single(shown.Detection.Nodes);
        Assert.Equal(DaevanionNodeAppearance.Active, shown.Detection.Nodes[0].Appearance);
        Assert.Equal(returned.FrameAt, shown.FrameAt);
        Assert.Same(first.Context, shown.Context);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Assert.Equal([f.InitialStat], f.Settings.Current.DaevanionHighlightedStatIds);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresTheory(TestRequirement.InteractiveDesktop)]
    [InlineData("foreground")]
    [InlineData("placement")]
    [InlineData("master")]
    [InlineData("hotkey")]
    [InlineData("context")]
    [InlineData("window")]
    [InlineData("expired")]
    [InlineData("board")]
    public Task HiddenNodeContentsDoNotPreventEveryLayerFromBeingSuppressed(string reason) => OnSta(() =>
    {
        using var f = new Fixture(); f.Publish(); f.Form.Present();
        Assert.True(f.Form.HighlightWindow.Visible);
        switch (reason)
        {
            case "foreground": f.Form.SetForegroundSuppressed(true); break;
            case "placement": f.Form.SetPlacementHidden(true); break;
            case "master": f.Settings.Update(settings => settings.DaevanionOverlayEnabled = false); break;
            case "hotkey": f.Settings.Update(settings => OverlayHotkeys.ToggleVisibility(settings, "daevanion.toggle")); break;
            case "context": f.Network.ResetProgressContext(); break;
            case "window": f.GameBounds = null; break;
            case "expired": Thread.Sleep(1100); break;
            case "board": f.CloseBoard(); break;
        }
        f.Form.CheckVisibility();
        Assert.False(f.Form.Visible);
        Assert.False(f.Form.ToggleWindow.Visible);
        Assert.False(f.Form.StatRailWindow.Visible);
        Assert.False(f.Form.HighlightWindow.Visible);
        Assert.Equal([f.InitialStat], f.Settings.Current.DaevanionHighlightedStatIds);
        Assert.False(f.Settings.Current.DaevanionNodeContentVisible);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task BoardSwitchRebuildsTheStatListWhileKeepingGlobalPreferences() => OnSta(() =>
    {
        using var f = new Fixture();
        var ariel = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var arielStat = DaevanionStatRailLayout.StatsFor(ariel).First(stat => stat.Id.StartsWith("PvE", StringComparison.Ordinal)).Id;
        f.Settings.Update(settings => settings.DaevanionHighlightedStatIds.Add(arielStat));
        var initial = f.Publish(); f.Form.Present();
        Assert.Equal(8, f.Form.StatRailWindow.RailLayout.Items.Count(item => item.Kind == DaevanionStatRailItemKind.Stat));
        Assert.DoesNotContain(f.Form.StatRailWindow.RailLayout.Items, item => item.Id == arielStat);
        var revision = f.Service.ObservationRevision;
        var next = f.Publish("ariel"); f.Form.CheckVisibility();
        Assert.Same(initial.Context, next.Context);
        Assert.Equal(14, f.Form.StatRailWindow.RailLayout.Items.Count(item => item.Kind == DaevanionStatRailItemKind.Stat));
        Assert.Contains(f.Form.StatRailWindow.RailLayout.Items, item => item.Id == arielStat && item.Selected);
        Assert.Equal(new[] { arielStat, f.InitialStat }.Order(StringComparer.Ordinal),
            f.Settings.Current.DaevanionHighlightedStatIds.Order(StringComparer.Ordinal));
        Assert.Same(next, f.Service.Snapshot);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Assert.True(f.Form.HighlightWindow.Visible);
        Assert.False(f.Form.Visible);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task AStatPressCannotSurviveACharacterContextChangeAtTheSamePosition() => OnSta(() =>
    {
        using var f = new Fixture(); var initial = f.Publish(); f.Form.Present();
        var rail = f.Form.StatRailWindow;
        var target = rail.RailLayout.Items.First(item => item.Kind == DaevanionStatRailItemKind.Stat && !item.Selected);
        var bounds = rail.Bounds;
        Mouse(rail, "OnMouseDown", Center(target.Bounds));
        f.Network.ObserveIdentity("own", Fixture.Identity with { CharacterId = 102 }, DateTimeOffset.UnixEpoch);
        f.Publish(); f.Form.Present();
        Assert.Equal(bounds, rail.Bounds);
        Assert.NotSame(initial.Context, f.Service.Snapshot!.Context);
        Mouse(rail, "OnMouseUp", Center(target.Bounds));
        Assert.Equal([f.InitialStat], f.Settings.Current.DaevanionHighlightedStatIds);
        Assert.True(rail.Visible);
        Assert.True(f.Form.HighlightWindow.Visible);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task AnimationFramesKeepTheSourceDeadlineAndStopWhenThatSourceExpires() => OnSta(() =>
    {
        using var f = new Fixture(); var snapshot = f.Publish(); f.Form.Present();
        var revision = f.Service.ObservationRevision;
        Animate(f.Form.HighlightWindow);
        Animate(f.Form.HighlightWindow);
        Assert.True(f.Form.HighlightWindow.Visible);
        Assert.Equal(snapshot.FrameAt, ShownHighlights(f.Form.HighlightWindow)!.FrameAt);
        Assert.Same(snapshot, f.Service.Snapshot);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Thread.Sleep(1100);
        Animate(f.Form.HighlightWindow);
        Assert.False(f.Form.HighlightWindow.Visible);
        Assert.Null(ShownHighlights(f.Form.HighlightWindow));
        Assert.Same(snapshot, f.Service.Snapshot);
        Assert.Equal(snapshot.FrameAt, f.Service.Snapshot!.FrameAt);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Assert.Equal([f.InitialStat], f.Settings.Current.DaevanionHighlightedStatIds);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task AnimationReadsTheCurrentSelectionBeforeRenderingItsNextFrame() => OnSta(() =>
    {
        using var f = new Fixture(); var snapshot = f.Publish(); f.Form.Present();
        var revision = f.Service.ObservationRevision;
        f.Settings.Update(settings => settings.DaevanionHighlightedStatIds.Clear());
        Animate(f.Form.HighlightWindow);
        Assert.False(f.Form.HighlightWindow.Visible);
        Assert.Null(ShownHighlights(f.Form.HighlightWindow));
        Assert.Empty(f.Settings.Current.DaevanionHighlightedStatIds);
        Assert.Same(snapshot, f.Service.Snapshot);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Assert.True(f.Form.StatRailWindow.Visible);
        Assert.True(f.Form.ToggleWindow.Visible);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresTheory(TestRequirement.InteractiveDesktop)]
    [InlineData(false)]
    [InlineData(true)]
    public Task FailedSelectionWritesKeepThePreviousPreferenceAndMark(bool clear) => OnSta(() =>
    {
        using var f = new Fixture(); var snapshot = f.Publish(); f.Form.Present();
        var revision = f.Service.ObservationRevision;
        var target = clear ? DaevanionStatRailLayout.ClearId : f.Form.StatRailWindow.RailLayout.Items
            .First(item => item.Kind == DaevanionStatRailItemKind.Stat && !item.Selected).Id;
        File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            ClickRail(f.Form.StatRailWindow, target);
            Assert.Equal([f.InitialStat], f.Settings.Current.DaevanionHighlightedStatIds);
            Assert.Same(snapshot, f.Service.Snapshot);
            Assert.Equal(revision, f.Service.ObservationRevision);
            Assert.True(f.Form.StatRailWindow.Visible);
            Assert.True(f.Form.HighlightWindow.Visible);
            Assert.False(f.Form.Visible);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); }
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    private static DaevanionSnapshot? ShownHighlights(DaevanionHighlightForm form) =>
        (DaevanionSnapshot?)typeof(DaevanionHighlightForm).GetField("_shown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form);
    private static void Animate(DaevanionHighlightForm form) => typeof(DaevanionHighlightForm)
        .GetMethod("Animate", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
    private static void ClickRail(DaevanionStatRailForm rail, string statId)
    {
        var point = Center(rail.RailLayout.Items.Single(item => item.Id == statId).Bounds);
        Mouse(rail, "OnMouseDown", point); Mouse(rail, "OnMouseUp", point);
    }
    private static void ClickToggle(DaevanionToggleForm toggle)
    { Mouse(toggle, "OnMouseDown", Center(toggle.ClientRectangle)); Mouse(toggle, "OnMouseUp", Center(toggle.ClientRectangle)); }
    private static Point Center(Rectangle rectangle) => new(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);
    private static void Mouse(Form form, string method, Point point) => form.GetType()
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(form, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly OwnCharacterIdentity Identity = new(101, 501, 300, "SyntheticHighlight") { ClassId = "Cleric" };
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-highlights-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        internal readonly SettingsService Settings = new();
        internal readonly GameCaptureService Capture = new();
        internal readonly NetworkLootService Network;
        internal readonly DaevanionService Service;
        internal readonly DaevanionOverlayForm Form;
        internal readonly string InitialStat;
        internal Rectangle? GameBounds = new(50, 50, 1800, 1250);
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            InitialStat = DaevanionStatRailLayout.StatsFor(DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!)[0].Id;
            Settings.Update(settings =>
            {
                settings.DaevanionOverlayEnabled = true; settings.DaevanionNodeContentVisible = false;
                settings.OverlayLanguage = "en"; settings.DaevanionHighlightedStatIds = [InitialStat];
            });
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Service = new(Network, Settings, Capture, () => GameBounds);
            Network.ObserveIdentity("own", Identity, DateTimeOffset.UnixEpoch);
            Form = new(Service, Settings);
        }
        internal DaevanionSnapshot Publish(string family = "nezekan", IReadOnlyList<DaevanionNode>? visibleNodes = null,
            DaevanionNodeAppearance appearance = DaevanionNodeAppearance.Inactive)
        {
            var board = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
            const int spacing = 44, nodeSize = 32;
            var minRow = board.Nodes.Min(node => node.Row); var minColumn = board.Nodes.Min(node => node.Column);
            var anchors = (visibleNodes ?? board.Nodes).Select(node => new DaevanionNodeAnchor(node.Row, node.Column,
                new(80 + (node.Column - minColumn) * spacing, 100 + (node.Row - minRow) * spacing, nodeSize, nodeSize), .95)
                { Appearance = appearance }).ToArray();
            var snapshot = new DaevanionSnapshot(Network.OwnCharacter!, board, GameBounds!.Value, Stopwatch.GetTimestamp(),
                new(family, new(80 + nodeSize / 2d - (minColumn - 1) * spacing,
                    100 + nodeSize / 2d - (minRow - 1) * spacing), spacing, nodeSize, anchors, .95), "en");
            Assert.True(Service.TryPublishObservation(snapshot, Service.ObservationRevision));
            return snapshot;
        }
        internal void CloseBoard() => Assert.True((bool)typeof(DaevanionService)
            .GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Service, [null, "Daevanion-Board nicht erkannt.", Service.ObservationRevision])!);
        public void Dispose()
        {
            Form.Dispose(); Service.Dispose(); Network.Dispose(); Capture.Dispose();
            if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _language; Directory.Delete(_root, recursive: true);
        }
    }
}
