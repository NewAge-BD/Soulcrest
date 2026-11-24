using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayEditorLiveInteractionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RepeatedAltTabPreservesTheChosenLockAndActualClickThrough(bool locked) => RunSta(() =>
    {
        using var fixture = new WindowFixture();
        if (!locked) fixture.Unlock();
        var keyboard = (OverlayKeyboardInput)typeof(OverlayInteraction)
            .GetField("Keyboard", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            for (var repeat = 0; repeat < 5; repeat++)
            {
                keyboard.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, 0);
                Invoke(fixture.Window, "RefreshInteraction");
                AssertLock(fixture.Window, locked);
                fixture.Window.HideFrame();
                Assert.True(Field<bool>(fixture.Window, "_locked")); // Hidden surfaces cannot accept input.
                fixture.Window.ShowFrame(fixture.Image, fixture.Original, false);
                Invoke(fixture.Window, "RefreshInteraction");
                AssertLock(fixture.Window, locked); // Alt may still be held when AION2 becomes foreground.
                keyboard.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak);
                Invoke(fixture.Window, "RefreshInteraction");
                AssertLock(fixture.Window, locked);
            }
        }
        finally { keyboard.ProcessKeyboard(1, OverlayKeyboardInput.LeftAltKey, 0x38, OverlayKeyboardInput.KeyBreak); }
    });

    [Fact]
    public Task IntentionalLockClicksKeepTheLastCommittedIconWhileSavingAndRejectRepeatedClicks() => RunSta(() =>
    {
        using var window = new OverlayEditorLiveWindow();
        using var image = new Bitmap(360, 260);
        var monitor = new OverlayMonitor("synthetic", "Synthetic", new(-16000, -16000, 8000, 8000), new(-16000, -16000, 8000, 8000));
        var instance = new OverlayInstance { Id = "synthetic", Settings = new() };
        var requests = new List<bool>();
        window.LockRequested += requests.Add;
        window.ConfigureInteraction(instance, image.Size, monitor, "aion2-stylized");
        window.ShowFrame(image, new(-14000, -14000, 360, 260), false);
        ClickLock(window);
        Assert.False(Assert.Single(requests));
        AssertLock(window, true);
        ClickLock(window);
        Assert.Single(requests);
        window.CompleteLock("synthetic write failure");
        AssertLock(window, true);
        ClickLock(window);
        Assert.Equal(2, requests.Count);
        instance = instance with { Settings = instance.Settings with { Locked = false } };
        window.ConfigureInteraction(instance, image.Size, monitor, "aion2-stylized");
        Assert.True(Field<bool>(window, "_locked")); // Acknowledgement still pending.
        Assert.False(Field<OverlayLockButton>(window, "_lockButton").Locked);
        window.CompleteLock(null);
        AssertLock(window, false);
        ClickLock(window);
        Assert.True(requests.Last());
        Assert.False(Field<OverlayLockButton>(window, "_lockButton").Locked);
        Assert.True(Field<bool>(window, "_locked")); // Saving suspends input, not the persistent icon.
        instance = instance with { Settings = instance.Settings with { Locked = true } };
        window.ConfigureInteraction(instance, image.Size, monitor, "aion2-stylized");
        window.CompleteLock(null);
        AssertLock(window, true);
    });

    private static void ClickLock(OverlayEditorLiveWindow window)
    {
        var button = Field<OverlayLockButton>(window, "_lockButton");
        var point = new MouseEventArgs(MouseButtons.Left, 1, 12, 12, 0);
        Invoke(button, "OnMouseDown", point);
        Invoke(button, "OnMouseUp", point);
    }

    private static void AssertLock(OverlayEditorLiveWindow window, bool expected)
    {
        Assert.Equal(expected, Field<bool>(window, "_locked"));
        Assert.Equal(expected, Field<OverlayLockButton>(window, "_lockButton").Locked);
        Assert.Equal(expected, (NativeMethods.GetWindowLongPtr(window.Handle, NativeMethods.GWL_EXSTYLE)
            & NativeMethods.WS_EX_TRANSPARENT) != 0);
    }

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
    public Task CaptureLossAndSourceHideCancelWithoutSavingAndSuspendInput() => RunSta(() =>
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
        internal OverlaySettings Settings { get; private set; } = new() { Enabled = true, LockWidget = new() { X = 30.25, Y = 24.5 } };
        internal OverlayInstance Instance { get; private set; }
        internal OverlayMonitor Monitor { get; } = new("synthetic", "Synthetic", new(-16000, -16000, 8000, 8000), new(-16000, -16000, 8000, 8000));
        internal Rectangle Original { get; }
        internal WindowFixture(double scale = 1)
        {
            Instance = new() { Id = "synthetic", Settings = Settings };
            Original = new(-14000, -14000, (int)Math.Ceiling(Image.Width * scale), (int)Math.Ceiling(Image.Height * scale));
            Window.LockRequested += locked =>
            {
                Settings = Settings with { Locked = locked };
                Instance = Instance with { Settings = Settings };
                Window.ConfigureInteraction(Instance, Image.Size, Monitor, "aion2-stylized", scale);
                Window.CompleteLock(null);
            };
            Window.ConfigureInteraction(Instance, Image.Size, Monitor, "aion2-stylized", scale);
            Window.ShowFrame(Image, Original, false);
        }
        internal void Unlock()
        {
            Invoke(Window, "ToggleManualInteraction");
            Assert.False(Settings.Locked);
            AssertLock(Window, false);
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
