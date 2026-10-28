using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AbyssRankMotionSettingsTests
{
    [Fact]
    public void IndependentMotionAndAudioChoicesPersistAndClampWithoutChangingDropSettings()
    {
        WithSettings(() =>
        {
            var settings = new SettingsService();
            Assert.False(settings.Current.AbyssRankReducedMotion);
            Assert.True(settings.Current.AbyssRankSoundEnabled);
            Assert.Equal(.6, settings.Current.AbyssRankSoundVolume);
            var dropMotion = settings.Current.LootDropAnimationsEnabled;
            var dropSound = settings.Current.LootDropSoundEnabled;
            var dropVolume = settings.Current.LootDropSoundVolume;
            settings.SetAbyssRankReducedMotion(true);
            settings.SetAbyssRankSoundEnabled(false);
            settings.SetAbyssRankSoundVolume(10);
            var saved = new SettingsService().Current;
            Assert.True(saved.AbyssRankReducedMotion);
            Assert.False(saved.AbyssRankSoundEnabled);
            Assert.Equal(1, saved.AbyssRankSoundVolume);
            Assert.Equal(dropMotion, saved.LootDropAnimationsEnabled);
            Assert.Equal(dropSound, saved.LootDropSoundEnabled);
            Assert.Equal(dropVolume, saved.LootDropSoundVolume);
            settings.SetAbyssRankSoundVolume(double.NaN);
            Assert.Equal(.6, new SettingsService().Current.AbyssRankSoundVolume);
        });
    }

    [Fact]
    public void FailedSaveRollsBackMotionAndDoesNotPublishChangedEvent()
    {
        WithSettings(() =>
        {
            var settings = new SettingsService();
            var changes = 0;
            settings.Changed += () => changes++;
            File.Delete(AppPaths.SettingsFile);
            Directory.CreateDirectory(AppPaths.SettingsFile);
            try
            {
                Assert.ThrowsAny<Exception>(() => settings.SetAbyssRankReducedMotion(true));
                Assert.False(settings.Current.AbyssRankReducedMotion);
                Assert.Equal(0, changes);
            }
            finally { Directory.Delete(AppPaths.SettingsFile); }
        });
    }

    [Theory]
    [InlineData("de", "Reduzierte Bewegung", "Orchestraler Aufstiegsklang")]
    [InlineData("en", "Reduced motion", "Orchestral rank-up sound")]
    public async Task SettingsRenderLocalizedWithoutPlayingOrMutatingFiles(string language, string motion, string sound)
    {
        var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var oldLanguage = UiText.Language;
        try
        {
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings { UiLanguage = language });
            var before = File.ReadAllBytes(AppPaths.SettingsFile);
            var settings = new SettingsService();
            UiText.Language = language;
            using var player = new AbyssRankSoundPlayer(() => new(),
                () => throw new InvalidOperationException("Rendering must not play audio"), (_, _, _) => throw new InvalidOperationException());
            var services = new ServiceCollection();
            services.AddLogging(); services.AddSingleton(settings); services.AddSingleton(player);
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<AbyssRankMotionSettings>(ParameterView.Empty)).ToHtmlString());
            html = WebUtility.HtmlDecode(html);
            Assert.Contains(motion, html); Assert.Contains(sound, html);
            Assert.Contains("abyss-rank-sound-volume", html);
            Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
        }
        finally
        {
            UiText.Language = oldLanguage;
            if (backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, backup);
        }
    }

    private static void WithSettings(Action action)
    {
        var backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        try { JsonFile.Save(AppPaths.SettingsFile, new AppSettings()); action(); }
        finally { if (backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, backup); }
    }
}
