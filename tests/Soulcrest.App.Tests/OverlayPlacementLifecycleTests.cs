using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Exercises the native exit handlers on unshown forms; no handle, capture or live source.</summary>
[Collection("Settings file")]
public sealed class OverlayPlacementLifecycleTests : IDisposable
{
    private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;
    private static readonly OverlayMonitor Monitor = new("fixture", "Fixture", new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040));

    [Fact]
    public Task EscapeDuringAGestureSavesTheLastValidLayoutExactlyOnceAndEndsPlacement() => RunSta(() =>
    {
        var settings = Fresh();
        using var placement = new OverlayPlacementService(settings, () => [Monitor], () => null);
        using var renderer = new OverlayPreviewRenderer(new PetCatalog([]));
        Assert.True(placement.Start());
        using var form = new OverlayPlacementForm(settings, placement, renderer);
        var layout = new OverlayLayout(-1808, 144, 1.2);
        SeedGesture(form, layout);
        var saves = 0;
        settings.Changed += () => saves++;

        object[] arguments = [new Message(), Keys.Escape];
        var handled = typeof(OverlayPlacementForm).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, arguments);

        Assert.Equal(true, handled);
        Assert.False(placement.Active);
        Assert.Null(placement.Monitor);
        Assert.Equal(1, saves);
        Assert.Equal(layout, OverlayRegistry.ReadLayout(new SettingsService().Current, "loot"));
        Assert.False(settings.Current.OverlayEnabled);
        Assert.Equal(["fixture-boss"], settings.Current.BossAlertIds);
        Assert.False(form.IsHandleCreated);
    });

    [Fact]
    public Task MonitorRemovalThenNativeCloseFlushesTheCachedGestureAfterTheServiceLostItsMonitor() => RunSta(() =>
    {
        var settings = Fresh();
        IReadOnlyList<OverlayMonitor> monitors = [Monitor];
        using var placement = new OverlayPlacementService(settings, () => monitors, () => null);
        using var renderer = new OverlayPreviewRenderer(new PetCatalog([]));
        Assert.True(placement.Start());
        using var form = new OverlayPlacementForm(settings, placement, renderer);
        var layout = new OverlayLayout(-1664, 208, 1.35);
        SeedGesture(form, layout);
        var saves = 0;
        settings.Changed += () => saves++;

        monitors = [];
        placement.RefreshMonitors();
        Assert.False(placement.Active);
        Assert.Null(placement.Monitor);
        Assert.NotNull(placement.Error);
        typeof(OverlayPlacementForm).GetMethod("OnFormClosing", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);

        Assert.Equal(1, saves);
        Assert.Equal(layout, OverlayRegistry.ReadLayout(new SettingsService().Current, "loot"));
        Assert.Null(Field("_gestureId").GetValue(form));
        Assert.False(form.IsHandleCreated);
        Assert.True(form.PlacementClosing);
    });

    [Theory]
    [InlineData("ChangeScale")]
    [InlineData("ChangeWidth")]
    public Task SizeButtonsAtTheirVisibleLimitDoNotPersistVirtualPositionOrScale(string method) => RunSta(() =>
    {
        var settings = Fresh();
        settings.Update(current =>
        {
            current.EventTimelineOverlayX = 8000;
            current.EventTimelineOverlayY = -9000;
            current.EventTimelineOverlayWidth = 1600;
            current.EventTimelineOverlayScale = 2;
        });
        using var placement = new OverlayPlacementService(settings, () => [Monitor], () => null);
        using var renderer = new OverlayPreviewRenderer(new PetCatalog([]));
        Assert.True(placement.Start());
        placement.SelectedOverlayId = "event-timeline";
        using var form = new OverlayPlacementForm(settings, placement, renderer);
        var previews = (List<OverlayPlacementPreview>)Field("_previews").GetValue(form)!;
        previews.Add(new("event-timeline", "Timeline", new Bitmap(1600, 300), new(-1920, 80, 1.2, 1600)));
        Field("_monitorBounds").SetValue(form, Monitor.Bounds);
        Field("_layoutBounds").SetValue(form, Monitor.WorkingArea);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var saves = 0;
        settings.Changed += () => saves++;

        object delta = method == "ChangeScale" ? (object).05 : 16;
        typeof(OverlayPlacementForm).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, [delta]);

        Assert.Equal(0, saves);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
        Assert.Equal(new OverlayLayout(8000, -9000, 2, 1600), OverlayRegistry.ReadLayout(settings.Current, "event-timeline"));
        Assert.False(form.IsHandleCreated);
    });

    [Theory]
    [InlineData("aion2-stylized", "aion2-neutral")]
    [InlineData("aion2-neutral", "obsidian")]
    [InlineData("obsidian", "aurora")]
    [InlineData("aurora", "runic")]
    [InlineData("runic", "soulcrest-legacy")]
    [InlineData("soulcrest-legacy", "aion2-stylized")]
    public Task ThemeChangeDuringANoOpGestureWaitsForItsEndWithoutSavingAnyLayout(string previous, string next) => RunSta(() =>
    {
        var settings = Fresh();
        settings.Current.OverlayTheme = previous;
        using var placement = new OverlayPlacementService(settings, () => [Monitor], () => null);
        using var renderer = new OverlayPreviewRenderer(new PetCatalog([]));
        Assert.True(placement.Start());
        using var form = new OverlayPlacementForm(settings, placement, renderer);
        _ = form.Handle; // An unshown own HWND exercises the real deferred presentation path.
        var refresh = typeof(OverlayPlacementForm).GetMethod("RefreshState", BindingFlags.NonPublic | BindingFlags.Instance)!;
        refresh.Invoke(form, null);
        var layouts = OverlayRegistry.All.Where(item => item.CanPlace)
            .Select(item => OverlayRegistry.ReadLayout(settings.Current, item.Id)).ToArray();
        Field("_gestureId").SetValue(form, "loot");
        Field("_gestureChanged").SetValue(form, false);
        var changes = 0; settings.Changed += () => changes++;
        settings.Update(current => current.OverlayTheme = next);
        Assert.Equal(previous, ((AppSettings)Field("_renderSettings").GetValue(form)!).OverlayTheme);
        typeof(OverlayPlacementForm).GetMethod("FinishGesture", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
        Assert.Equal(next, ((AppSettings)Field("_renderSettings").GetValue(form)!).OverlayTheme);
        Assert.Equal(layouts, OverlayRegistry.All.Where(item => item.CanPlace)
            .Select(item => OverlayRegistry.ReadLayout(settings.Current, item.Id)).ToArray());
        Assert.Equal(1, changes);
        Assert.True(placement.Active);
        Assert.False(form.Visible);
    });

    private static void SeedGesture(OverlayPlacementForm form, OverlayLayout layout)
    {
        var previews = (List<OverlayPlacementPreview>)Field("_previews").GetValue(form)!;
        previews.Add(new("loot", "Pet-Fortschritt", new Bitmap(360, 300), layout));
        Field("_monitorBounds").SetValue(form, Monitor.Bounds);
        Field("_layoutBounds").SetValue(form, Monitor.WorkingArea);
        Field("_gestureId").SetValue(form, "loot");
        Field("_gestureLayout").SetValue(form, new OverlayLayout(-1900, 40, 1));
        Field("_gestureChanged").SetValue(form, true);
    }

    private static FieldInfo Field(string name) => typeof(OverlayPlacementForm)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static SettingsService Fresh()
    {
        Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            SettingsRevision = 5, OverlayEnabled = false, OverlayX = -1900, OverlayY = 40,
            BossAlertIds = ["fixture-boss"], TimerOverlayTimerIds = []
        });
        return new SettingsService();
    }

    private static Task RunSta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    public void Dispose()
    {
        if (_backup is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}
