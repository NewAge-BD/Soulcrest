using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayThemeNativePresentationTests
{
    [Fact]
    public Task PresenterRoundtripKeepsTheSameHiddenWindowOwnerInputStylesAndCaptureAffinity() => RunSta(() =>
    {
        using var owner = new Form();
        using var target = new NativeSurface { Bounds = new(30, 40, 60, 50), Owner = owner, Opacity = .94 };
        var handle = target.Handle;
        var foreground = GetForegroundWindow();
        NativeMethods.SetClickThrough(handle, true);
        var protectedCapture = NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        using var image = new Bitmap(60, 50, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.FromArgb(204, 23, 27, 31));
            using var ink = new SolidBrush(Color.White);
            graphics.FillRectangle(ink, 10, 10, 20, 20);
        }
        Assert.Equal(204, image.GetPixel(3, 3).A);
        Assert.Equal(255, image.GetPixel(20, 20).A);
        foreach (var perPixel in new[] { true, false, true })
        {
            LayeredOverlayPresenter.Configure(target, perPixel, .94);
            Assert.Equal(handle, target.Handle);
            Assert.Same(owner, target.Owner);
            Assert.False(target.Visible);
            Assert.Equal(perPixel ? 1 : .94, target.Opacity, 5);
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE);
            Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_LAYERED);
            Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_TRANSPARENT);
            Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_NOACTIVATE);
            Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_TOOLWINDOW);
            if (protectedCapture)
            {
                Assert.True(GetWindowDisplayAffinity(handle, out var affinity));
                Assert.Equal(NativeMethods.WDA_EXCLUDEFROMCAPTURE, affinity);
            }
            if (perPixel) Assert.True(LayeredOverlayPresenter.Present(target, image));
        }
        NativeMethods.SetClickThrough(handle, false);
        LayeredOverlayPresenter.Configure(target, false, .94);
        Assert.Equal((nint)0, NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TRANSPARENT);
        Assert.Equal(foreground, GetForegroundWindow());
    });

    [Fact]
    public Task HiddenLockRoundtripRestoresLegacyShapeAndCancelsPressedInputWithoutChangingTheTarget() => RunSta(() =>
    {
        using var target = new NativeSurface { Bounds = new(30, 40, 360, 300) };
        var targetHandle = target.Handle;
        using var button = new OverlayLockButton(target) { Size = new(28, 28) };
        _ = button.Handle;
        Assert.NotNull(button.Region);
        button.SetLocked(false);
        var toggles = 0;
        button.Toggled += () => toggles++;
        foreach (var theme in OverlayThemeDefinition.All.Concat([OverlayThemeDefinition.Aion2]))
        {
            typeof(OverlayLockButton).GetField("_pressed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(button, true);
            button.SetTheme(theme);
            Assert.Equal(targetHandle, target.Handle);
            Assert.False(target.Visible);
            Assert.False(button.Visible);
            Assert.False(button.Locked);
            Assert.False((bool)typeof(OverlayLockButton).GetField("_pressed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(button)!);
            Assert.False(button.Capture);
            Assert.Equal(0, toggles);
            var style = NativeMethods.GetWindowLongPtr(button.Handle, NativeMethods.GWL_EXSTYLE);
            Assert.Equal((nint)0, style & NativeMethods.WS_EX_TRANSPARENT);
            Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_NOACTIVATE);
            if (theme.IsLegacy)
            {
                Assert.NotNull(button.Region);
                Assert.False(button.Region!.IsVisible(0, 0));
                Assert.True(button.Region.IsVisible(14, 14));
            }
            else
            {
                Assert.Null(button.Region);
                Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_LAYERED);
                using var image = OverlayLockButton.RenderThemedGlyph(button.Size, theme, false, false, false);
                Assert.InRange(image.GetPixel(6, 14).A, theme.Surface.A - 2, theme.Surface.A + 2);
                Assert.True(LayeredOverlayPresenter.Present(button, image));
            }
        }
    });

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(28, 28)]
    public Task Aion2InitialHandleCreationAndTinyBoundsRecoverOnAnOffscreenVisibleTarget(int width, int height) => RunSta(() =>
    {
        using var target = new NativeSurface { Bounds = new(-30000, -30000, 360, 300) };
        target.Show();
        var targetHandle = target.Handle;
        var relative = new Rectangle(2, 2, width, height);
        using var button = new OverlayLockButton(target, () => relative);
        button.SetTheme(OverlayThemeDefinition.Aion2);
        _ = button.Handle;
        Assert.False((bool)typeof(OverlayLockButton).GetField("_creatingHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(button)!);
        relative = new Rectangle(4, 4, 28, 28);
        button.RefreshTarget();
        Assert.True(button.Visible);
        Assert.Equal(new Size(28, 28), button.ClientSize);
        Assert.Equal(targetHandle, target.Handle);
        Assert.Same(target, button.Owner);
        relative = new Rectangle(4, 4, 0, 0);
        button.RefreshTarget();
        Assert.False(button.Visible);
        relative = new Rectangle(4, 4, 2, 2);
        button.RefreshTarget();
        Assert.True(button.Visible);
        Assert.Equal(new Size(2, 2), button.ClientSize);
    });

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 28)]
    [InlineData(28, -1)]
    [InlineData(int.MaxValue, 28)]
    [InlineData(28, int.MaxValue)]
    public void TransientInvalidOrHugeGlyphDimensionsAreRejectedBeforeAllocation(int width, int height)
    {
        var size = new Size(width, height);
        Assert.False(OverlayLockButton.IsRenderableGlyphSize(size));
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayLockButton.RenderThemedGlyph(size,
            OverlayThemeDefinition.Aion2, true, false, false));
    }

    private sealed class NativeSurface : Form
    {
        public NativeSurface()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
                return parameters;
            }
        }
    }

    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
}
