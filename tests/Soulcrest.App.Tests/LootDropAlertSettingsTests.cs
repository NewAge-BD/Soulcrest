using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootDropAlertSettingsTests
{
    [Theory]
    [InlineData("de", "Seltenheits-Alerts", "Fundhinweis", "Ton testen")]
    [InlineData("en", "Rarity alerts", "Drop notice", "Test sound")]
    public async Task FourExceptionalTiersRenderLocalizedWithoutChangingSettings(string language, string title, string notice, string preview)
    {
        await using var fixture = new LootAlertUiFixture(language);
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var fileBefore = File.ReadAllBytes(AppPaths.SettingsFile);
        var html = await fixture.RenderSettings();
        Assert.Contains(title, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(notice, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(preview, html, StringComparison.OrdinalIgnoreCase);
        foreach (var tier in DropRarityAlertSettings.SupportedRarities)
        {
            var key = tier.ToString().ToLowerInvariant();
            Assert.Contains($"data-rarity-alert=\"{key}\"", html);
            Assert.Contains($"id=\"loot-rarity-{key}-enabled\"", html);
        }
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(html, "data-rarity-alert=").Count);
        Assert.DoesNotContain("data-rarity-alert=\"common\"", html);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Equal(fileBefore, File.ReadAllBytes(AppPaths.SettingsFile));
        Assert.Equal(0, fixture.SharedAudioCalls);
        Assert.Equal(0, fixture.DropAudioCalls);
    }

    [Theory]
    [InlineData(LootItemRarity.Unique)]
    [InlineData(LootItemRarity.Epic)]
    [InlineData(LootItemRarity.Mythic)]
    [InlineData(LootItemRarity.Special)]
    public async Task Mp3MuteAndNoticeChoicesPersistOnlyForTheSelectedTier(LootItemRarity tier)
    {
        await using var fixture = new LootAlertUiFixture();
        await fixture.RenderSettings();
        var others = DropRarityAlertSettings.SupportedRarities.Where(other => other != tier)
            .ToDictionary(other => other, fixture.Settings.GetLootDropRarityAlert);

        await fixture.InvokeSettings("ChangeSound", tier, new AlertSoundPicker.Selection("custom", fixture.SoundFile));
        var saved = new SettingsService();
        Assert.Equal("custom", saved.GetLootDropRarityAlert(tier).Sound);
        Assert.Equal(fixture.SoundFile, saved.GetLootDropRarityAlert(tier).SoundFile);
        Assert.True(saved.GetLootDropRarityAlert(tier).SoundEnabled);
        await fixture.InvokeSettings("ChangeSound", tier, new AlertSoundPicker.Selection("custom", fixture.SoundFile, false));
        saved = new SettingsService();
        Assert.False(saved.GetLootDropRarityAlert(tier).SoundEnabled);
        Assert.True(saved.GetLootDropRarityAlert(tier).Enabled);
        Assert.Equal(fixture.SoundFile, saved.GetLootDropRarityAlert(tier).SoundFile);
        await fixture.InvokeSettings("ChangeEnabled", tier, new ChangeEventArgs { Value = false });
        saved = new SettingsService();
        Assert.False(saved.GetLootDropRarityAlert(tier).Enabled);
        foreach (var (other, expected) in others) Assert.Equal(expected, saved.GetLootDropRarityAlert(other));
        Assert.True(saved.Current.LootDropAlertsEnabled);
        Assert.True(saved.Current.LootDropSoundEnabled);
        Assert.Equal(0, fixture.SharedAudioCalls);
        Assert.Equal(0, fixture.DropAudioCalls);
    }

    [Fact]
    public async Task FailedSaveShowsErrorAndRetainsPriorChoicesWithoutPublishingAChange()
    {
        await using var fixture = new LootAlertUiFixture();
        fixture.Settings.SetLootDropRarityAlert(LootItemRarity.Unique, new(SoundEnabled: false));
        await fixture.RenderSettings();
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var changes = 0;
        fixture.Settings.Changed += () => changes++;
        File.Delete(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            var html = await fixture.InvokeSettings("ChangeEnabled", LootItemRarity.Unique, new ChangeEventArgs { Value = false });
            Assert.Contains("role=\"alert\"", html);
            Assert.Contains("could not be saved", html, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
            Assert.Equal(0, changes);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); }
    }

    [Fact]
    public async Task DisabledSettingsIncludingExampleViewsRejectChangesAndPreview()
    {
        await using var fixture = new LootAlertUiFixture();
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        var html = await fixture.RenderSettings(disabled: true);
        Assert.Contains("disabled", html);
        await fixture.InvokeSettings("ChangeSound", LootItemRarity.Epic, new AlertSoundPicker.Selection("custom", fixture.SoundFile));
        await fixture.InvokeSettings("ChangeEnabled", LootItemRarity.Unique, new ChangeEventArgs { Value = false });
        Assert.False(await fixture.PreviewTier(LootItemRarity.Unique));
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Equal(0, fixture.SharedAudioCalls);
        Assert.Equal(0, fixture.DropAudioCalls);
    }

    [Fact]
    public async Task TierPreviewUsesDedicatedDropChannelAndDoesNotCreateANotice()
    {
        await using var fixture = new LootAlertUiFixture();
        fixture.Settings.SetLootDropRarityAlert(LootItemRarity.Mythic, new(Sound: "custom", SoundFile: fixture.SoundFile));
        await fixture.RenderSettings();
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        Assert.True(await fixture.PreviewTier(LootItemRarity.Mythic));
        Assert.Equal(fixture.SoundFile, fixture.LastDropAudioFile);
        Assert.Equal(1, fixture.DropAudioCalls);
        Assert.Equal(0, fixture.SharedAudioCalls);
        Assert.Empty(fixture.DropAlerts.CurrentNotices);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
    }
}

internal sealed class LootAlertUiFixture : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-loot-alert-ui-" + Guid.NewGuid().ToString("N"));
    private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _previousLanguage = UiText.Language;
    private readonly ServiceProvider _provider;
    private readonly HtmlRenderer _renderer;
    private readonly AlertSoundPlayer _sounds;
    private HtmlRootComponent _root;
    private CapturedLootDropAlertSettings? _settingsView;
    private CapturedDropSoundPicker? _picker;
    public SettingsService Settings { get; }
    public AlertsService Alerts { get; }
    public RareDropAlertService DropAlerts { get; }
    public string SoundFile => Path.Combine(_directory, "tier-tone.mp3");
    public string AlertsFile => Path.Combine(_directory, "alerts.json");
    public AlertSoundFileChoice Choice { get; set; } = new();
    public int SharedAudioCalls, DropAudioCalls, PickerCalls;
    public string? LastDropAudioFile;

    public LootAlertUiFixture(string language = "en")
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(SoundFile, [1, 2, 3]); // Decoder is always faked; no real sound is played.
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings { UiLanguage = language, LootDropAlertsEnabled = true,
            LootDropSoundEnabled = true, LootDropRarityAlerts = [] });
        Settings = new SettingsService();
        UiText.Language = language;
        Alerts = new AlertsService(AlertsFile);
        DropAlerts = new RareDropAlertService(() => new(Settings.Current.LootDropAlertsEnabled,
            Settings.Current.LootDropSoundEnabled, Volume: Settings.Current.LootDropSoundVolume)
            { RarityAlerts = Settings.Current.LootDropRarityAlerts }, () => new DropAudio(this), TimeProvider.System);
        _sounds = new AlertSoundPlayer(() => new SharedAudio(this), _ => SharedAudioCalls++,
            () => { PickerCalls++; return Task.FromResult(Choice); }, () => new SharedAudio(this));
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton(Settings); services.AddSingleton(Alerts);
        services.AddSingleton(_sounds); services.AddSingleton(DropAlerts);
        _provider = services.BuildServiceProvider();
        _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
    }

    public async Task<string> RenderSettings(bool disabled = false)
    {
        _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedLootDropAlertSettings>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedLootDropAlertSettings.Captured)] = (Action<CapturedLootDropAlertSettings>)(component => _settingsView = component),
                [nameof(LootDropAlertSettings.Disabled)] = disabled
            })));
        return await Markup();
    }

    public async Task<string> InvokeSettings(string method, params object[] arguments)
    {
        await _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(LootDropAlertSettings).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_settingsView, arguments);
            if (result is Task task) await task;
            await _settingsView!.RefreshNow();
        });
        return await Markup();
    }

    public Task<bool> PreviewTier(LootItemRarity tier) => _renderer.Dispatcher.InvokeAsync(() =>
        (Task<bool>)typeof(LootDropAlertSettings).GetMethod("Preview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_settingsView, [tier])!);

    public async Task<string> RenderPicker(Action<AlertSoundPicker.Selection> changed, Func<Task<bool>>? preview = null,
        Func<string?>? status = null, bool disabled = false)
    {
        _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedDropSoundPicker>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedDropSoundPicker.Captured)] = (Action<CapturedDropSoundPicker>)(component => _picker = component),
                [nameof(AlertSoundPicker.Changed)] = EventCallback.Factory.Create<AlertSoundPicker.Selection>(this, changed),
                [nameof(AlertSoundPicker.PreviewSound)] = preview,
                [nameof(AlertSoundPicker.PreviewStatus)] = status,
                [nameof(AlertSoundPicker.Disabled)] = disabled
            })));
        return await Markup();
    }

    public async Task<string> InvokePicker(string method, params object[] arguments)
    {
        await _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(AlertSoundPicker).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_picker, arguments);
            if (result is Task task) await task;
            await _picker!.RefreshNow();
        });
        return await Markup();
    }

    private async Task<string> Markup() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync(); await _provider.DisposeAsync();
        DropAlerts.Dispose(); Alerts.Dispose(); _sounds.Dispose();
        if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
        UiText.Language = _previousLanguage;
        Directory.Delete(_directory, true);
    }

    private sealed class SharedAudio(LootAlertUiFixture fixture) : IAlertAudioSession
    {
        public event Action<Exception>? Failed { add { } remove { } }
        public Task StartAsync(string path, CancellationToken cancellation) { fixture.SharedAudioCalls++; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class DropAudio(LootAlertUiFixture fixture) : IRareDropAudioSession
    {
        public Task PlayAsync(byte[] wave, double volume, CancellationToken cancellation) { fixture.DropAudioCalls++; return Task.CompletedTask; }
        public Task PlayFileAsync(string path, double volume, CancellationToken cancellation)
        { fixture.DropAudioCalls++; fixture.LastDropAudioFile = path; return Task.CompletedTask; }
        public void Dispose() { }
    }
}

public sealed class CapturedLootDropAlertSettings : LootDropAlertSettings
{
    [Parameter] public Action<CapturedLootDropAlertSettings>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}

public sealed class CapturedDropSoundPicker : AlertSoundPicker
{
    [Parameter] public Action<CapturedDropSoundPicker>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}
