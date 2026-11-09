using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class FarmLootUiIntegrationTests : IDisposable
{
    private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;

    [Fact]
    public void DefaultPveRecordingPreservesLegacyLayoutsWithoutOfferingRetiredHuds()
    {
        var settings = new AppSettings();
        Assert.True(settings.MonsterLootTrackingEnabled);
        Assert.False(settings.LootOverlayEnabled);
        Assert.False(settings.AbyssRankOverlayEnabled);
        Assert.True(settings.OverlayEnabled);
        Assert.Equal((48, 300, 1d), (settings.LootOverlayX, settings.LootOverlayY, settings.LootOverlayScale));
        settings.LootOverlayEnabled = true;
        settings.LootOverlayX = -1500;
        settings.LootOverlayY = 220;
        settings.LootOverlayScale = 1.4;
        settings.AbyssRankOverlayEnabled = true;
        settings.AbyssRankOverlayX = -1100;
        settings.AbyssRankOverlayY = 350;
        settings.AbyssRankOverlayScale = 1.2;
        JsonFile.Save(AppPaths.SettingsFile, settings);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var restored = new SettingsService().Current;
        Assert.True(restored.LootOverlayEnabled);
        Assert.True(restored.AbyssRankOverlayEnabled);
        Assert.Equal((-1500, 220, 1.4), (restored.LootOverlayX, restored.LootOverlayY, restored.LootOverlayScale));
        Assert.Equal((-1100, 350, 1.2), (restored.AbyssRankOverlayX, restored.AbyssRankOverlayY, restored.AbyssRankOverlayScale));
        Assert.Equal(new Soulcrest.App.Services.OverlayLayout(40, 200, 1), OverlayRegistry.ReadLayout(restored, "loot"));
        Assert.DoesNotContain(OverlayRegistry.All, overlay => overlay.Id is "farm-loot" or "abyss-rank");
        Assert.Throws<ArgumentException>(() => OverlayRegistry.Get("farm-loot"));
        Assert.Throws<ArgumentException>(() => OverlayRegistry.Get("abyss-rank"));
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    }

    [Theory]
    [InlineData("monster")]
    [InlineData("overlay")]
    public void SavedSwitchesRoundtripAndAWriteFailureRollsBackWithoutAnEvent(string preference)
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings { LootTrackingEnabled = false, OverlayEnabled = false,
            MonsterLootTrackingEnabled = false, MonsterLootAutoSessionRevision = 1 });
        var settings = new SettingsService();
        var events = 0;
        settings.Changed += () => events++;
        void Set(bool enabled)
        {
            if (preference == "monster") settings.SetMonsterLootTrackingEnabled(enabled);
            else settings.SetLootOverlayEnabled(enabled);
        }
        Set(true);
        var restored = new SettingsService().Current;
        Assert.Equal(preference == "monster", restored.MonsterLootTrackingEnabled);
        Assert.Equal(preference == "overlay", restored.LootOverlayEnabled);
        Assert.False(restored.OverlayEnabled);
        Assert.False(restored.LifeskillTrackingEnabled);
        Assert.False(restored.LootTrackingEnabled);
        Assert.Equal(1, events);
        Set(true);
        Assert.Equal(1, events);
        var before = JsonSerializer.Serialize(settings.Current);
        File.Delete(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            var error = Record.Exception(() => Set(false));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
            Assert.Equal(1, events);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); }
    }

    [Fact]
    public void LootOverlayScaleIsSanitizedOnLoadAndSaveWhileNegativeMonitorCoordinatesSurvive()
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings { LootOverlayX = -1800, LootOverlayY = 50, LootOverlayScale = -7 });
        var settings = new SettingsService();
        Assert.Equal(.6, settings.Current.LootOverlayScale);
        Assert.Equal(-1800, settings.Current.LootOverlayX);
        settings.Update(row => row.LootOverlayScale = double.NaN);
        Assert.Equal(1, new SettingsService().Current.LootOverlayScale);
        settings.Update(row => row.LootOverlayScale = 8);
        Assert.Equal(2, new SettingsService().Current.LootOverlayScale);
    }

    [Fact]
    public Task HostSharesEditorPresentationAndOneNetworkSourceWithoutRetiredHudsOrStartingTheApp() => RunSta(() =>
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            LootTrackingEnabled = false, MonsterLootTrackingEnabled = false, LootOverlayEnabled = false,
            CheckUpdatesOnStart = false, MapTrackingActive = false
        });
        using var host = new MainForm(); // Never Show: no WebView startup, passive capture or game window.
        var network = host.Services.GetRequiredService<NetworkLootService>();
        Assert.Same(network, host.Services.GetRequiredService<INetworkFarmSource>());
        Assert.Same(network, host.Services.GetRequiredService<INetworkAbyssSource>());
        Assert.Same(network, host.Services.GetRequiredService<INetworkAbyssCombatSource>());
        Assert.Same(network, host.Services.GetRequiredService<INetworkGatheringSource>());
        Assert.Same(network, host.Services.GetRequiredService<INetworkKinaSource>());
        Assert.Null(host.Services.GetService<UnifiedLootOverlaySource>());
        Assert.Null(host.Services.GetService<ILootOverlaySource>());
        Assert.Same(host.Services.GetRequiredService<AbyssRankOverlaySource>(), host.Services.GetRequiredService<IAbyssRankOverlaySource>());
        Assert.NotNull(host.Services.GetRequiredService<UnifiedLootTrackerService>());
        Assert.NotNull(host.Services.GetRequiredService<AbyssProgressService>());
        Assert.NotNull(host.Services.GetRequiredService<RareDropAlertService>());
        var presentation = host.Services.GetRequiredService<IOverlayEditorPresentationSource>();
        Assert.IsType<OverlayEditorPresentationSource>(presentation);
        Assert.Same(presentation, host.Services.GetRequiredService<IOverlayEditorPresentationSource>());
        var layouts = host.Services.GetRequiredService<IOverlayService>();
        var windows = layouts.Overlays;
        var live = presentation.Capture(false);
        Assert.False(live.SessionActive);
        Assert.False(live.TrackingAvailable);
        Assert.Contains("clock", live.Metrics.Keys);
        Assert.True(presentation.Capture(true).SessionActive);
        Assert.False(presentation.Capture(false).SessionActive);
        Assert.Same(windows, layouts.Overlays);
        Assert.False(network.Running);
        Assert.False(host.Visible);
    });

    public void Dispose()
    {
        if (Directory.Exists(AppPaths.SettingsFile)) Directory.Delete(AppPaths.SettingsFile);
        if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
        UiText.Language = _language;
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
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
