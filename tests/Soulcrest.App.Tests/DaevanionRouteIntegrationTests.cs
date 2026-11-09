using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionRouteIntegrationTests
{
    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task HoverClearsLinesUntilPointerExitAndANewCompleteFrame() => OnSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.Visible);
        Assert.True(fixture.Form.ConnectionsVisible);
        fixture.Pointer = new(fixture.Source!.GameBounds.X + 128, fixture.Source.GameBounds.Y + 52);
        fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.Visible);
        Assert.False(fixture.Form.ConnectionsVisible);
        fixture.Publish(); // Captured while hovered, so it cannot authorize lines after exit.
        fixture.Form.RefreshRoute(false);
        fixture.Pointer = Point.Empty;
        fixture.Form.RefreshRoute(false);
        Assert.False(fixture.Form.ConnectionsVisible);
        fixture.Form.RefreshRoute(false);
        Assert.False(fixture.Form.ConnectionsVisible);
        fixture.Publish(startVisible: false);
        fixture.Form.RefreshRoute(false);
        Assert.False(fixture.Form.ConnectionsVisible);
        fixture.Publish();
        fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.ConnectionsVisible);
    });

    [RequiresTheory(TestRequirement.InteractiveDesktop)]
    [InlineData("source")]
    [InlineData("expired")]
    [InlineData("route")]
    [InlineData("class")]
    [InlineData("board")]
    [InlineData("family")]
    public Task InvalidCurrentSourcesAndRouteIdentityImmediatelyHideTheIndependentLayer(string invalid) => OnSta(() =>
    {
        using var fixture = new Fixture(); fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.Visible);
        switch (invalid)
        {
            case "source": fixture.Source = null; break;
            case "expired": fixture.Source = fixture.Source! with { FrameAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2 }; break;
            case "route": fixture.Route = null; break;
            case "class": fixture.Route = fixture.Route! with { ClassId = "Ranger" }; break;
            case "board": fixture.Source = fixture.Source! with { Board = fixture.Source.Board with { Id = -1 } }; break;
            case "family": fixture.Source = fixture.Source! with { Board = fixture.Source.Board with { FamilyId = "ariel" } }; break;
        }
        fixture.Form.RefreshRoute(false);
        Assert.False(fixture.Form.Visible);
        Assert.Null(fixture.Form.ShownSource);
        Assert.False(fixture.Form.ConnectionsVisible);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task CoveredStartAndNonRouteNodeInvalidateAllEdgesButNotCurrentlyVisibleRouteNodes() => OnSta(() =>
    {
        using var fixture = new Fixture(); fixture.Form.RefreshRoute(false);
        fixture.Publish(startVisible: false);
        fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.Visible);
        Assert.False(fixture.Form.ConnectionsVisible);
        Assert.Same(fixture.Source, fixture.Form.ShownSource);
        fixture.Publish();
        fixture.Source = fixture.Source! with { Detection = fixture.Source.Detection with
            { Nodes = fixture.Source.Detection.Nodes.Where(node => node.Row != 9).ToArray() } };
        fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.Visible);
        Assert.False(fixture.Form.ConnectionsVisible);
        fixture.Publish(); fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.ConnectionsVisible);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task SourceChangeDuringRenderingDropsTheEntireOldSurfaceAndNeverReconcilesItsLines() => OnSta(() =>
    {
        var original = DaevanionRouteRendererTests.Picture();
        var masked = original with { FrameAt = original.FrameAt + 1,
            Detection = original.Detection with { StartVisible = false } };
        var reads = 0; var route = Fixture.RouteFor(original);
        using var form = new DaevanionRouteForm(() => ++reads == 1 ? original : masked, _ => route, () => Point.Empty);
        form.RefreshRoute(false);
        Assert.False(form.Visible);
        Assert.Null(form.ShownSource);
        form.RefreshRoute(false);
        Assert.True(form.Visible);
        Assert.Same(masked, form.ShownSource);
        Assert.False(form.ConnectionsVisible);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task RouteLayerRemainsTransparentToInputAndDoesNotActivateItsWindow() => OnSta(() =>
    {
        using var fixture = new Fixture(); fixture.Form.RefreshRoute(false);
        var styles = NativeMethods.GetWindowLongPtr(fixture.Form.Handle, NativeMethods.GWL_EXSTYLE);
        Assert.NotEqual(0, styles & NativeMethods.WS_EX_TRANSPARENT);
        Assert.NotEqual(0, styles & NativeMethods.WS_EX_NOACTIVATE);
        Assert.NotEqual(0, styles & NativeMethods.WS_EX_TOOLWINDOW);
        Assert.False(fixture.Form.ShowInTaskbar);
        Assert.True((bool)typeof(DaevanionRouteForm).GetProperty("ShowWithoutActivation", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(fixture.Form)!);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task ActiveEditTakesPrecedenceAndEndingItRestoresTheUnchangedSavedRoute() => OnSta(() =>
    {
        using var fixture = new Fixture(); var route = fixture.Route;
        fixture.Edit = Changes(1);
        fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.Visible); Assert.False(fixture.Form.ConnectionsVisible);
        Assert.Same(route, fixture.Route);
        fixture.Edit = null;
        fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.Visible); Assert.True(fixture.Form.ConnectionsVisible);
        Assert.Same(route, fixture.Route);
        Assert.Equal(new[] { 1, 2, 3 }, route!.Result.Boards.Single().NodeIds);
    });

    [RequiresTheory(TestRequirement.InteractiveDesktop)]
    [InlineData("empty")]
    [InlineData("invalid")]
    [InlineData("expired")]
    public Task ActiveEmptyInvalidOrExpiredEditsNeverFallBackToTheSavedRoute(string reason) => OnSta(() =>
    {
        using var fixture = new Fixture(); fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.ConnectionsVisible);
        fixture.Edit = reason switch
        {
            "empty" => new() { CanPresent = true, Revision = 2 },
            "invalid" => Changes(2) with { CanPresent = false },
            _ => Changes(2)
        };
        if (reason == "expired") fixture.Source = fixture.Source! with
            { FrameAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2 };
        fixture.Form.RefreshRoute(false);
        Assert.False(fixture.Form.Visible); Assert.Null(fixture.Form.ShownSource);
        Assert.NotNull(fixture.Route);
    });

    [RequiresTheory(TestRequirement.InteractiveDesktop)]
    [InlineData(2)]
    [InlineData(3)]
    public Task EditRevisionChangesDuringRenderingOrHandlePlacementDiscardTheEntireOldSurface(int changeOnRead) => OnSta(() =>
    {
        var source = DaevanionRouteRendererTests.Picture(); var reads = 0;
        using var form = new DaevanionRouteForm(() => source, _ => Fixture.RouteFor(source), () => Point.Empty,
            _ => Changes(++reads >= changeOnRead ? 2 : 1));
        form.RefreshRoute(false);
        Assert.False(form.Visible); Assert.Null(form.ShownSource);
        form.RefreshRoute(false);
        Assert.True(form.Visible); Assert.False(form.ConnectionsVisible);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task AChangedEditRevisionAlsoInvalidatesTheCachedFrameFastPath() => OnSta(() =>
    {
        var source = DaevanionRouteRendererTests.Picture(); var changing = false; var reads = 0;
        using var form = new DaevanionRouteForm(() => source, _ => Fixture.RouteFor(source), () => Point.Empty,
            _ => Changes(changing && ++reads >= 2 ? 2 : 1));
        form.RefreshRoute(false); Assert.True(form.Visible);
        changing = true;
        form.RefreshRoute(false);
        Assert.False(form.Visible); Assert.Null(form.ShownSource);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task InvalidatingTheEditDuringRenderingCannotPublishSavedRouteArtworkInstead() => OnSta(() =>
    {
        var source = DaevanionRouteRendererTests.Picture(); var reads = 0;
        using var form = new DaevanionRouteForm(() => source, _ => Fixture.RouteFor(source), () => Point.Empty,
            _ => Changes(1) with { CanPresent = ++reads == 1 });
        form.RefreshRoute(false);
        Assert.False(form.Visible); Assert.Null(form.ShownSource);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task DraftRimsDoNotAuthorizeConnectionsAfterHoverWhenTheSavedRouteReturns() => OnSta(() =>
    {
        using var fixture = new Fixture(); fixture.Edit = Changes(1);
        fixture.Pointer = new(fixture.Source!.GameBounds.X + 128, fixture.Source.GameBounds.Y + 52);
        fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.Visible); Assert.False(fixture.Form.ConnectionsVisible);
        fixture.Pointer = Point.Empty; fixture.Edit = null;
        fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.Visible); Assert.False(fixture.Form.ConnectionsVisible);
        fixture.Publish(); fixture.Form.RefreshRoute(false);
        Assert.True(fixture.Form.ConnectionsVisible);
    });

    private static DaevanionBoardEditOverlay Changes(long revision) => new()
    {
        CanPresent = true, Revision = revision,
        AddedNodeIds = new HashSet<int> { 4 }, RemovedNodeIds = new HashSet<int> { 2 }
    };

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
        internal DaevanionSnapshot? Source = DaevanionRouteRendererTests.Picture();
        internal DaevanionSavedRoute? Route;
        internal DaevanionBoardEditOverlay? Edit;
        internal Point Pointer = Point.Empty;
        internal readonly DaevanionRouteForm Form;
        private readonly IReadOnlyList<DaevanionNodeAnchor> _anchors;
        internal Fixture()
        {
            Route = RouteFor(Source!);
            _anchors = Source!.Detection.Nodes;
            Form = new(() => Source is { } source && DaevanionLayoutStability.Fresh(source.FrameAt) ? source : null,
                _ => Route, () => Pointer, _ => Edit);
        }
        internal void Publish(bool startVisible = true) => Source = Source! with
            { FrameAt = Stopwatch.GetTimestamp(), Detection = Source!.Detection with { StartVisible = startVisible, Nodes = _anchors } };
        internal static DaevanionSavedRoute RouteFor(DaevanionSnapshot source) => new()
        {
            ClassId = source.Board.ClassId,
            Result = new() { ClassId = source.Board.ClassId, Status = DaevanionOptimizationStatus.Optimal,
                Boards = [new() { BoardId = source.Board.Id, FamilyId = source.Board.FamilyId,
                    NodeIds = [1, 2, 3], Edges = [new(1, 2), new(2, 3)] }] }
        };
        public void Dispose() => Form.Dispose();
    }
}
