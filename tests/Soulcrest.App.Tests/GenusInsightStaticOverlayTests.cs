using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;
using OverlayLayout = Soulcrest.App.Services.OverlayLayout;

namespace Soulcrest.App.Tests;

/// <summary>Isolated settings and own offscreen HWNDs; no app host, game capture or game input.</summary>
[Collection("Settings file")]
public sealed class GenusInsightStaticOverlayTests
{
    private static GenusInsightSnapshot Example => GenusInsightOverlayExamples.Create(
        new(2026, 11, 9, 12, 34, 56, TimeSpan.Zero), "anonymous-static-overlay");

    [Theory]
    [InlineData(null, true)]
    [InlineData("{}", true)]
    [InlineData("{\"SettingsRevision\":1}", true)]
    [InlineData("{\"GenusInsightOverlayEnabled\":false}", false)]
    [InlineData("{\"SettingsRevision\":1,\"GenusInsightOverlayEnabled\":false}", false)]
    public void FreshAndMissingPreferencesDefaultOnWhileSavedOffSurvivesReload(string? json, bool enabled)
    {
        using var fixture = new Fixture();
        if (json is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllText(AppPaths.SettingsFile, json);
        Assert.True(new AppSettings().GenusInsightOverlayEnabled);
        var settings = new SettingsService();
        Assert.Equal(enabled, settings.Current.GenusInsightOverlayEnabled);
        settings.Update(current => current.UiLanguage = "en");
        Assert.Equal(enabled, new SettingsService().Current.GenusInsightOverlayEnabled);
        settings.Update(current => current.GenusInsightOverlayEnabled = false);
        Assert.False(new SettingsService().Current.GenusInsightOverlayEnabled);
    }

    [Fact]
    public void StaticFamilyIsFreelyPlaceableAndPersistsOnlyItsOwnGeometry()
    {
        using var fixture = new Fixture();
        var descriptor = OverlayRegistry.Get("genus-insight");
        Assert.Equal("Genus Insight", descriptor.Title);
        Assert.True(descriptor.CanPlace);
        fixture.Settings.Update(current =>
        {
            current.GenusInsightOverlayEnabled = false;
            current.OverlayX = 701;
            current.OverlayY = 702;
            current.OverlayScale = 1.25;
        });
        using var placement = new OverlayPlacementService(fixture.Settings,
            () => [new("left", "Left", new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040))], () => null);
        var changes = 0;
        placement.Changed += () => changes++;
        var requested = new OverlayLayout(-1640, 230, 1.4);
        placement.UpdateLayout("genus-insight", requested);
        placement.UpdateLayout("genus-insight", requested);
        Assert.Equal(1, changes);
        Assert.True(placement.Start());
        Assert.Contains(placement.PlaceableOverlays, item => item.Id == "genus-insight");
        placement.Stop();
        var loaded = new SettingsService().Current;
        Assert.Equal(requested, OverlayRegistry.ReadLayout(loaded, "genus-insight"));
        Assert.False(loaded.GenusInsightOverlayEnabled);
        Assert.Equal((701, 702, 1.25), (loaded.OverlayX, loaded.OverlayY, loaded.OverlayScale));
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(-10, .6)]
    [InlineData(10, 2)]
    public void StaticScaleIsFiniteBoundedAndConsistentAcrossLayoutAndRendering(double value, double expected)
    {
        Assert.Equal(expected, OverlayRegistry.NormalizeLayout("genus-insight", new(0, 0, value)).Scale);
        Assert.Equal(expected, GenusInsightOverlayRenderer.NormalizeScale(value));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040)]
    [InlineData(-1920, -40, 1920, 1040)]
    [InlineData(0, 0, 1280, 680)]
    public void DefaultPlacementLeavesTheNativeStatColumnVisibleAndRetainsAnExplicitDesktopPosition(
        int left, int top, int width, int height)
    {
        var area = new Rectangle(left, top, width, height);
        var settings = new AppSettings();
        var automatic = OverlayRegistry.GenusInsightLayout(settings, area);
        Assert.InRange(automatic.X, area.Left, area.Right - 340);
        Assert.InRange(automatic.Y, area.Top, area.Bottom - 510);
        Assert.True(automatic.X + 340 <= area.Right - 340);
        settings.GenusInsightOverlayX = -4700;
        settings.GenusInsightOverlayY = -800;
        var saved = OverlayRegistry.GenusInsightLayout(settings, area);
        Assert.Equal((-4700, -800), (saved.X, saved.Y));
        Assert.Null(new AppSettings().GenusInsightOverlayX);
        Assert.Null(new AppSettings().GenusInsightOverlayY);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void SidebarRendersEverySlotAndUsesTheSharedThemesWithoutChangingTheSnapshot(string language)
    {
        var snapshot = Example;
        var before = JsonSerializer.Serialize(snapshot);
        foreach (var theme in OverlayThemeDefinition.All)
        {
            using var image = GenusInsightOverlayRenderer.Render(snapshot, theme, language: language);
            Assert.Equal(new Size(340, 510), image.Size);
            Assert.Equal(PixelFormat.Format32bppPArgb, image.PixelFormat);
            Assert.True(image.GetPixel(image.Width / 2, image.Height / 2).A > 0);
        }
        using var original = GenusInsightOverlayRenderer.Render(snapshot, language: language);
        var originalHash = ImageHash(original);
        for (byte number = 1; number <= 9; number++)
        {
            var slots = snapshot.ViewedBoard!.Slots.Select(slot => slot.Slot == number
                ? slot with { LabelDe = "Synthetischer Slot " + number, LabelEn = "Synthetic slot " + number }
                : slot).ToArray();
            using var changed = GenusInsightOverlayRenderer.Render(snapshot with
            {
                ViewedBoard = snapshot.ViewedBoard with { Slots = slots }
            }, language: language);
            Assert.NotEqual(originalHash, ImageHash(changed));
        }
        Assert.Equal(before, JsonSerializer.Serialize(snapshot));
    }

    [Theory]
    [InlineData("en", "3.2%", "0.25%", "3.25%")]
    [InlineData("de", "3,2%", "0,25%", "3,25%")]
    public void PercentFormattingKeepsExactRangeEndpointsAndNeverTurnsRoundingIntoAMaximum(
        string language, string expectedValue, string expectedMinimum, string expectedMaximum)
    {
        var slot = Example.ViewedBoard!.Slots[0] with
        {
            Unit = "Percent", Value = 3.249m, Minimum = .25m, Maximum = 3.25m,
            RollFraction = .999666666, IsCurrent = true, IsMaxRoll = true
        };
        Assert.Equal(expectedValue, GenusInsightOverlayRenderer.ValueText(slot, language).Replace(" ", ""));
        Assert.Equal(expectedMinimum, GenusInsightOverlayRenderer.RangeText(slot.Minimum, slot.Unit, language).Replace(" ", ""));
        Assert.Equal(expectedMaximum, GenusInsightOverlayRenderer.RangeText(slot.Maximum, slot.Unit, language).Replace(" ", ""));
        Assert.False(GenusInsightOverlayRenderer.ShowMax(slot));
        Assert.True(GenusInsightOverlayRenderer.ShowMax(slot with { Value = 3.25m }));
        Assert.False(GenusInsightOverlayRenderer.ShowMax(slot with { Value = 3.25m, IsCurrent = false }));
        Assert.False(GenusInsightOverlayRenderer.ShowMax(slot with { Value = 3.25m, IsMaxRoll = false }));
        var snapshot = Example with { ViewedBoard = Example.ViewedBoard! with { Slots = [slot] } };
        using var invalidMaxFlag = GenusInsightOverlayRenderer.Render(snapshot, language: language);
        using var noMaxFlag = GenusInsightOverlayRenderer.Render(snapshot with
        {
            ViewedBoard = snapshot.ViewedBoard! with { Slots = [slot with { IsMaxRoll = false }] }
        }, language: language);
        Assert.Equal(ImageHash(noMaxFlag), ImageHash(invalidMaxFlag));
    }

    [Theory]
    [InlineData("old")]
    [InlineData("historical")]
    [InlineData("menu")]
    [InlineData("generation")]
    [InlineData("profile")]
    [InlineData("category")]
    [InlineData("preset")]
    [InlineData("board")]
    public Task MenuAndAuthorityLossHideAndRelockTheNativeSidebar(string boundary) => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var panel = fixture.Panel();
        panel.Start(); panel.Show();
        Assert.True(panel.Visible);
        panel.ToggleManualInteractionFromHotkey();
        Assert.False(panel.Locked);
        var snapshot = fixture.Snapshot;
        fixture.Snapshot = boundary switch
        {
            "old" => snapshot with { IsCurrent = false },
            "historical" => snapshot with { IsHistorical = true, IsCurrent = false },
            "menu" => snapshot with { MenuVisible = false },
            "generation" => snapshot with { Generation = 0 },
            "profile" => snapshot with { ProfileId = null },
            "category" => snapshot with { SelectedGenus = "fera" },
            "preset" => snapshot with { ViewedPreset = null },
            _ => snapshot with { ViewedBoard = null },
        };
        panel.ApplySettings(); panel.Show();
        Assert.False(panel.Visible);
        Assert.False(panel.RuntimeAvailable);
        Assert.True(panel.Locked);
        Assert.False(panel.ManualInteractionEnabled);
        Assert.False(panel.Capture);
        Assert.True(fixture.Settings.Current.GenusInsightOverlayEnabled);
        fixture.Snapshot = snapshot with { ProfileId = "anonymous-next-profile", Generation = snapshot.Generation + 1 };
        panel.ApplySettings();
        Assert.True(panel.Visible);
        Assert.True(panel.Locked);
    });

    [Fact]
    public Task ConstructorAndHandleCreationRemainPassiveUntilExplicitStart() => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var panel = fixture.Panel();
        Assert.Equal(0, fixture.Acquired);
        Assert.False(panel.IsHandleCreated);
        _ = panel.Handle;
        Assert.Equal(0, fixture.Acquired);
        panel.Start(); panel.Start();
        Assert.Equal(1, fixture.Acquired);
        Assert.Equal(0, fixture.Released);
    });

    [Fact]
    public Task LeaseIsKeptForMenuDetectionAndReleasedWhenDisabledSuppressedOrDisposed() => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var panel = fixture.Panel();
        panel.Start(); panel.Show();
        Assert.Equal(1, fixture.Acquired);
        fixture.Snapshot = fixture.Snapshot with { MenuVisible = false };
        panel.ApplySettings();
        Assert.False(panel.Visible);
        Assert.Equal(0, fixture.Released);
        fixture.Settings.Current.GenusInsightOverlayEnabled = false;
        panel.ApplySettings();
        Assert.Equal(1, fixture.Released);
        fixture.Settings.Current.GenusInsightOverlayEnabled = true;
        panel.ApplySettings();
        Assert.Equal(2, fixture.Acquired);
        panel.SetPlacementHidden(true);
        Assert.Equal(2, fixture.Released);
        panel.SetPlacementHidden(false);
        Assert.Equal(3, fixture.Acquired);
        panel.SetForegroundSuppressed(true);
        Assert.Equal(3, fixture.Released);
        panel.SetForegroundSuppressed(false);
        Assert.Equal(4, fixture.Acquired);
        panel.SetSuppressed(true);
        Assert.Equal(4, fixture.Released);
        panel.SetSuppressed(false);
        Assert.Equal(5, fixture.Acquired);
        panel.Dispose();
        Assert.Equal(fixture.Acquired, fixture.Released);
    });

    [Fact]
    public Task NativeDragSavesItsPositionAndPlacementHidingCancelsInteraction() => RunSta(() =>
    {
        using var fixture = new Fixture();
        using var panel = fixture.Panel();
        panel.Start(); panel.Show();
        Assert.True(panel.Visible);
        var style = NativeMethods.GetWindowLongPtr(panel.Handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        Assert.NotEqual(0, style & NativeMethods.WS_EX_NOACTIVATE);
        Assert.NotEqual(0, style & NativeMethods.WS_EX_TRANSPARENT);
        panel.ToggleManualInteractionFromHotkey();
        Assert.False(panel.Locked);
        var position = panel.Location;
        SendMouse(panel, 0x0201, new(30, 30));
        Assert.True(panel.Capture);
        SendMouse(panel, 0x0200, new(70, 60));
        Assert.Equal(new Point(position.X + 40, position.Y + 30), panel.Location);
        SendMouse(panel, 0x0202, new(70, 60));
        Assert.False(panel.Capture);
        var saved = new SettingsService().Current;
        Assert.Equal((panel.Left, panel.Top), (saved.GenusInsightOverlayX, saved.GenusInsightOverlayY));
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        panel.SetPlacementHidden(true); panel.Show();
        Assert.False(panel.Visible);
        Assert.True(panel.Locked);
        Assert.False(panel.ManualInteractionEnabled);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
        panel.SetPlacementHidden(false);
        Assert.True(panel.Visible);
        Assert.True(panel.Locked);
    });

    private static long ImageHash(Bitmap image)
    {
        var hash = 17L;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            hash = unchecked(hash * 31 + image.GetPixel(x, y).ToArgb());
        return hash;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly byte[]? _backup;
        private readonly string _language = UiText.Language;
        public SettingsService Settings { get; }
        public GenusInsightSnapshot Snapshot { get; set; } = Example;
        public int Acquired { get; private set; }
        public int Released { get; private set; }
        public Fixture()
        {
            var redirected = Environment.GetEnvironmentVariable("SOULCREST_DATA");
            Assert.False(string.IsNullOrWhiteSpace(redirected), "Native overlay tests require isolated SOULCREST_DATA.");
            Assert.Equal(Path.GetFullPath(redirected!), Path.GetFullPath(AppPaths.DataDirectory), ignoreCase: true);
            Directory.CreateDirectory(AppPaths.DataDirectory);
            _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
            Settings = new();
            Settings.Current.GenusInsightOverlayEnabled = true;
            Settings.Current.GenusInsightOverlayX = Settings.Current.GenusInsightOverlayY = -30000;
            Settings.Current.GenusInsightOverlayScale = 1;
        }
        public GenusInsightOverlayForm Panel() => new(() => Snapshot, () =>
        {
            Acquired++;
            return new Lease(() => Released++);
        }, Settings);
        public void Dispose()
        {
            if (_backup is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _language;
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private static void SendMouse(Form panel, int message, Point point) =>
        SendMessage(panel.Handle, message, message == 0x0202 ? 0 : 1,
            (nint)((point.Y << 16) | (point.X & 0xffff)));

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    private static Task RunSta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception error) { completed.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }
}
