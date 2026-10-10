using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class WorldMapInteractionTests : IDisposable
{
    private readonly Dictionary<string, byte[]?> _originalFiles = [];
    private readonly string _originalLanguage = UiText.Language;

    public WorldMapInteractionTests()
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
            LevelingOverlayEnabled = false, CheckUpdatesOnStart = false, LootTrackingEnabled = false
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

    [Fact]
    public Task AltNeverEnablesWorldMapInputAndNativeStyleRefreshKeepsItClickThrough() => RunOnSta(() =>
    {
        using var host = new MainForm(); // No Show: capture and WebView startup remain inactive.
        var overlay = RouteOverlay(host);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        foreach (var altHeld in new[] { false, true, true, false })
        {
            host.SetOverlayInteractionEnabled(altHeld);
            AssertPassive(overlay);
            typeof(Control).GetMethod("UpdateStyles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, null);
            AssertPassive(overlay);
            typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, null);
            AssertPassive(overlay);
        }
        Assert.False(overlay.Visible);
        Assert.Empty(overlay.OwnedForms);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    });

    [Fact]
    public Task DrawnWorldMapMarkerRemainsClickThroughWhileOtherPanelsAreInteractive() => RunOnSta(() =>
    {
        using var host = new MainForm();
        var overlay = RouteOverlay(host);
        var area = (Screen.AllScreens.FirstOrDefault(screen => !screen.Primary) ?? Screen.PrimaryScreen!).WorkingArea;
        using var behind = new PassiveTestBackground
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(area.Left + 40, area.Top + 40, 600, 400)
        };
        behind.Show();
        overlay.Bounds = behind.Bounds;
        using var frame = RouteOverlayForm.RenderFrame(Placement(behind.Bounds, worldMap: true), [(Target(), 0)], []);
        overlay.Show(behind);
        typeof(RouteOverlayForm).GetMethod("Present", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, [frame]);
        overlay.Update();
        var marker = new Point(overlay.Left + 112, overlay.Top + 100);
        Assert.Equal(behind.Handle, WindowFromPoint(marker));
        host.SetOverlayInteractionEnabled(true);
        Assert.Equal(behind.Handle, WindowFromPoint(marker));
        Assert.Empty(overlay.OwnedForms);
        AssertPassive(overlay);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PassiveMinimapAndWorldMapContinueDrawingTargetsPetsAndResources(bool worldMap)
    {
        using var image = RouteOverlayForm.Draw(Placement(new Rectangle(0, 0, 600, 400), worldMap), [(Target(), 0)],
            [new RouteOverlayForm.PetSymbol(200, 100, null, "Pet", false)],
            [new RouteOverlayForm.ResourceSymbol(300, 100, null)]);
        Assert.True(image.GetPixel(112, 100).A > 0); // marked target ring
        Assert.True(image.GetPixel(213, 100).A > 0); // pet ring
        Assert.True(image.GetPixel(300, 100).A > 0); // resource
    }

    private static MapTarget Target() => new("route-stop", "altgard", 100, 100, "", "Waypoint", null,
        Leveling: true, Color: LevelingRouteStyle.MainQuest);

    private static MapPlacement Placement(Rectangle area, bool worldMap) => new("altgard",
        new MapFix(1, 0, 0, 0, 1, 0, 50, 60, .3, false), 1, area, new OpenCvSharp.Point2d(40, 40), WorldMap: worldMap);

    private static RouteOverlayForm RouteOverlay(MainForm host) => (RouteOverlayForm)typeof(MainForm)
        .GetField("_routeOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private static void AssertPassive(RouteOverlayForm overlay)
    {
        var style = NativeMethods.GetWindowLongPtr(overlay.Handle, NativeMethods.GWL_EXSTYLE);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_TRANSPARENT);
        Assert.NotEqual((nint)0, style & NativeMethods.WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

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
