using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionStatRailTests
{
    [Theory]
    [InlineData("nezekan", 8)]
    [InlineData("zikel", 8)]
    [InlineData("vaizel", 8)]
    [InlineData("triniel", 8)]
    [InlineData("ariel", 14)]
    [InlineData("azphel", 12)]
    public void RailUsesTheCompleteBoardsExactStatsInCatalogOrderForEveryClass(string family, int expected)
    {
        foreach (var board in DaevanionCatalog.Default.Boards.Where(board => board.FamilyId == family))
        {
            var stats = DaevanionStatRailLayout.StatsFor(board);
            Assert.Equal(expected, stats.Count);
            var ids = board.Nodes.Where(node => node.Kind == DaevanionNodeKind.Stat).SelectMany(node => node.Effects)
                .Select(effect => effect.StatId).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(DaevanionCatalog.Default.Stats.Where(stat => ids.Contains(stat.Id)).Select(stat => stat.Id), stats.Select(stat => stat.Id));
        }
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void CompleteGeometryControlsPlacementRegardlessOfVisibleAnchors(int dpi)
    {
        var source = Picture("ariel");
        var selected = new HashSet<string>(StringComparer.Ordinal) { "PvEAccuracy" };
        var layout = DaevanionStatRailLayout.Build(source.Board, source.Detection, source.GameBounds.Size, selected, "de", dpi: dpi);
        var covered = DaevanionStatRailLayout.Build(source.Board, source.Detection with { Nodes = [] }, source.GameBounds.Size, selected, "de", dpi: dpi);
        Assert.True(layout.HasSpace); Assert.Equal(14, layout.Items.Count(item => item.Kind == DaevanionStatRailItemKind.Stat));
        Assert.Equal((int)Math.Round(176 * dpi / 96d), layout.Bounds.Width);
        Assert.True(layout.Bounds.Left > source.Detection.Origin.X + 14 * source.Detection.Spacing);
        Assert.True(new Rectangle(Point.Empty, source.GameBounds.Size).Contains(layout.Bounds));
        Assert.Equal(layout.Bounds, covered.Bounds); Assert.Equal(layout.Items, covered.Items);
        Assert.Contains(layout.Items, item => item.Id == "PvEAccuracy" && item.Selected);
        Assert.Contains(layout.Items, item => item.Id == DaevanionStatRailLayout.ClearId && item.Enabled);
    }

    [Fact]
    public void HeightPagesWithoutLosingStatsAndRightSideToggleKeepsItsSpace()
    {
        var source = Picture("ariel"); var selected = new HashSet<string>();
        var client = new Size(2200, 240);
        var first = DaevanionStatRailLayout.Build(source.Board, source.Detection, client, selected, "en");
        Assert.True(first.PageCount > 1);
        var all = new List<string>();
        for (var page = 0; page < first.PageCount; page++)
        {
            var layout = DaevanionStatRailLayout.Build(source.Board, source.Detection, client, selected, "en", page: page);
            Assert.True(new Rectangle(Point.Empty, client).Contains(layout.Bounds));
            Assert.Equal(first.Bounds, layout.Bounds);
            Assert.True(layout.Bounds.Height <= client.Height - 16);
            all.AddRange(layout.Items.Where(item => item.Kind == DaevanionStatRailItemKind.Stat).Select(item => item.Id));
            Assert.Equal(page > 0, layout.Items.Single(item => item.Kind == DaevanionStatRailItemKind.Previous).Enabled);
            Assert.Equal(page + 1 < first.PageCount, layout.Items.Single(item => item.Kind == DaevanionStatRailItemKind.Next).Enabled);
        }
        Assert.Equal(DaevanionStatRailLayout.StatsFor(source.Board).Select(stat => stat.Id), all);
        var toggle = new Rectangle(first.Bounds.Left - 4, 8, 112, 28);
        var shifted = DaevanionStatRailLayout.Build(source.Board, source.Detection, client, selected, "en", toggle);
        Assert.True(shifted.Bounds.Top > toggle.Bottom); Assert.False(shifted.Bounds.IntersectsWith(toggle));
        Assert.Equal(first.PageCount - 1, DaevanionStatRailLayout.Build(source.Board, source.Detection, client, selected, "en", page: 999).Page);
    }

    [Fact]
    public void UnsafeOrTooSmallGeometryDoesNotCreateAControlSurface()
    {
        var source = Picture("ariel"); var selected = new HashSet<string>();
        Assert.False(DaevanionStatRailLayout.Build(source.Board, source.Detection, new(100, 400), selected, "en").HasSpace);
        Assert.False(DaevanionStatRailLayout.Build(source.Board, source.Detection, new(2200, 90), selected, "en").HasSpace);
        Assert.False(DaevanionStatRailLayout.Build(source.Board, source.Detection with { Origin = new(double.NaN, 1) }, source.GameBounds.Size, selected, "en").HasSpace);
        Assert.False(DaevanionStatRailLayout.Build(source.Board with { Nodes = [] }, source.Detection, source.GameBounds.Size, selected, "en").HasSpace);
        Assert.False(DaevanionStatRailLayout.Build(source.Board, source.Detection, source.GameBounds.Size, selected, "en", dpi: 0).HasSpace);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(288)]
    public void ProductionPainterKeepsTransparentGapsAndDistinctPhysicalInteractiveStates(int bitmapDpi)
    {
        var source = Picture("ariel");
        var layout = DaevanionStatRailLayout.Build(source.Board, source.Detection, source.GameBounds.Size, new HashSet<string>(), "de");
        var first = layout.Items[0];
        using var normal = DaevanionStatRailRenderer.Render(layout);
        using var hover = DaevanionStatRailRenderer.Render(layout, first.Id);
        using var pressed = DaevanionStatRailRenderer.Render(layout, first.Id, first.Id);
        using var selected = DaevanionStatRailRenderer.Render(layout with { Items = layout.Items.Select(item => item.Id == first.Id ? item with { Selected = true } : item).ToArray() });
        using var english = DaevanionStatRailRenderer.Render(DaevanionStatRailLayout.Build(source.Board, source.Detection, source.GameBounds.Size, new HashSet<string>(), "en"));
        Assert.Equal(0, normal.GetPixel(0, 0).A);
        Assert.Equal(0, normal.GetPixel(50, first.Bounds.Bottom + 1).A);
        Assert.NotEqual(Pixels(normal), Pixels(hover)); Assert.NotEqual(Pixels(hover), Pixels(pressed));
        Assert.NotEqual(Pixels(normal), Pixels(selected)); Assert.NotEqual(Pixels(normal), Pixels(english));
        using var metadata = new Bitmap(normal.Width, normal.Height, PixelFormat.Format32bppPArgb);
        metadata.SetResolution(bitmapDpi, bitmapDpi);
        using (var graphics = Graphics.FromImage(metadata))
            DaevanionStatRailRenderer.Paint(graphics, layout with { Bounds = new(Point.Empty, normal.Size) });
        Assert.Equal(Pixels(normal), Pixels(metadata));
        Assert.Equal("Hervorhebungen konnten nicht gespeichert werden.", DaevanionStatRailForm.SaveErrorText("de"));
        Assert.Equal("Highlights could not be saved.", DaevanionStatRailForm.SaveErrorText("en"));
    }

    [Fact]
    public Task WaitingDoesNotCreateAnInputHandle() => OnSta(() =>
    {
        using var rail = new DaevanionStatRailForm(() => false); rail.Suppress(); rail.Suppress();
        Assert.False(rail.IsHandleCreated); Assert.False(rail.Visible);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task TheIndependentRailHasNoFocusAndItsGapsDoNotReceiveClicks() => OnSta(() =>
    {
        var source = Picture("ariel");
        using var rail = new DaevanionStatRailForm(() => true);
        var foreground = GetForegroundWindow();
        rail.Present(source, new HashSet<string>(), Rectangle.Empty, false);
        Assert.True(rail.Visible); Assert.Null(rail.Owner);
        var style = NativeMethods.GetWindowLongPtr(rail.Handle, NativeMethods.GWL_EXSTYLE);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_NOACTIVATE);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_LAYERED);
        Assert.Equal((nint)0, style & NativeMethods.WS_EX_TRANSPARENT);
        Assert.Equal((nint)NativeMethods.MA_NOACTIVATE, SendMessage(rail.Handle, NativeMethods.WM_MOUSEACTIVATE, 0, 0));
        Assert.Equal(foreground, GetForegroundWindow());
        var first = rail.RailLayout.Items[0]; var center = Center(first.Bounds);
        Assert.Equal(rail.Handle, WindowFromPoint(rail.PointToScreen(center)));
        Assert.NotEqual(rail.Handle, WindowFromPoint(rail.PointToScreen(new(center.X, first.Bounds.Bottom + 1))));
        Assert.NotEqual(rail.Handle, WindowFromPoint(rail.PointToScreen(Point.Empty)));
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task CompleteClicksFireOnceAndContextMovementOrHandleChangesCancelThePress() => OnSta(() =>
    {
        var source = Picture("ariel"); var selected = new HashSet<string>(); var allowed = true;
        using var rail = new DaevanionStatRailForm(() => allowed);
        rail.Present(source, selected, Rectangle.Empty, false);
        var count = 0; string? fired = null; rail.StatToggled += id => { count++; fired = id; };
        var first = rail.RailLayout.Items[0]; var point = MousePoint(Center(first.Bounds));
        SendMessage(rail.Handle, 0x0202, 0, point); Assert.Equal(0, count);
        SendMessage(rail.Handle, 0x0201, 1, point);
        rail.Present(source with { FrameAt = Stopwatch.GetTimestamp() }, selected, Rectangle.Empty, false);
        SendMessage(rail.Handle, 0x0202, 0, point); SendMessage(rail.Handle, 0x0202, 0, point);
        Assert.Equal(1, count); Assert.Equal(first.Id, fired);
        SendMessage(rail.Handle, 0x0201, 1, point);
        source = source with { Context = new(source.Context.Identity, source.Context.Generation + 1) };
        rail.Present(source, selected, Rectangle.Empty, false); SendMessage(rail.Handle, 0x0202, 0, point); Assert.Equal(1, count);
        SendMessage(rail.Handle, 0x0201, 1, point);
        source = source with { GameBounds = source.GameBounds with { X = source.GameBounds.X + 20 } };
        rail.Present(source, selected, Rectangle.Empty, false); SendMessage(rail.Handle, 0x0202, 0, point); Assert.Equal(1, count);
        SendMessage(rail.Handle, 0x0201, 1, point); rail.Capture = false;
        SendMessage(rail.Handle, 0x0202, 0, point); Assert.Equal(1, count);
        SendMessage(rail.Handle, 0x0201, 1, point);
        typeof(Control).GetMethod("RecreateHandle", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(rail, null);
        rail.Present(source, selected, Rectangle.Empty, true); SendMessage(rail.Handle, 0x0202, 0, point); Assert.Equal(1, count);
        SendMessage(rail.Handle, 0x0201, 1, point); allowed = false;
        SendMessage(rail.Handle, 0x0202, 0, point); Assert.Equal(1, count);
        rail.Suppress(); Assert.False(rail.Visible);
    });

    private static DaevanionSnapshot Picture(string family)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
        const int nodeSize = 48, spacing = 61;
        var origin = new OpenCvSharp.Point2d(90 - (board.Nodes.Min(node => node.Column) - 1) * spacing,
            120 + nodeSize / 2 - (board.Nodes.Min(node => node.Row) - 1) * spacing);
        return new(new OwnCharacterContext(new(101, 501, 300, "SyntheticRail") { ClassId = "Cleric" }, 1), board,
            new(0, 0, 2200, 1600), Stopwatch.GetTimestamp(), new(family, origin, spacing, nodeSize, [], 1), "en");
    }
    private static Point Center(Rectangle box) => new(box.X + box.Width / 2, box.Y + box.Height / 2);
    private static nint MousePoint(Point point) => (nint)((uint)(ushort)point.X | (uint)(ushort)point.Y << 16);
    private static int[] Pixels(Bitmap image)
    {
        var values = new int[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (var y = 0; y < image.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, values, y * image.Width, image.Width); }
        finally { image.UnlockBits(data); }
        return values;
    }
    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
