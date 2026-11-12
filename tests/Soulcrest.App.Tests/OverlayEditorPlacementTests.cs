using System.Drawing;
using System.Reflection;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using OverlayLayout = Soulcrest.App.Services.OverlayLayout;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayEditorPlacementTests
{
    private static readonly OverlayMonitor Left = new("left", "Left", new(-1920, -120, 1920, 1080), new(-1880, -100, 1880, 1020));

    [Fact]
    public async Task SharedStartIncludesEightStaticPanelsAndEveryKitWindowWithoutActivatingOrSavingThem()
    {
        using var fixture = new Fixture();
        Assert.True((await fixture.Editor.CreateOverlayAsync("Leer")).Succeeded);
        var selected = fixture.Editor.SelectedOverlayId;
        var before = File.ReadAllBytes(fixture.Path);
        var nativeBefore = System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current);
        using var service = fixture.Placement();
        Assert.True(service.StartEditor("left", selected));
        Assert.Equal(8, service.Items.Count(item => item.CanPlace && !item.IsEditor));
        Assert.Equal(2, service.Items.Count(item => item.IsEditor));
        Assert.Equal(OverlayPlacementService.EditorKey(selected), service.SelectedOverlayId);
        Assert.DoesNotContain(service.Items, item => item.Id is "farm-loot" or "abyss-rank");
        Assert.All(service.Items.Where(item => item.IsEditor), item => { Assert.False(item.Editor!.Settings.Enabled); Assert.Empty(item.Editor.Settings.Widgets); });
        service.Stop();
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Equal(nativeBefore, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Same(OverlayRegistry.All, service.Overlays);
    }

    [Fact]
    public async Task ActiveInventoryFollowsKitCreationAndDeletionWithoutResurrectingADeletedSelection()
    {
        using var fixture = new Fixture();
        using var service = fixture.Placement();
        Assert.True(service.StartEditor("left", "default"));
        Assert.True((await fixture.Editor.CreateOverlayAsync("Neu")).Succeeded);
        var created = fixture.Editor.SelectedOverlayId;
        Assert.Contains(service.Items, item => item.Id == OverlayPlacementService.EditorKey(created));
        service.SelectedOverlayId = OverlayPlacementService.EditorKey(created);
        Assert.True((await fixture.Editor.DeleteOverlayAsync(created)).Succeeded);
        Assert.DoesNotContain(service.Items, item => item.Id == OverlayPlacementService.EditorKey(created));
        Assert.NotEqual(OverlayPlacementService.EditorKey(created), service.SelectedOverlayId);
        Assert.True(service.Active);
    }

    [Fact]
    public void EndingRunRejectsASecondStartWithoutReplacingItsInventory()
    {
        using var fixture = new Fixture();
        using var service = fixture.Placement();
        Assert.True(service.StartEditor("left", "default"));
        var items = service.Items;
        service.SetEnding(true);
        Assert.False(service.StartEditor("left", "default"));
        Assert.True(service.Active);
        Assert.Same(items, service.Items);
        Assert.NotNull(service.Error);
        service.SetEnding(false);
        service.Stop();
        Assert.True(service.StartEditor("left", "default"));
    }

    [Fact]
    public void CorruptKitStoreBlocksPlacementWithoutTouchingItsOriginalBytes()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(fixture.Path, "broken editor");
        using var corrupt = new OverlayService(fixture.Root);
        using var service = new OverlayPlacementService(fixture.Settings, () => [Left], () => null, corrupt);
        Assert.False(service.StartEditor("left"));
        Assert.False(service.Active);
        Assert.NotNull(service.Error);
        Assert.Equal("broken editor", File.ReadAllText(fixture.Path));
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void EditorGeometryRoundtripsWorkingAreaFractionsOnANegativeMonitor(double scale)
    {
        var settings = new OverlaySettings { PositionX = .37, PositionY = .81, Scale = scale };
        var logical = new Size(360, 260);
        var layout = OverlayEditorPlacementGeometry.Read(settings, logical, Left);
        var fractions = OverlayEditorPlacementGeometry.Fractions(layout, logical, Left);
        var physical = OverlayEditorPlacementGeometry.ScaledSize(logical, scale);
        Assert.InRange(Math.Abs(fractions.X - settings.PositionX), 0, 1d / (Left.WorkingArea.Width - physical.Width));
        Assert.InRange(Math.Abs(fractions.Y - settings.PositionY), 0, 1d / (Left.WorkingArea.Height - physical.Height));
        Assert.True(layout.X < 0);
        Assert.Equal(scale, layout.Scale);
        Assert.True(Left.WorkingArea.Contains(new Rectangle(new Point(layout.X, layout.Y), physical)));
    }

    [Fact]
    public void OversizedPanelsAndNonfiniteFractionsNeverCreateNegativeTravelOrInvalidCoordinates()
    {
        var settings = new OverlaySettings { PositionX = double.NaN, PositionY = double.PositiveInfinity, Scale = 2 };
        var logical = new Size(2000, 2000);
        var layout = OverlayEditorPlacementGeometry.Read(settings, logical, Left);
        Assert.Equal(Left.WorkingArea.Left, layout.X);
        Assert.Equal(Left.WorkingArea.Top, layout.Y);
        Assert.Equal((0d, 0d), OverlayEditorPlacementGeometry.Fractions(layout, logical, Left));
        Assert.Equal((0d, 1d), OverlayEditorPlacementGeometry.Fractions(new(-9000, 9000, 1), new(100, 100), Left));
    }

    [Fact]
    public async Task CompletedEditorPlacementSavesMonitorPositionAndScaleAtomicallyWhilePreservingTheLayout()
    {
        using var fixture = new Fixture();
        var widget = OverlayCatalog.CreateWidget("duration");
        var rules = new OverlayTriggerRules { Areas = [OverlayTriggerArea.Abyss] };
        Assert.True((await fixture.Editor.SaveAsync(fixture.Editor.Settings with { Widgets = [widget], TriggerRules = rules })).Succeeded);
        using var service = fixture.Placement();
        Assert.True(service.StartEditor("left", "default"));
        var layout = new OverlayLayout(-1500, 120, 1.4);
        var expected = OverlayEditorPlacementGeometry.Fractions(layout, new(360, 260), Left);
        Assert.True((await service.SaveEditorPlacementAsync(OverlayPlacementService.EditorKey("default"), layout, new(360, 260), Left)).Succeeded);
        using var reopened = new OverlayService(fixture.Root);
        Assert.Equal("left", reopened.Settings.PositionMonitorId);
        Assert.Equal(expected.X, reopened.Settings.PositionX);
        Assert.Equal(expected.Y, reopened.Settings.PositionY);
        Assert.Equal(1.4, reopened.Settings.Scale);
        Assert.Equal(widget, Assert.Single(reopened.Settings.Widgets));
        Assert.True(OverlayTriggerRules.Equivalent(rules, reopened.Settings.TriggerRules));
        Assert.False(reopened.Settings.Enabled);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public Task RefreshUsesOneExampleSnapshotAndScaleFreeDomBitmapsForAllEditorWindows() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Editor.SaveAsync(fixture.Editor.Settings with { Scale = 1.5 }).GetAwaiter().GetResult();
        fixture.Editor.CreateOverlayAsync("Zweites").GetAwaiter().GetResult();
        using var service = fixture.Placement();
        service.StartEditor("left", "default");
        using var native = new OverlayPreviewRenderer(new PetCatalog([]));
        var presentation = new Examples();
        using var raster = new Raster();
        using var form = new OverlayPlacementForm(fixture.Settings, service, native, presentation, raster);
        _ = form.Handle;
        Method("RefreshState").Invoke(form, null);
        Assert.Equal(1, presentation.Reads);
        Assert.Equal(2, raster.Reads);
        var previews = (List<OverlayPlacementPreview>)Field("_previews").GetValue(form)!;
        Assert.Equal(10, previews.Count);
        Assert.All(previews.Where(item => item.Editor is not null), item => Assert.Equal(360, item.Image.Width));
        Assert.Equal(1.5, previews.Single(item => item.Id == OverlayPlacementService.EditorKey("default")).Layout.Scale);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ARefreshedPlacementFormCanBeDisposedAgainAfterCleanupOrClosing(bool closeFirst) => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var service = fixture.Placement();
        Assert.True(service.StartEditor("left", "default"));
        using var native = new OverlayPreviewRenderer(new PetCatalog([]));
        using var raster = new Raster();
        using var form = new OverlayPlacementForm(fixture.Settings, service, native, new Examples(), raster);
        _ = form.Handle;
        Method("RefreshState").Invoke(form, null);
        Assert.IsType<CancellationTokenSource>(Field("_renderCancellation").GetValue(form));
        var previews = (List<OverlayPlacementPreview>)Field("_previews").GetValue(form)!;
        Assert.NotEmpty(previews);
        if (closeFirst)
        {
            // MainForm disposes in FormClosed; a caller may also own a using scope.
            form.FormClosed += (_, _) => form.Dispose();
            form.Close();
        }
        else form.Dispose();
        form.Dispose();
        Assert.True(form.IsDisposed);
        Assert.Null(Field("_renderCancellation").GetValue(form));
        Assert.Empty(previews);
    });

    [Fact]
    public Task ExternalKitRevisionCancelsADirtyGestureBeforeItCanOverwriteTheNewerPlacement() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Editor.SaveAsync(fixture.Editor.Settings).GetAwaiter().GetResult();
        using var service = fixture.Placement();
        service.StartEditor("left", "default");
        using var native = new OverlayPreviewRenderer(new PetCatalog([]));
        using var form = new OverlayPlacementForm(fixture.Settings, service, native);
        var window = service.Items.Single(item => item.IsEditor).Editor!;
        var key = OverlayPlacementService.EditorKey(window.Id);
        ((List<OverlayPlacementPreview>)Field("_previews").GetValue(form)!).Add(new(key, window.Name, new Bitmap(360, 260), new(-1500, 100, 1.2), window));
        Field("_gestureId").SetValue(form, key);
        Field("_gestureChanged").SetValue(form, true);
        Field("_gestureLayout").SetValue(form, new OverlayLayout(-1600, 80, 1));
        Field("_gestureEditorSettings").SetValue(form, window.Settings);
        Field("_gestureMonitor").SetValue(form, Left);
        fixture.Editor.SavePlacementAsync(window.Id, "left", .8, .7, 1.7).GetAwaiter().GetResult();
        var before = File.ReadAllBytes(fixture.Path);
        Method("FinishGesture").Invoke(form, null);
        Assert.Null(Field("_gestureId").GetValue(form));
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Equal(1.7, fixture.Editor.Settings.Scale);
        Assert.False(form.IsHandleCreated);
    });

    [Fact]
    public Task LongInventoriesKeepPagingAndDoneControlsInsideA720PixelScreen() => RunSta(() =>
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 6; index++) fixture.Editor.CreateOverlayAsync("Window " + index).GetAwaiter().GetResult();
        using var service = fixture.Placement();
        service.StartEditor("left", "default");
        using var native = new OverlayPreviewRenderer(new PetCatalog([]));
        using var form = new OverlayPlacementForm(fixture.Settings, service, native) { ClientSize = new(1280, 720) };
        using var image = new Bitmap(1280, 720);
        using var graphics = Graphics.FromImage(image);
        Method("DrawControls").Invoke(form, [graphics]);
        var buttons = (List<(Rectangle Bounds, Action Invoke)>)Field("_buttons").GetValue(form)!;
        Assert.InRange(buttons.Count, 6, 17);
        Assert.All(buttons, button => Assert.InRange(button.Bounds.Bottom, 0, 667));
        Assert.True(service.Items.Count(item => item.CanPlace) > OverlayPlacementForm.ListPageSize(720));
    });

    private static FieldInfo Field(string name) => typeof(OverlayPlacementForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static MethodInfo Method(string name) => typeof(OverlayPlacementForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Soulcrest-editor-placement-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "overlay.json");
        internal SettingsService Settings { get; } = new();
        internal OverlayService Editor { get; }
        internal Fixture() { Assert.StartsWith(System.IO.Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase); Editor = new(Root, new OverlayCollectionSettings()); }
        internal OverlayPlacementService Placement() => new(Settings, () => [Left], () => null, Editor);
        public void Dispose() { Editor.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Examples : IOverlayEditorPresentationSource
    {
        public int Reads { get; private set; }
        public event Action? Changed;
        public void Publish() => Changed?.Invoke();
        public OverlaySnapshot Capture(bool examples = false, string? language = null) { Assert.True(examples); Reads++; return OverlaySnapshot.DemoFor("en"); }
        public IDisposable Acquire(bool examples = false, bool market = false) => throw new InvalidOperationException("Placement cannot acquire live sources.");
        public void Dispose() { }
    }
    private sealed class Raster : IOverlayEditorBitmapRenderer
    {
        public int Reads { get; private set; }
        public Task<Bitmap> RenderAsync(OverlayInstance window, OverlaySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(new Bitmap((int)Math.Ceiling(window.Settings.Width), (int)Math.Ceiling(window.Settings.Height)));
        }
        public void Dispose() { }
    }
}
