using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class FarmLootUiIntegrationTests : IDisposable
{
    private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;

    [Fact]
    public void DefaultPveRecordingDoesNotEnableEitherOverlay()
    {
        var settings = new AppSettings();
        Assert.True(settings.MonsterLootTrackingEnabled);
        Assert.False(settings.LootOverlayEnabled);
        Assert.True(settings.OverlayEnabled);
        Assert.Equal(new OverlayLayout(48, 300, 1), OverlayRegistry.ReadLayout(settings, "farm-loot"));
        OverlayRegistry.WriteLayout(settings, "farm-loot", new(-1500, 220, 1.4));
        Assert.Equal(new OverlayLayout(-1500, 220, 1.4), OverlayRegistry.ReadLayout(settings, "farm-loot"));
        Assert.Equal(new OverlayLayout(40, 200, 1), OverlayRegistry.ReadLayout(settings, "loot"));
        Assert.Equal(new OverlayVisibilityRules(), OverlayVisibilityPolicy.Get(settings, "farm-loot"));
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
    public void MixedSessionSourcesPreserveAllEntriesAndUseTheNewestObservation()
    {
        var at = DateTimeOffset.UnixEpoch;
        var gathering = new LootTrackerSession { Id = "gathering", Kind = LootTrackerSessionKind.Lifeskill, StartedAt = at };
        var monster = new LootTrackerSession { Id = "monster", Kind = LootTrackerSessionKind.Grind, StartedAt = at.AddMinutes(1) };
        var merged = LootTrackerLiveView.MergeSnapshots(new([gathering], at), new([monster], at.AddMinutes(2)));
        Assert.Equal(["monster", "gathering"], merged.Sessions.Select(row => row.Id));
        Assert.Equal(at.AddMinutes(2), merged.At);
        var withEmptySource = LootTrackerLiveView.MergeSnapshots(new(default, null), merged);
        Assert.Equal(merged.Sessions.Select(row => row.Id), withEmptySource.Sessions.Select(row => row.Id));
        Assert.Equal(merged.At, withEmptySource.At);
    }

    [Fact]
    public Task HostUsesOneSharedNetworkSourceAndCreatesAnIndependentOverlayWithoutStartingTheApp() => RunSta(() =>
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            LootTrackingEnabled = false, MonsterLootTrackingEnabled = false, LootOverlayEnabled = false,
            CheckUpdatesOnStart = false, MapTrackingActive = false
        });
        using var host = new MainForm(); // Never Show: no WebView startup, passive capture or game window.
        var network = host.Services.GetRequiredService<NetworkLootService>();
        Assert.Same(network, host.Services.GetRequiredService<INetworkFarmSource>());
        Assert.Same(host.Services.GetRequiredService<FarmLootTrackerService>(), host.Services.GetRequiredService<ILootOverlaySource>());
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
