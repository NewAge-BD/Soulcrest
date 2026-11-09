using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorLiveInteractionTests
{
    [Fact]
    public Task LiveGestureKeepsFreshFramesAtItsPhysicalBoundsAndCommitsOnlyOnce() => RunSta(() =>
    {
        using var fixture = new WindowFixture();
        fixture.Unlock();
        var results = new List<OverlayLivePlacement>();
        fixture.Window.PlacementCompleted += results.Add;
        fixture.Mouse("OnMouseDown", MouseButtons.Left, 80, 80);
        fixture.Mouse("OnMouseMove", MouseButtons.Left, 112, 104);
        var moved = fixture.Window.Bounds;
        Assert.Equal(fixture.Original.X + 32, moved.X);
        Assert.Equal(fixture.Original.Y + 24, moved.Y);
        fixture.Window.ShowFrame(fixture.Image, fixture.Original, false);
        Assert.Equal(moved, fixture.Window.Bounds);
        fixture.Mouse("OnMouseUp", MouseButtons.Left, 112, 104);
        fixture.Mouse("OnMouseUp", MouseButtons.Left, 112, 104);
        var result = Assert.Single(results);
        Assert.Same(fixture.Settings, result.StartedSettings);
        Assert.Equal(moved, result.Bounds);
        Assert.Equal(1, result.Scale);
        fixture.Window.ShowFrame(fixture.Image, fixture.Original, false);
        Assert.Equal(moved, fixture.Window.Bounds); // Save is still pending.
        fixture.Window.CompletePlacement(null);
    });

    [Fact]
    public Task CaptureLossAndSourceHideCancelWithoutSavingAndHideAlwaysRelocks() => RunSta(() =>
    {
        using var fixture = new WindowFixture();
        fixture.Unlock();
        var results = new List<OverlayLivePlacement>();
        fixture.Window.PlacementCompleted += results.Add;
        fixture.Mouse("OnMouseDown", MouseButtons.Left, 80, 80);
        fixture.Mouse("OnMouseMove", MouseButtons.Left, 90, 90);
        fixture.Window.Capture = false;
        Invoke(fixture.Window, "OnMouseCaptureChanged", EventArgs.Empty);
        Assert.Equal(fixture.Original, fixture.Window.Bounds);
        Assert.Empty(results);
        fixture.Mouse("OnMouseDown", MouseButtons.Left, 80, 80);
        fixture.Mouse("OnMouseMove", MouseButtons.Left, 100, 90);
        fixture.Window.HideFrame();
        Assert.False(fixture.Window.Visible);
        Assert.True(Field<bool>(fixture.Window, "_locked"));
        Assert.False(Field<OverlayLockButton>(fixture.Window, "_lockButton").Visible);
        Assert.Empty(results);
    });

    [Fact]
    public Task DpiChangeDuringGesturePreservesPhysicalBoundsUntilPlacementCommit() => RunSta(() =>
    {
        using var fixture = new WindowFixture();
        fixture.Unlock();
        var invalidations = 0;
        fixture.Window.GeometryInvalidated += () => invalidations++;
        fixture.Mouse("OnMouseDown", MouseButtons.Left, 80, 80);
        fixture.Mouse("OnMouseMove", MouseButtons.Left, 112, 104);
        var moved = fixture.Window.Bounds;
        var memory = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(new[] { moved.X, moved.Y, moved.Right + 180, moved.Bottom + 130 }, 0, memory, 4);
            var message = Message.Create(fixture.Window.Handle, 0x02e0, (nint)((144 << 16) | 144), memory);
            var constructor = typeof(DpiChangedEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var change = (DpiChangedEventArgs)constructor.Invoke([96, message]);
            Invoke(fixture.Window, "OnDpiChanged", change);
            Assert.True(change.Cancel);
            Assert.Equal(moved, fixture.Window.Bounds);
            Assert.True(fixture.Window.Visible);
            Assert.Equal(0, invalidations);
        }
        finally { Marshal.FreeHGlobal(memory); }
        var results = new List<OverlayLivePlacement>();
        fixture.Window.PlacementCompleted += results.Add;
        fixture.Mouse("OnMouseUp", MouseButtons.Left, 112, 104);
        Assert.Equal(moved, Assert.Single(results).Bounds);
        fixture.Window.CompletePlacement(null);
    });

    [Fact]
    public Task CornerResizeUsesOneUniformScaleAndRightClickCancelsOnlyItsGesture() => RunSta(() =>
    {
        using var fixture = new WindowFixture();
        fixture.Unlock();
        var results = new List<OverlayLivePlacement>();
        fixture.Window.PlacementCompleted += results.Add;
        fixture.Mouse("OnMouseDown", MouseButtons.Left, 359, 259);
        fixture.Mouse("OnMouseMove", MouseButtons.Left, 395, 285);
        var resized = fixture.Window.Bounds;
        Assert.True(resized.Width > fixture.Original.Width);
        var scale = Field<double>(fixture.Window, "_scale");
        Assert.Equal(OverlayEditorPlacementGeometry.ScaledSize(fixture.Image.Size, scale), resized.Size);
        fixture.Mouse("OnMouseDown", MouseButtons.Right, 100, 100);
        Assert.Equal(fixture.Original, fixture.Window.Bounds);
        Assert.False(Field<bool>(fixture.Window, "_locked"));
        Assert.Empty(results);
    });

    [Fact]
    public Task SaveFailureRestoresBoundsAndCannotBeClearedByAPlainFreshFrame() => RunSta(() =>
    {
        using var fixture = new WindowFixture();
        fixture.Unlock();
        fixture.Window.PlacementCompleted += _ => { };
        fixture.Mouse("OnMouseDown", MouseButtons.Left, 80, 80);
        fixture.Mouse("OnMouseMove", MouseButtons.Left, 120, 100);
        fixture.Mouse("OnMouseUp", MouseButtons.Left, 120, 100);
        fixture.Window.CompletePlacement("synthetic write failure");
        Assert.Equal(fixture.Original, fixture.Window.Bounds);
        Assert.True(Field<bool>(fixture.Window, "_blocked"));
        Assert.True(Field<bool>(fixture.Window, "_locked"));
        fixture.Window.ConfigureInteraction(fixture.Instance, fixture.Image.Size, fixture.Monitor, "aion2-stylized", 1);
        fixture.Window.ShowFrame(fixture.Image, fixture.Original, false);
        Invoke(fixture.Window, "ToggleManualInteraction");
        Assert.True(Field<bool>(fixture.Window, "_blocked"));
        Assert.True(Field<bool>(fixture.Window, "_locked"));
        fixture.Window.ConfigureInteraction(fixture.Instance with { Settings = fixture.Settings with { } },
            fixture.Image.Size, fixture.Monitor, "aion2-stylized", 1);
        Assert.False(Field<bool>(fixture.Window, "_blocked"));
    });

    [Fact]
    public Task AcknowledgedPlacementUpdatesItsGestureContextBeforeTheNextBitmap() => RunSta(() =>
    {
        using var fixture = new WindowFixture();
        fixture.Unlock();
        var results = new List<OverlayLivePlacement>();
        fixture.Window.PlacementCompleted += results.Add;
        fixture.Mouse("OnMouseDown", MouseButtons.Left, 80, 80);
        fixture.Mouse("OnMouseMove", MouseButtons.Left, 112, 104);
        fixture.Mouse("OnMouseUp", MouseButtons.Left, 112, 104);
        Assert.Single(results);
        var committedSettings = fixture.Settings with { PositionMonitorId = fixture.Monitor.Id, PositionX = .25, PositionY = .25, Scale = 1.25 };
        var committed = fixture.Instance with { Settings = committedSettings };
        fixture.Window.ConfigureInteraction(committed, fixture.Image.Size, fixture.Monitor, "aion2-stylized", 1.25);
        fixture.Window.CompletePlacement(null);
        Assert.False(Field<bool>(fixture.Window, "_locked"));
        Assert.Equal(1.25, Field<double>(fixture.Window, "_scale"));
        Assert.Equal(OverlayEditorPlacementGeometry.ScaledSize(fixture.Image.Size, 1.25), fixture.Window.Size);
        fixture.Mouse("OnMouseDown", MouseButtons.Left, 80, 80);
        fixture.Mouse("OnMouseMove", MouseButtons.Left, 92, 88);
        fixture.Mouse("OnMouseUp", MouseButtons.Left, 92, 88);
        Assert.Equal(2, results.Count);
        Assert.Same(committedSettings, results[1].StartedSettings);
        Assert.Equal(1.25, results[1].Scale);
        Assert.False(Field<bool>(fixture.Window, "_blocked"));
        fixture.Window.CompletePlacement(null);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public Task LockHotspotUsesTheActualClientScaleAndMandatoryPosition(double scale) => RunSta(() =>
    {
        using var fixture = new WindowFixture(scale);
        var logical = OverlayLockWidget.Bounds(fixture.Settings);
        var actual = (Rectangle)Invoke(fixture.Window, "LockBounds")!;
        var expected = Rectangle.FromLTRB((int)Math.Floor(logical.Left * fixture.Window.ClientSize.Width / fixture.Image.Width),
            (int)Math.Floor(logical.Top * fixture.Window.ClientSize.Height / fixture.Image.Height),
            (int)Math.Ceiling(logical.Right * fixture.Window.ClientSize.Width / fixture.Image.Width),
            (int)Math.Ceiling(logical.Bottom * fixture.Window.ClientSize.Height / fixture.Image.Height));
        Assert.Equal(expected, actual);
        Assert.True(fixture.Window.ClientRectangle.Contains(actual));
    });

    [Theory]
    [InlineData("aion2-stylized")]
    [InlineData("aion2-neutral")]
    [InlineData("obsidian")]
    [InlineData("aurora")]
    [InlineData("runic")]
    [InlineData("soulcrest-legacy")]
    public void CanvasLockUriUsesTheOriginalNativeGlyphForBothStates(string themeId)
    {
        foreach (var locked in new[] { false, true })
        {
            var uri = OverlayLockGlyph.GetDataUri(themeId, locked);
            using var stream = new MemoryStream(Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]));
            using var preview = new Bitmap(stream);
            using var expected = OverlayLockButton.RenderGlyph(new(28, 28), OverlayThemeDefinition.Resolve(themeId), locked);
            Assert.Equal(expected.Size, preview.Size);
            for (var y = 0; y < 28; y++) for (var x = 0; x < 28; x++) Assert.Equal(expected.GetPixel(x, y), preview.GetPixel(x, y));
            Assert.Equal(uri, OverlayLockGlyph.GetDataUri(themeId, locked));
        }
    }

    private sealed class WindowFixture : IDisposable
    {
        internal OverlayEditorLiveWindow Window { get; } = new();
        internal Bitmap Image { get; } = new(360, 260);
        internal OverlaySettings Settings { get; } = new() { Enabled = true, LockWidget = new() { X = 30.25, Y = 24.5 } };
        internal OverlayInstance Instance { get; }
        internal OverlayMonitor Monitor { get; } = new("synthetic", "Synthetic", new(-16000, -16000, 8000, 8000), new(-16000, -16000, 8000, 8000));
        internal Rectangle Original { get; }
        internal WindowFixture(double scale = 1)
        {
            Instance = new() { Id = "synthetic", Settings = Settings };
            Original = new(-14000, -14000, (int)Math.Ceiling(Image.Width * scale), (int)Math.Ceiling(Image.Height * scale));
            Window.ConfigureInteraction(Instance, Image.Size, Monitor, "aion2-stylized", scale);
            Window.ShowFrame(Image, Original, false);
        }
        internal void Unlock()
        {
            typeof(OverlayEditorLiveWindow).GetField("_manualInteraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, true);
            Invoke(Window, "SetLocked", false);
        }
        internal void Mouse(string method, MouseButtons buttons, int x, int y) => Invoke(Window, method, new MouseEventArgs(buttons, 1, x, y, 0));
        public void Dispose() { Window.Dispose(); Image.Dispose(); }
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, arguments);
    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
