using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayInteractionPanelTests : IDisposable
{
    private readonly Dictionary<string, byte[]?> _originalFiles = [];
    private readonly string _originalLanguage = UiText.Language;

    public OverlayInteractionPanelTests()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        foreach (var path in new[]
        {
            AppPaths.SettingsFile, AppPaths.RoutesFile, AppPaths.TargetsFile,
            Path.Combine(AppPaths.DataDirectory, "exploration.json"),
            Path.Combine(AppPaths.DataDirectory, "leveling-progress.json")
        })
        {
            _originalFiles.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            File.Delete(path);
        }
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            OverlayEnabled = false, BossRushEnabled = false, BossAlertsEnabled = false,
            LevelingOverlayEnabled = false, BossOverlayLocked = false, LevelingOverlayLocked = false,
            CheckUpdatesOnStart = false, LootTrackingEnabled = false
        });
    }

    public void Dispose()
    {
        foreach (var (path, contents) in _originalFiles)
        {
            if (contents is null) File.Delete(path);
            else File.WriteAllBytes(path, contents);
        }
        UiText.Language = _originalLanguage;
    }

    [Theory]
    [InlineData("_overlay")]
    [InlineData("_bossOverlay")]
    [InlineData("_levelingOverlay")]
    public Task AltInteractionChangesNativeHitTestingWithoutShowingPanelsOrSavingSettings(string field) => RunOnSta(() =>
    {
        using var main = new MainForm(); // No Show: capture and WebView startup remain inactive.
        var panel = (Form)GetField(main, field)!;
        var settings = main.Services.GetRequiredService<SettingsService>();
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var changes = 0;
        settings.Changed += () => changes++;

        AssertNativeStyle(panel, clickThrough: true);
        Assert.False(panel.Visible);
        SetInteraction(panel, true);
        SetInteraction(panel, true); // Repeated polling must be idempotent.
        AssertNativeStyle(panel, clickThrough: false);
        SetInteraction(panel, false);
        SetInteraction(panel, false);
        AssertNativeStyle(panel, clickThrough: true);

        Assert.False(panel.Visible);
        Assert.False(main.Visible);
        Assert.False(settings.Current.BossOverlayLocked);
        Assert.False(settings.Current.LevelingOverlayLocked);
        Assert.Equal(0, changes);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    });

    [Theory]
    [InlineData("_overlay", "_dragStart")]
    [InlineData("_bossOverlay", "_drag")]
    [InlineData("_levelingOverlay", "_drag")]
    public Task ReleasingAltEndsCapturedGesturesAndSavesOnlyTheFinishedDrag(string field, string dragField) => RunOnSta(() =>
    {
        using var main = new MainForm();
        var panel = (Form)GetField(main, field)!;
        var settings = main.Services.GetRequiredService<SettingsService>();
        var changes = 0;
        settings.Changed += () => changes++;
        _ = panel.Handle;
        SetInteraction(panel, true);
        panel.Location = new Point(321, 234);
        SetField(panel, dragField, new Point(10, 10));
        if (panel is PetOverlayForm)
        {
            var resizing = panel.GetType().GetField("_resizing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            resizing.SetValue(panel, Enum.ToObject(resizing.FieldType, 3));
        }
        if (panel is LevelingOverlayForm)
        {
            SetField(panel, "_pressed", LevelingOverlayAction.Next);
            SetField(panel, "_pressedRouteId", "old-route");
            SetField(panel, "_pressedCharacterId", "old-character");
            SetField(panel, "_pressedProgress", new RouteProgress(4, 10));
        }
        panel.Capture = true;

        SetInteraction(panel, false);
        Assert.Null(GetField(panel, dragField));
        Assert.False(panel.Capture);
        AssertNativeStyle(panel, clickThrough: true);
        if (panel is PetOverlayForm) Assert.Equal(0, Convert.ToInt32(GetField(panel, "_resizing")));
        if (panel is LevelingOverlayForm)
        {
            Assert.Equal(LevelingOverlayAction.None, GetField(panel, "_pressed"));
            Assert.Null(GetField(panel, "_pressedRouteId"));
            Assert.Null(GetField(panel, "_pressedCharacterId"));
            Assert.Null(GetField(panel, "_pressedProgress"));
        }
        Assert.Equal(1, changes);
        SetInteraction(panel, false);
        Assert.Equal(1, changes);
    });

    private static void AssertNativeStyle(Form panel, bool clickThrough)
    {
        var style = NativeMethods.GetWindowLongPtr(panel.Handle, NativeMethods.GWL_EXSTYLE);
        Assert.Equal(clickThrough, (style & NativeMethods.WS_EX_TRANSPARENT) != 0);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_NOACTIVATE);
    }

    [Theory]
    [InlineData("_overlay")]
    [InlineData("_bossOverlay")]
    [InlineData("_levelingOverlay")]
    public Task AltInteractionSurvivesWinFormsStyleRefreshAndHandleRecreation(string field) => RunOnSta(() =>
    {
        using var main = new MainForm();
        var panel = (Form)GetField(main, field)!;
        _ = panel.Handle;
        SetInteraction(panel, true);
        typeof(Control).GetMethod("UpdateStyles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
        AssertNativeStyle(panel, clickThrough: false);
        typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
        AssertNativeStyle(panel, clickThrough: false);
        SetInteraction(panel, false);
        typeof(Control).GetMethod("UpdateStyles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
        AssertNativeStyle(panel, clickThrough: true);
    });

    private static void SetInteraction(Form panel, bool enabled)
    {
        switch (panel)
        {
            case PetOverlayForm pet: pet.SetInteractionEnabled(enabled); break;
            case BossOverlayForm boss: boss.SetInteractionEnabled(enabled); break;
            case LevelingOverlayForm leveling: leveling.SetInteractionEnabled(enabled); break;
            default: throw new InvalidOperationException("Unexpected overlay panel.");
        }
    }

    [Theory]
    [InlineData("_overlay")]
    [InlineData("_bossOverlay")]
    [InlineData("_levelingOverlay")]
    public Task WindowsRoutesPointerToThePanelOnlyWhileInteractionIsEnabled(string field) => RunOnSta(() =>
    {
        if (InteractiveDesktop.Missing) return; // needs real window hit-testing
        using var main = new MainForm();
        var panel = (Form)GetField(main, field)!;
        var area = (Screen.AllScreens.FirstOrDefault(screen => !screen.Primary) ?? Screen.PrimaryScreen!).WorkingArea;
        using var behind = new PassiveTestBackground
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(area.Left + 40, area.Top + 40, 400, 200)
        };
        behind.Show();
        panel.Bounds = behind.Bounds;
        panel.Show();
        panel.Update();
        var point = new Point(panel.Left + 70, panel.Top + 30);
        Assert.Equal(behind.Handle, WindowFromPoint(point));
        SetInteraction(panel, true);
        Assert.Equal(panel.Handle, WindowFromPoint(point));
        Assert.Equal((nint)NativeMethods.MA_NOACTIVATE,
            SendMessage(panel.Handle, NativeMethods.WM_MOUSEACTIVATE, behind.Handle, 0));
        typeof(Control).GetMethod("UpdateStyles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
        Assert.Equal(panel.Handle, WindowFromPoint(point));
        SetInteraction(panel, false);
        Assert.Equal(behind.Handle, WindowFromPoint(point));
    });

    [Theory]
    [InlineData((int)Keys.LMenu)]
    [InlineData((int)Keys.RMenu)]
    [InlineData((int)Keys.Menu)]
    public void EitherAltKeyEnablesInteractionOnlyWhileCurrentlyDown(int key)
    {
        Assert.True(OverlayInteraction.IsAltDown(value => value == key ? unchecked((short)0x8000) : (short)0));
        Assert.False(OverlayInteraction.IsAltDown(value => value == key ? (short)1 : (short)0));
        Assert.False(OverlayInteraction.IsAltDown(_ => 0));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void AStaleRawReleaseCannotOverrideAPressedKey(bool? observed, bool queried, bool held) =>
        Assert.Equal(held, OverlayInteraction.ResolveHeld(observed, queried));

    [Fact]
    public Task RawInputRegistrationFollowsTheRecreatedHostWindow() => RunOnSta(() =>
    {
        using var main = new MainForm();
        SetField(main, "_overlaysStarted", true);
        var original = main.Handle;
        Assert.Equal(original, RegisteredKeyboardTarget());
        typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
        Assert.NotEqual(original, main.Handle);
        Assert.Equal(main.Handle, RegisteredKeyboardTarget());
    });

    private static nint RegisteredKeyboardTarget()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<OverlayRawInputDevice>();
        Assert.Equal(0u, GetRegisteredRawInputDevices(0, ref count, size));
        var buffer = Marshal.AllocHGlobal(checked((int)(count * size)));
        try
        {
            var copied = GetRegisteredRawInputDevices(buffer, ref count, size);
            Assert.NotEqual(uint.MaxValue, copied);
            for (var index = 0; index < copied; index++)
            {
                var device = Marshal.PtrToStructure<OverlayRawInputDevice>(buffer + checked(index * (int)size));
                if (device.UsagePage == 1 && device.Usage == 6) return device.Target;
            }
            throw new InvalidOperationException("No registered keyboard input target.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("user32.dll")]
    private static extern uint GetRegisteredRawInputDevices(nint devices, ref uint count, uint size);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [Fact]
    public Task RawAltEnablesNativeLevelingButtonsAndDragAndReleaseCancelsInput() => RunOnSta(() =>
    {
        RouteFile.Save(AppPaths.RoutesFile, new[]
        {
            new SavedRoute("input-test", "Input test",
                [new RouteStop("altgard", 100, 200, "First", "NPCs", null),
                    new RouteStop("altgard", 200, 300, "Second", "NPCs", null)],
                Category: LevelingRouteStyle.Category)
        });
        var keyboard = (OverlayKeyboardInput)typeof(OverlayInteraction)
            .GetField("Keyboard", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            using var main = new MainForm();
            var targets = main.Services.GetRequiredService<MapTargetsService>();
            var settings = main.Services.GetRequiredService<SettingsService>();
            settings.Current.LevelingOverlayEnabled = true;
            targets.StartRoute("input-test");
            var panel = (LevelingOverlayForm)GetField(main, "_levelingOverlay")!;
            _ = panel.Handle;
            typeof(LevelingOverlayForm).GetMethod("RefreshPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            keyboard.ProcessKeyboard(1, OverlayKeyboardInput.AltKey, 0x38, 0);
            main.SetOverlayInteractionEnabled(OverlayInteraction.IsAltHeld);
            var snapshot = (LevelingOverlaySnapshot)GetField(panel, "_snapshot")!;
            var button = LevelingOverlayForm.ButtonBounds(LevelingOverlayAction.Next, snapshot.Window.Count);
            var click = new Point((int)button.X + 10, (int)button.Y + 10);
            SendMessage(panel.Handle, 0x0201, 1, MousePoint(click));
            SendMessage(panel.Handle, 0x0202, 0, MousePoint(click));
            Assert.Equal(new RouteProgress(1, 2), targets.GetRouteProgress("input-test"));

            var before = panel.Location;
            SendMessage(panel.Handle, 0x0201, 1, MousePoint(new Point(30, 30)));
            SendMessage(panel.Handle, 0x0200, 1, MousePoint(new Point(70, 60)));
            Assert.Equal(new Point(before.X + 40, before.Y + 30), panel.Location);
            keyboard.ProcessKeyboard(1, OverlayKeyboardInput.AltKey, 0x38, OverlayKeyboardInput.KeyBreak);
            main.SetOverlayInteractionEnabled(OverlayInteraction.IsAltHeld);
            Assert.False(panel.Capture);
            Assert.Equal(panel.Left, settings.Current.LevelingOverlayX);
            Assert.Equal(panel.Top, settings.Current.LevelingOverlayY);
            SendMessage(panel.Handle, 0x0201, 1, MousePoint(click));
            SendMessage(panel.Handle, 0x0202, 0, MousePoint(click));
            Assert.Equal(new RouteProgress(1, 2), targets.GetRouteProgress("input-test"));
        }
        finally { keyboard.Stop(); }
    });

    private static nint MousePoint(Point point) => (nint)((point.Y << 16) | (point.X & 0xffff));

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    private static object? GetField(object value, string name) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);

    private static void SetField(object value, string name, object fieldValue) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, fieldValue);

    /// <summary>Own hit-test surface that never activates or takes keyboard focus.</summary>
    private sealed class PassiveTestBackground : Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WS_EX_NOACTIVATE;
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

    private static async Task RunOnSta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
