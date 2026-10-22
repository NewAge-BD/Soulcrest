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

[Collection("Settings file")]
public sealed class DaevanionToggleTests
{
    [Fact]
    public Task WaitingForABoardDoesNotCreateAnInteractiveHandle() => RunOnSta(() =>
    {
        using var button = new DaevanionToggleForm(() => false);
        button.Suppress(); button.Suppress();
        Assert.False(button.IsHandleCreated);
        Assert.False(button.Visible);
    });

    [Theory]
    [InlineData("nezekan", 96)]
    [InlineData("triniel", 144)]
    [InlineData("ariel", 192)]
    [InlineData("azphel", 96)]
    public void ButtonSitsAboveTheCompleteBoardAndDoesNotMoveWhenNodesAreCovered(string family, int dpi)
    {
        var source = Picture(family, 120);
        var bounds = DaevanionToggleLayout.Bounds(source, dpi);
        Assert.True(bounds.Width > 0);
        Assert.Equal((int)Math.Round(112 * dpi / 96d), bounds.Width);
        Assert.Equal((int)Math.Round(28 * dpi / 96d), bounds.Height);
        Assert.True(bounds.Bottom < 120);
        Assert.True(new Rectangle(Point.Empty, source.GameBounds.Size).Contains(bounds));
        Assert.Equal(bounds, DaevanionToggleLayout.Bounds(source with { Detection = source.Detection with { Nodes = [] } }, dpi));
        Assert.Equal(bounds, DaevanionToggleLayout.Bounds(source with { GameBounds = source.GameBounds with { X = 3000, Y = -900 } }, dpi));
    }

    [Fact]
    public void MissingSpaceAboveUsesTheRightSideAndNoSafeSpaceReturnsEmpty()
    {
        var source = Picture("ariel", 10);
        var bounds = DaevanionToggleLayout.Bounds(source);
        var lastNativeX = source.Detection.Origin.X + 14 * source.Detection.Spacing + source.Detection.NodeSize / 2;
        Assert.True(bounds.Left > lastNativeX);
        Assert.True(new Rectangle(Point.Empty, source.GameBounds.Size).Contains(bounds));
        Assert.Equal(Rectangle.Empty, DaevanionToggleLayout.Bounds(source with { GameBounds = new(0, 0, 750, 760) }));
        Assert.Equal(Rectangle.Empty, DaevanionToggleLayout.Bounds(source with { Detection = source.Detection with { Origin = new(double.NaN, 20) } }));
        Assert.Equal(Rectangle.Empty, DaevanionToggleLayout.Bounds(source with { Board = source.Board with { Nodes = [] } }));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(288)]
    public void ProductionButtonUsesPhysicalBoundsAndDistinctLocalizedInteractiveStates(int bitmapDpi)
    {
        var size = new Size(112, 28);
        using var normal = DaevanionToggleRenderer.Render(size, true, "de");
        using var off = DaevanionToggleRenderer.Render(size, false, "de");
        using var english = DaevanionToggleRenderer.Render(size, true, "en");
        using var hover = DaevanionToggleRenderer.Render(size, true, "de", hover: true);
        using var pressed = DaevanionToggleRenderer.Render(size, true, "de", hover: true, pressed: true);
        using var scaledMetadata = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        scaledMetadata.SetResolution(bitmapDpi, bitmapDpi);
        using (var graphics = Graphics.FromImage(scaledMetadata))
            DaevanionToggleRenderer.Paint(graphics, new Rectangle(Point.Empty, size), true, "de");
        Assert.Equal(Pixels(normal), Pixels(scaledMetadata));
        Assert.NotEqual(Pixels(normal), Pixels(off));
        Assert.NotEqual(Pixels(normal), Pixels(english));
        Assert.NotEqual(Pixels(normal), Pixels(hover));
        Assert.NotEqual(Pixels(hover), Pixels(pressed));
        Assert.Equal(0, normal.GetPixel(0, 0).A);
        Assert.Equal(0, normal.GetPixel(111, 27).A);
        Assert.True(normal.GetPixel(50, 14).A > 240);
        Assert.Equal("Node-Ergänzungen ausblenden", DaevanionToggleForm.TooltipText(true, "de"));
        Assert.Equal("Show node additions", DaevanionToggleForm.TooltipText(false, "en"));
        Assert.Equal("Anzeige konnte nicht gespeichert werden.", DaevanionToggleForm.SaveErrorText("de"));
        Assert.Equal("Display preference could not be saved.", DaevanionToggleForm.SaveErrorText("en"));
    }

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task ButtonRemainsClickableWhenNodeWindowIsHiddenWithoutOwningOrActivatingIt() => RunOnSta(() =>
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        using var nodes = new NonActivatingForm { Bounds = new(area.Left + 80, area.Top + 140, 260, 180) };
        using var button = new DaevanionToggleForm(() => true);
        nodes.Show();
        var foreground = GetForegroundWindow();
        var bounds = new Rectangle(area.Left + 180, area.Top + 100, 112, 28);
        button.Present(bounds, true, "en", false);
        Assert.True(button.Visible); Assert.Null(button.Owner);
        nodes.Hide();
        button.Present(bounds, false, "en", false);
        Assert.True(button.Visible); Assert.False(button.ContentVisible);
        var style = NativeMethods.GetWindowLongPtr(button.Handle, NativeMethods.GWL_EXSTYLE);
        Assert.Equal((nint)0, style & NativeMethods.WS_EX_TRANSPARENT);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_NOACTIVATE);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_TOOLWINDOW);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_LAYERED);
        Assert.Equal(button.Handle, WindowFromPoint(button.PointToScreen(new Point(55, 14))));
        Assert.Equal((nint)NativeMethods.MA_NOACTIVATE, SendMessage(button.Handle, NativeMethods.WM_MOUSEACTIVATE, 0, 0));
        Assert.Equal(foreground, GetForegroundWindow());
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task CompleteLeftClickTogglesOnceAndCancelledGesturesNeverToggle() => RunOnSta(() =>
    {
        var allowed = true;
        using var button = new DaevanionToggleForm(() => allowed);
        var area = Screen.PrimaryScreen!.WorkingArea;
        var bounds = new Rectangle(area.Left + 180, area.Top + 100, 112, 28);
        button.Present(bounds, true, "de", false);
        var count = 0; button.Toggled += () => count++;
        var inside = MousePoint(new(55, 14));
        SendMessage(button.Handle, 0x0202, 0, inside);
        Assert.Equal(0, count);
        SendMessage(button.Handle, 0x0201, 1, inside);
        // Periodic fresh identical presentations must not cancel a valid press.
        button.Present(bounds, true, "de", false);
        SendMessage(button.Handle, 0x0202, 0, inside);
        SendMessage(button.Handle, 0x0202, 0, inside);
        Assert.Equal(1, count);
        SendMessage(button.Handle, 0x0201, 1, inside);
        SendMessage(button.Handle, 0x0202, 0, MousePoint(new(-5, -5)));
        Assert.Equal(1, count);
        SendMessage(button.Handle, 0x0201, 1, inside);
        button.Capture = false;
        SendMessage(button.Handle, 0x0202, 0, inside);
        Assert.Equal(1, count);
        SendMessage(button.Handle, 0x0201, 1, inside);
        button.Suppress(); button.Present(bounds, true, "de", false);
        SendMessage(button.Handle, 0x0202, 0, inside);
        Assert.Equal(1, count);
        SendMessage(button.Handle, 0x0201, 1, inside);
        allowed = false;
        SendMessage(button.Handle, 0x0202, 0, inside);
        Assert.Equal(1, count);
        button.Present(bounds, true, "de", false);
        Assert.False(button.Visible);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task MovingOrRecreatingTheButtonCannotCompleteAnOldPress() => RunOnSta(() =>
    {
        using var button = new DaevanionToggleForm(() => true);
        var area = Screen.PrimaryScreen!.WorkingArea;
        var bounds = new Rectangle(area.Left + 180, area.Top + 100, 112, 28);
        button.Present(bounds, true, "en", false);
        var count = 0; button.Toggled += () => count++;
        var inside = MousePoint(new(55, 14));
        SendMessage(button.Handle, 0x0201, 1, inside);
        bounds.Offset(15, 12); button.Present(bounds, true, "en", false);
        SendMessage(button.Handle, 0x0202, 0, inside);
        Assert.Equal(0, count);
        SendMessage(button.Handle, 0x0201, 1, inside);
        typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
        button.Present(bounds, false, "en", true);
        SendMessage(button.Handle, 0x0202, 0, inside);
        Assert.Equal(0, count); Assert.True(button.Visible); Assert.Null(button.Owner);
    });

    private static DaevanionSnapshot Picture(string family, int top)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
        const int size = 48, spacing = 61;
        var origin = new OpenCvSharp.Point2d(100 - (board.Nodes.Min(n => n.Column) - 1) * spacing,
            top + size / 2 - (board.Nodes.Min(n => n.Row) - 1) * spacing);
        var anchors = board.Nodes.Select(n => new DaevanionNodeAnchor(n.Row, n.Column,
            new((int)(origin.X + (n.Column - 1) * spacing - size / 2),
                (int)(origin.Y + (n.Row - 1) * spacing - size / 2), size, size), 1)).ToArray();
        return new(new OwnCharacterContext(new(101, 501, 300, "SyntheticBoard") { ClassId = "Cleric" }, 1),
            board, new(-1920, 0, 1920, 1080), Stopwatch.GetTimestamp(), new(family, origin, spacing, size, anchors, 1), "en");
    }

    private static int[] Pixels(Bitmap image)
    {
        var values = new int[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (var y = 0; y < image.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, values, y * image.Width, image.Width); }
        finally { image.UnlockBits(data); }
        return values;
    }
    private static nint MousePoint(Point point) => (nint)((uint)(ushort)point.X | (uint)(ushort)point.Y << 16);
    private static Task RunOnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private sealed class NonActivatingForm : Form { protected override bool ShowWithoutActivation => true; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
