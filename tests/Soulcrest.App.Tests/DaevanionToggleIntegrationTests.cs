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
public sealed class DaevanionToggleIntegrationTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void ProductionOffPreviewKeepsOnlyNativeBoardPixelsBelowItsIndependentSwitch(string language)
    {
        using var renderer = new DaevanionOverlayRenderer();
        using var on = renderer.Preview(family: "ariel", language: language, nodeContentVisible: true, showToggle: true);
        using var off = renderer.Preview(family: "ariel", language: language, nodeContentVisible: false, showToggle: true);
        using var native = renderer.Preview(family: "ariel", language: language, nodeContentVisible: false);
        Assert.Equal(on.Size, off.Size);
        Assert.Equal(native.Height + 52, off.Height);
        Assert.Equal(native.Width, off.Width);
        using var offBoard = off.Clone(new Rectangle(0, 52, native.Width, native.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var originalPixels = Pixels(native); var hiddenPixels = Pixels(offBoard); var maximumDelta = 0;
        for (var i = 0; i < originalPixels.Length; i++)
        for (var shift = 0; shift < 32; shift += 8)
            maximumDelta = Math.Max(maximumDelta, Math.Abs((originalPixels[i] >> shift & 255) - (hiddenPixels[i] >> shift & 255)));
        // Translating fractional native sprite coordinates can round interpolation by a channel step.
        Assert.InRange(maximumDelta, 0, 2);
        Assert.NotEqual(Pixels(on), Pixels(off));
        var background = Color.FromArgb(255, 26, 33, 35).ToArgb();
        Assert.Contains(Enumerable.Range(0, 52), y => Enumerable.Range(0, off.Width).Any(x => off.GetPixel(x, y).ToArgb() != background));
    }

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task SavedOffStartsWithAClickableSwitchAndBothDirectionsKeepTheCurrentSnapshot() => OnSta(() =>
    {
        using var f = new Fixture();
        var snapshot = f.Publish();
        f.Form.Present();
        Assert.False(f.Form.Visible);
        Assert.True(f.Form.ToggleWindow.Visible);
        Assert.False(f.Form.ToggleWindow.ContentVisible);
        Assert.Null(f.Form.ToggleWindow.Owner);
        Click(f.Form.ToggleWindow);
        Assert.True(f.Settings.Current.DaevanionNodeContentVisible);
        Assert.True(f.Form.Visible);
        Assert.True(f.Form.ToggleWindow.Visible);
        Assert.Same(snapshot, f.Service.Snapshot);
        Click(f.Form.ToggleWindow);
        Assert.False(f.Settings.Current.DaevanionNodeContentVisible);
        Assert.False(f.Form.Visible);
        Assert.True(f.Form.ToggleWindow.Visible);
        Assert.Same(snapshot, f.Service.Snapshot);
        Assert.False(new SettingsService().Current.DaevanionNodeContentVisible);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresTheory(TestRequirement.InteractiveDesktop)]
    [InlineData("foreground")]
    [InlineData("placement")]
    [InlineData("master")]
    [InlineData("context")]
    [InlineData("window")]
    [InlineData("expired")]
    public Task HiddenContentStillHidesItsSwitchOnSuppressionOrInvalidEvidence(string change) => OnSta(() =>
    {
        using var f = new Fixture(); f.Publish(); f.Form.Present();
        Assert.True(f.Form.ToggleWindow.Visible);
        switch (change)
        {
            case "foreground": f.Form.SetForegroundSuppressed(true); break;
            case "placement": f.Form.SetPlacementHidden(true); break;
            case "master": f.Settings.Update(s => s.DaevanionOverlayEnabled = false); break;
            case "context": f.Network.ResetProgressContext(); break;
            case "window": f.GameBounds = null; break;
            case "expired": Thread.Sleep(1100); break;
        }
        f.Form.CheckVisibility();
        Assert.False(f.Form.ToggleWindow.Visible);
        Assert.False(f.Form.Visible);
        Assert.False(f.Settings.Current.DaevanionNodeContentVisible);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task PressCannotToggleAfterBoardContextChangesEvenWhenTheButtonDoesNotMove() => OnSta(() =>
    {
        using var f = new Fixture(); var initial = f.Publish(); f.Form.Present();
        var button = f.Form.ToggleWindow;
        var originalBounds = button.Bounds;
        Mouse(button, "OnMouseDown");
        f.Network.ObserveIdentity("own", Fixture.Identity with { CharacterId = 102 }, DateTimeOffset.UnixEpoch);
        f.Publish(); f.Form.Present();
        Assert.Equal(originalBounds, button.Bounds);
        Assert.NotSame(initial.Context, f.Service.Snapshot!.Context);
        Mouse(button, "OnMouseUp");
        Assert.False(f.Settings.Current.DaevanionNodeContentVisible);
        Assert.True(button.Visible);
    });

    [RequiresFact(TestRequirement.InteractiveDesktop)]
    public Task AFailedPreferenceWriteKeepsThePreviousDisplayState() => OnSta(() =>
    {
        using var f = new Fixture(); f.Publish(); f.Form.Present();
        File.Delete(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            Click(f.Form.ToggleWindow);
            Assert.False(f.Settings.Current.DaevanionNodeContentVisible);
            Assert.False(f.Form.Visible);
            Assert.True(f.Form.ToggleWindow.Visible);
            Assert.False(f.Form.ToggleWindow.ContentVisible);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); }
    });

    private static void Click(DaevanionToggleForm button) { Mouse(button, "OnMouseDown"); Mouse(button, "OnMouseUp"); }
    private static int[] Pixels(Bitmap image)
    {
        var result = new int[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < image.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, result, y * image.Width, image.Width);
        }
        finally { image.UnlockBits(data); }
        return result;
    }
    private static void Mouse(DaevanionToggleForm button, string method) => typeof(DaevanionToggleForm)
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(button, [new MouseEventArgs(MouseButtons.Left, 1, button.Width / 2, button.Height / 2, 0)]);

    private static Task OnSta(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); result.SetResult(); }
            catch (Exception error) { result.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return result.Task;
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly OwnCharacterIdentity Identity = new(101, 501, 300, "SyntheticSwitch") { ClassId = "Cleric" };
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-toggle-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        internal readonly SettingsService Settings = new();
        internal readonly GameCaptureService Capture = new();
        internal readonly NetworkLootService Network;
        internal readonly DaevanionService Service;
        internal readonly DaevanionOverlayForm Form;
        internal Rectangle? GameBounds = new(100, 100, 600, 400);
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Settings.Update(s => { s.DaevanionOverlayEnabled = true; s.DaevanionNodeContentVisible = false; s.OverlayLanguage = "en"; });
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Service = new(Network, Settings, Capture, () => GameBounds);
            Network.ObserveIdentity("own", Identity, DateTimeOffset.UnixEpoch);
            Form = new(Service, Settings);
        }
        internal DaevanionSnapshot Publish()
        {
            var original = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
            var nodes = original.Nodes.Where(n => n.Kind == DaevanionNodeKind.Stat).Take(3)
                .Select((node, i) => node with { Row = 3, Column = 3 + i }).ToArray();
            var board = original with { Nodes = nodes };
            var anchors = nodes.Select((node, i) => new DaevanionNodeAnchor(node.Row, node.Column, new(40 + i * 76, 100, 60, 60), .95)).ToArray();
            var snapshot = new DaevanionSnapshot(Network.OwnCharacter!, board, GameBounds!.Value, Stopwatch.GetTimestamp(),
                new("nezekan", new(-82, -22), 76, 60, anchors, .95), "en");
            Assert.True(Service.TryPublishObservation(snapshot, Service.ObservationRevision));
            return snapshot;
        }
        public void Dispose()
        {
            Form.Dispose(); Service.Dispose(); Network.Dispose(); Capture.Dispose();
            if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _language;
            Directory.Delete(_root, recursive: true);
        }
    }
}
