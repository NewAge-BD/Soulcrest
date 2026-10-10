using System.Reflection;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayLockButtonTests
{
    [Fact]
    public Task NativeHotspotRemainsClickableAboveAClickThroughTarget() => RunOnSta(() =>
    {
        if (InteractiveDesktop.Missing) return; // needs real window hit-testing
        using var behind = CreateBehind();
        using var target = CreateTarget(behind.Bounds);
        using var button = new OverlayLockButton(target);
        behind.Show();
        target.Show();
        button.Update();

        Assert.True(button.Visible);
        Assert.Same(target, button.Owner);
        Assert.NotEqual(target.Handle, button.Handle);
        Assert.NotEqual((nint)0, NativeMethods.GetWindowLongPtr(target.Handle, NativeMethods.GWL_EXSTYLE)
            & NativeMethods.WS_EX_TRANSPARENT);
        AssertButtonStyle(button);
        Assert.Equal(button.Handle, WindowFromPoint(Center(button)));
        Assert.Equal(behind.Handle, WindowFromPoint(target.PointToScreen(new Point(30, 60))));

        var foreground = GetForegroundWindow();
        Assert.Equal((nint)NativeMethods.MA_NOACTIVATE,
            SendMessage(button.Handle, NativeMethods.WM_MOUSEACTIVATE, target.Handle, 0));
        Assert.Equal(foreground, GetForegroundWindow());
    });

    [Fact]
    public Task TargetVisibilityControlsTheHotspotWithoutChangingItsLockState() => RunOnSta(() =>
    {
        using var target = CreateTarget(CreateBounds());
        using var button = new OverlayLockButton(target);
        Assert.False(button.Visible);
        Assert.False(button.IsHandleCreated);
        button.SetLocked(false);
        target.Show();
        Assert.True(button.Visible);
        Assert.False(button.Locked);
        target.Hide();
        Assert.False(button.Visible);
        target.Show();
        Assert.True(button.Visible);
        Assert.False(button.Locked);
    });

    [Fact]
    public Task HotspotFollowsTargetMovementAndSizeUsingItsDpi() => RunOnSta(() =>
    {
        using var target = CreateTarget(CreateBounds());
        using var button = new OverlayLockButton(target);
        target.Show();
        var size = (int)Math.Round(28 * target.DeviceDpi / 96d);
        var inset = (int)Math.Round(8 * target.DeviceDpi / 96d);
        Assert.Equal(new Size(size, size), button.Size);
        Assert.Equal(target.PointToScreen(new Point(target.ClientSize.Width - size - inset, inset)), button.Location);
        var before = button.Location;
        target.Location = new Point(target.Left + 11, target.Top + 13);
        Assert.Equal(new Point(before.X + 11, before.Y + 13), button.Location);
        before = button.Location;
        target.Width += 64;
        Assert.Equal(new Point(before.X + 64, before.Y), button.Location);
    });

    [Fact]
    public Task OptionalBoundsProviderUsesTheTargetClientArea() => RunOnSta(() =>
    {
        using var target = CreateTarget(CreateBounds());
        var relative = new Rectangle(14, 16, 32, 26);
        using var button = new OverlayLockButton(target, () => relative);
        target.Show();
        Assert.Equal(new Rectangle(target.PointToScreen(relative.Location), relative.Size), button.Bounds);
        relative = new Rectangle(20, 24, 30, 30);
        button.RefreshTarget();
        Assert.Equal(new Rectangle(target.PointToScreen(relative.Location), relative.Size), button.Bounds);
    });

    [Fact]
    public Task TargetAndHotspotHandleRecreationPreserveNativeClickability() => RunOnSta(() =>
    {
        using var target = CreateTarget(CreateBounds());
        using var button = new OverlayLockButton(target);
        target.Show();
        button.SetLocked(false);
        var oldTarget = target.Handle;
        RecreateHandle(target);
        Assert.NotEqual(oldTarget, target.Handle);
        Assert.True(button.Visible);
        Assert.True(button.IsHandleCreated);
        Assert.Same(target, button.Owner);
        Assert.False(button.Locked);
        AssertButtonStyle(button);
        Assert.Equal(button.Handle, WindowFromPoint(Center(button)));

        var oldButton = button.Handle;
        RecreateHandle(button);
        Assert.NotEqual(oldButton, button.Handle);
        Assert.True(button.Visible);
        AssertButtonStyle(button);
        Assert.Equal(button.Handle, WindowFromPoint(Center(button)));
    });

    [Fact]
    public Task ACompleteLeftClickTogglesExactlyOnceWithoutAnyModifier() => RunOnSta(() =>
    {
        using var target = CreateTarget(CreateBounds());
        using var button = new OverlayLockButton(target);
        target.Show();
        var toggles = 0;
        button.Toggled += () => { toggles++; button.SetLocked(!button.Locked); };
        var inside = new Point(button.ClientSize.Width / 2, button.ClientSize.Height / 2);
        SendMessage(button.Handle, 0x0201, 1, MousePoint(inside));
        SendMessage(button.Handle, 0x0202, 0, MousePoint(inside));
        Assert.Equal(1, toggles);
        Assert.False(button.Locked);
        SendMessage(button.Handle, 0x0202, 0, MousePoint(inside));
        SendMessage(button.Handle, 0x0204, 2, MousePoint(inside));
        SendMessage(button.Handle, 0x0205, 0, MousePoint(inside));
        Assert.Equal(1, toggles);
        SendMessage(button.Handle, 0x0201, 1, MousePoint(inside));
        SendMessage(button.Handle, 0x0202, 0, MousePoint(new Point(-2, -2)));
        Assert.Equal(1, toggles);
        Assert.False(button.Capture);
    });

    [Fact]
    public Task HidingTargetCancelsAPressedButtonAndPreventsADelayedToggle() => RunOnSta(() =>
    {
        using var target = CreateTarget(CreateBounds());
        using var button = new OverlayLockButton(target);
        target.Show();
        var toggles = 0;
        button.Toggled += () => toggles++;
        var inside = new Point(button.ClientSize.Width / 2, button.ClientSize.Height / 2);
        SendMessage(button.Handle, 0x0201, 1, MousePoint(inside));
        Assert.True(button.Capture);
        target.Hide();
        Assert.False(button.Visible);
        Assert.False(button.Capture);
        target.Show();
        SendMessage(button.Handle, 0x0202, 0, MousePoint(inside));
        Assert.Equal(0, toggles);
    });

    [Fact]
    public Task DisposingTargetAlsoDisposesItsNativeHotspot() => RunOnSta(() =>
    {
        using var target = CreateTarget(CreateBounds());
        using var button = new OverlayLockButton(target);
        target.Show();
        var handle = button.Handle;
        Assert.True(IsWindow(handle));
        target.Dispose();
        Assert.True(button.IsDisposed);
        Assert.False(IsWindow(handle));
    });

    [Fact]
    public Task DisposingHotspotLeavesItsTargetUsableAndClickThrough() => RunOnSta(() =>
    {
        if (InteractiveDesktop.Missing) return; // needs real window hit-testing
        using var behind = CreateBehind();
        using var target = CreateTarget(behind.Bounds);
        using var button = new OverlayLockButton(target);
        behind.Show();
        target.Show();
        var location = Center(button);
        button.Dispose();
        target.Location = new Point(target.Left + 1, target.Top + 1);
        Assert.False(target.IsDisposed);
        Assert.True(target.Visible);
        Assert.Equal(behind.Handle, WindowFromPoint(location));
    });

    [Fact]
    public void LockTooltipsAreLocalized()
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = "de";
            Assert.Equal("Overlay entsperren", OverlayLockButton.TooltipText(true));
            Assert.Equal("Overlay sperren", OverlayLockButton.TooltipText(false));
            UiText.Language = "en";
            Assert.Equal("Unlock overlay", OverlayLockButton.TooltipText(true));
            Assert.Equal("Lock overlay", OverlayLockButton.TooltipText(false));
        }
        finally { UiText.Language = previous; }
    }

    private static NoActivateForm CreateBehind() => new()
    {
        FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
        StartPosition = FormStartPosition.Manual, Bounds = CreateBounds()
    };

    private static ClickThroughTarget CreateTarget(Rectangle bounds) => new()
    {
        FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
        StartPosition = FormStartPosition.Manual, Bounds = bounds, Opacity = .96
    };

    private static Rectangle CreateBounds()
    {
        var area = (Screen.AllScreens.FirstOrDefault(screen => !screen.Primary) ?? Screen.PrimaryScreen!).WorkingArea;
        return new Rectangle(area.Left + 60, area.Top + 80, 400, 220);
    }

    private sealed class NoActivateForm : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                message.Result = NativeMethods.MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref message);
        }
    }

    private sealed class ClickThroughTarget : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT
                    | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                return parameters;
            }
        }
    }

    private static void AssertButtonStyle(OverlayLockButton button)
    {
        var style = NativeMethods.GetWindowLongPtr(button.Handle, NativeMethods.GWL_EXSTYLE);
        Assert.Equal((nint)0, style & NativeMethods.WS_EX_TRANSPARENT);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_NOACTIVATE);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_TOOLWINDOW);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_TOPMOST);
    }

    private static void RecreateHandle(Control control) => typeof(Control)
        .GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, null);

    private static Point Center(Form form) => form.PointToScreen(new Point(form.ClientSize.Width / 2, form.ClientSize.Height / 2));
    private static nint MousePoint(Point point) => (nint)((uint)(ushort)point.X | (uint)(ushort)point.Y << 16);

    private static Task RunOnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
}
