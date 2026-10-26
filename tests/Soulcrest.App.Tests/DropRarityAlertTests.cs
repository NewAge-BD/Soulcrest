using System.Collections.Concurrent;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>All playback is faked; these regressions never emit sound or open the game.</summary>
public sealed class DropRarityAlertTests
{
    private static AcceptedFarmLootDrop Drop(int itemId) => new("session", "profile", DateTimeOffset.UtcNow,
        $"Item:{itemId}", "Example drop", 1);
    private static RareDropAlertOptions Options(Dictionary<LootItemRarity, DropRarityAlertSettings> tiers) =>
        new(Volume: .35) { RarityAlerts = tiers };

    [Fact]
    public async Task ExactTierChoiceDoesNotApplyToTheNextRarityAndIndependentSoundMuteKeepsNotice()
    {
        var played = new ConcurrentQueue<Audio>();
        var options = Options(new()
        {
            [LootItemRarity.Unique] = new(Enabled: false),
            [LootItemRarity.Epic] = new(Sound: "custom", SoundFile: @"C:\Sounds\epic.mp3"),
            [LootItemRarity.Special] = new(SoundEnabled: false)
        });
        using var service = new RareDropAlertService(() => options, () =>
        { var audio = new Audio(); played.Enqueue(audio); return audio; }, TimeProvider.System,
            soundFileExists: _ => true);
        service.SetPresentationVisible(true);
        service.Accept(Drop(110120003));
        Assert.Empty(service.CurrentNotices); Assert.Empty(played);
        service.Accept(Drop(514310110));
        await WaitFor(() => played.Count == 1 && played.Single().Path is not null);
        Assert.Equal(@"C:\Sounds\epic.mp3", played.Single().Path);
        Assert.Equal(.35, played.Single().Volume);
        Assert.Equal(LootItemRarity.Epic, service.Current!.Rarity);
        service.Accept(Drop(310900001));
        Assert.Equal(LootItemRarity.Special, service.Current!.Rarity);
        Assert.Single(played);
    }

    [Theory]
    [InlineData(LootItemRarity.Unique)]
    [InlineData(LootItemRarity.Epic)]
    [InlineData(LootItemRarity.Mythic)]
    [InlineData(LootItemRarity.Special)]
    public async Task ExplicitTierPreviewUsesOwnChoiceWhileHiddenAndCreatesNoReceiptOrNotice(LootItemRarity rarity)
    {
        var audio = new Audio();
        var path = $@"C:\Sounds\{rarity}.mp3";
        var options = Options(new() { [rarity] = new(Sound: "custom", SoundFile: path) });
        using var service = new RareDropAlertService(() => options, () => audio, TimeProvider.System,
            soundFileExists: _ => true);
        service.SetPresentationVisible(false);
        Assert.True(await service.PreviewAsync(rarity));
        Assert.Equal(path, audio.Path); Assert.False(audio.WavePlayed);
        Assert.Empty(service.CurrentNotices);
        Assert.Equal("Drop-Ton abgespielt.", service.Status);
        Assert.True(audio.Disposed);
    }

    [Theory]
    [InlineData(@"C:\Sounds\missing.mp3")]
    [InlineData("https://example.invalid/sound.mp3")]
    [InlineData(@"\\server\share\sound.mp3")]
    [InlineData(@"C:\Sounds\wrong.wav")]
    public async Task UnavailableOrInvalidCustomChoiceFallsBackToOwnStandardCueWithVisibleStatus(string path)
    {
        var audio = new Audio();
        var options = Options(new() { [LootItemRarity.Unique] = new(Sound: "custom", SoundFile: path) });
        using var service = new RareDropAlertService(() => options, () => audio, TimeProvider.System,
            soundFileExists: _ => false);
        Assert.True(await service.PreviewAsync(LootItemRarity.Unique));
        Assert.True(audio.WavePlayed); Assert.Null(audio.Path);
        Assert.Equal("Eigene MP3 nicht verfügbar. Standardton wird verwendet.", service.Status);
    }

    [Fact]
    public async Task DecoderFailureDisposesItsSessionBeforeSynthesizedFallback()
    {
        var sessions = new ConcurrentQueue<Audio>();
        var options = Options(new() { [LootItemRarity.Unique] = new(Sound: "custom", SoundFile: @"C:\Sounds\bad.mp3") });
        using var service = new RareDropAlertService(() => options, () =>
        { var audio = new Audio { FailFile = true }; sessions.Enqueue(audio); return audio; }, TimeProvider.System,
            soundFileExists: _ => true);
        Assert.True(await service.PreviewAsync(LootItemRarity.Unique));
        Assert.Equal(2, sessions.Count); Assert.All(sessions, audio => Assert.True(audio.Disposed));
        Assert.NotNull(sessions.First().Path); Assert.True(sessions.Last().WavePlayed);
        Assert.Equal("Eigene MP3 konnte nicht abgespielt werden. Standardton wird verwendet.", service.Status);
    }

    [Fact]
    public async Task TierSettingsChangeCancelsPendingPreviewAndNeverReplaysAfterEnable()
    {
        var audio = new Audio { Block = true };
        var options = Options(new());
        using var service = new RareDropAlertService(() => options, () => audio, TimeProvider.System);
        var preview = service.PreviewAsync(LootItemRarity.Unique);
        await WaitFor(() => audio.WavePlayed);
        service.SetPresentationVisible(false); // hidden overlay cannot cancel an explicitly requested preview
        Assert.False(preview.IsCompleted);
        options = options with { RarityAlerts = new Dictionary<LootItemRarity, DropRarityAlertSettings>
            { [LootItemRarity.Unique] = new(SoundEnabled: false) } };
        service.ApplySettings();
        Assert.False(await preview); Assert.True(audio.Disposed);
        Assert.Empty(service.CurrentNotices);
        options = Options(new()); service.ApplySettings();
        Assert.Empty(service.CurrentNotices);
    }

    [Theory]
    [InlineData(false, true, .5)]
    [InlineData(true, false, .5)]
    [InlineData(true, true, 0)]
    public async Task MastersAlsoMuteExplicitPreview(bool enabled, bool sound, double volume)
    {
        var options = new RareDropAlertOptions(enabled, sound, Volume: volume);
        using var service = new RareDropAlertService(() => options,
            () => throw new InvalidOperationException("No audio when muted"), TimeProvider.System);
        Assert.False(await service.PreviewAsync(LootItemRarity.Unique));
        Assert.Equal("Drop-Ton ist deaktiviert.", service.Status);
    }

    [Fact]
    public async Task NewPreviewReplacesPreviousChannelAndDisposeCancelsTheNewest()
    {
        var sessions = new ConcurrentQueue<Audio>();
        using var service = new RareDropAlertService(() => new(), () =>
        { var audio = new Audio { Block = true }; sessions.Enqueue(audio); return audio; }, TimeProvider.System);
        var first = service.PreviewAsync(LootItemRarity.Unique);
        await WaitFor(() => sessions.Count == 1 && sessions.First().WavePlayed);
        var second = service.PreviewAsync(LootItemRarity.Epic);
        await WaitFor(() => sessions.Count == 2 && sessions.Last().WavePlayed);
        Assert.False(await first); Assert.True(sessions.First().Disposed);
        service.Dispose(); Assert.False(await second); Assert.True(sessions.Last().Disposed);
        Assert.False(await service.PreviewAsync(LootItemRarity.Special));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class Audio : IRareDropAudioSession
    {
        internal bool Block, FailFile;
        internal volatile bool WavePlayed, Disposed;
        internal volatile string? Path;
        internal double Volume;
        public Task PlayAsync(byte[] wave, double volume, CancellationToken cancellation)
        { Volume = volume; WavePlayed = true; return Finish(cancellation); }
        public Task PlayFileAsync(string path, double volume, CancellationToken cancellation)
        {
            Volume = volume; Path = path;
            return FailFile ? Task.FromException(new InvalidDataException("Synthetic decode failure")) : Finish(cancellation);
        }
        private Task Finish(CancellationToken cancellation) => Block ? Task.Delay(Timeout.Infinite, cancellation) : Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
}

[Collection("Settings file")]
public sealed class DropRarityAlertSettingsTests
{
    [Fact]
    public void DefaultsKeepAllFourTiersAndSavedExactChoicesSurviveRestart()
    {
        WithSettings(() =>
        {
            File.Delete(AppPaths.SettingsFile);
            var settings = new SettingsService();
            foreach (var rarity in DropRarityAlertSettings.SupportedRarities)
                Assert.Equal(new DropRarityAlertSettings(), settings.GetLootDropRarityAlert(rarity));
            settings.SetLootDropRarityAlert(LootItemRarity.Unique, new(Enabled: false));
            settings.SetLootDropRarityAlert(LootItemRarity.Epic, new(SoundEnabled: false));
            settings.SetLootDropRarityAlert(LootItemRarity.Mythic, new(Sound: "custom", SoundFile: @"C:\Sounds\mythic.mp3"));
            var saved = new SettingsService();
            Assert.False(saved.GetLootDropRarityAlert(LootItemRarity.Unique).Enabled);
            Assert.False(saved.GetLootDropRarityAlert(LootItemRarity.Epic).SoundEnabled);
            Assert.Equal(@"C:\Sounds\mythic.mp3", saved.GetLootDropRarityAlert(LootItemRarity.Mythic).SoundFile);
            Assert.Equal(new DropRarityAlertSettings(), saved.GetLootDropRarityAlert(LootItemRarity.Special));
            Assert.True(saved.Current.LootDropAlertsEnabled); Assert.True(saved.Current.LootDropSoundEnabled);
        });
    }

    [Fact]
    public void InvalidPathsAndUnsupportedTiersAreNormalizedWithoutLosingMissingCustomChoice()
    {
        WithSettings(() =>
        {
            File.WriteAllText(AppPaths.SettingsFile,
                """{"LootDropRarityAlerts":{"Common":{"Enabled":false},"Unique":{"Sound":"custom","SoundFile":"https://example.invalid/a.mp3"},"Epic":null}}""");
            var settings = new SettingsService();
            Assert.False(settings.Current.LootDropRarityAlerts.ContainsKey(LootItemRarity.Common));
            Assert.Equal("custom", settings.GetLootDropRarityAlert(LootItemRarity.Unique).Sound);
            Assert.Null(settings.GetLootDropRarityAlert(LootItemRarity.Unique).SoundFile);
            Assert.Equal(new DropRarityAlertSettings(), settings.GetLootDropRarityAlert(LootItemRarity.Epic));
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.SetLootDropRarityAlert(LootItemRarity.Common, new()));
        });
    }

    [Fact]
    public void SaveFailurePreservesPreviousDictionaryAndDoesNotPublishChanges()
    {
        WithSettings(() =>
        {
            var settings = new SettingsService();
            settings.SetLootDropRarityAlert(LootItemRarity.Unique, new(SoundEnabled: false));
            var previous = JsonSerializer.Serialize(settings.Current);
            var changes = 0; settings.Changed += () => changes++;
            File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
            try
            {
                var error = Record.Exception(() => settings.SetLootDropRarityAlert(LootItemRarity.Unique, new(Enabled: false)));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.Equal(previous, JsonSerializer.Serialize(settings.Current)); Assert.Equal(0, changes);
            }
            finally { Directory.Delete(AppPaths.SettingsFile); }
        });
    }

    private static void WithSettings(Action action)
    {
        var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var language = UiText.Language;
        try { action(); }
        finally
        {
            if (backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, backup);
            UiText.Language = language;
        }
    }
}
