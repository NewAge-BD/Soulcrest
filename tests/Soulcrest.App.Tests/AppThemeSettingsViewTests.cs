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
public sealed class AppThemeSettingsViewTests
{
    [Theory]
    [InlineData("de", "Wähle dein bevorzugtes Design.", "Ausgewählt")]
    [InlineData("en", "Choose your preferred appearance.", "Selected")]
    public async Task PreviewChoicesRenderLocalizedWithNativeRadiosWithoutSaving(string language, string description, string selected)
    {
        await using var fixture = new AppThemeUiFixture(language);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var html = await fixture.Render();
        Assert.Contains("Themes", html);
        Assert.Contains(description, html);
        Assert.Contains(selected, html);
        Assert.Contains("role=\"radiogroup\"", html);
        Assert.Contains("aria-labelledby=\"app-theme-heading\"", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "type=\"radio\"").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "is-selected"));
        foreach (var theme in AppThemeDefinition.All)
        {
            Assert.Contains($"id=\"app-theme-{theme.Id}\"", html);
            Assert.Contains($"for=\"app-theme-{theme.Id}\"", html);
            Assert.Contains($"data-preview-theme=\"{theme.Id}\"", html);
        }
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    }

    [Fact]
    public async Task SelectingDarkSavesImmediatelyShowsFeedbackAndLeavesOverlayThemeUnchanged()
    {
        await using var fixture = new AppThemeUiFixture();
        await fixture.Render();
        var html = await fixture.Select(AppThemeDefinition.DarkId);
        Assert.Equal(AppThemeDefinition.DarkId, fixture.Settings.Current.AppTheme);
        Assert.Equal(AppThemeDefinition.DarkId, new SettingsService().Current.AppTheme);
        Assert.Equal("runic", fixture.Settings.Current.OverlayTheme);
        Assert.Contains("Theme saved.", html);
        Assert.Contains("role=\"status\"", html);
        Assert.Contains("app-theme-choice is-selected\" for=\"app-theme-graphite-dark\"", html);
    }

    [Fact]
    public async Task FailedSaveKeepsThePreviousSelectionAndShowsVisibleError()
    {
        await using var fixture = new AppThemeUiFixture();
        await fixture.Render();
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        File.Delete(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            var html = await fixture.Select(AppThemeDefinition.DarkId);
            Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
            Assert.Contains("The theme could not be saved.", html);
            Assert.Contains("role=\"alert\"", html);
            Assert.Contains("app-theme-choice is-selected\" for=\"app-theme-soulcrest-classic\"", html);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); }
    }

    [Fact]
    public async Task ExistingViewTracksExternalThemeChanges()
    {
        await using var fixture = new AppThemeUiFixture();
        await fixture.Render();
        await fixture.ChangeExternally(AppThemeDefinition.DarkId);
        Assert.Contains("app-theme-choice is-selected\" for=\"app-theme-graphite-dark\"", await fixture.Markup());
    }
}

internal sealed class AppThemeUiFixture : IAsyncDisposable
{
    private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;
    private readonly ServiceProvider _provider;
    private readonly HtmlRenderer _renderer;
    private HtmlRootComponent _root;
    private CapturedAppThemeSettings? _view;
    public SettingsService Settings { get; }

    public AppThemeUiFixture(string language = "en")
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOULCREST_DATA")));
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            SettingsRevision = 5, MonsterLootAutoSessionRevision = 1, UiLanguage = language,
            OverlayTheme = "runic", AppTheme = AppThemeDefinition.DefaultId
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SettingsService>();
        _provider = services.BuildServiceProvider();
        Settings = _provider.GetRequiredService<SettingsService>();
        _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
    }

    public async Task<string> Render()
    {
        _root = await _renderer.Dispatcher.InvokeAsync(async () =>
            await _renderer.RenderComponentAsync<CapturedAppThemeSettings>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedAppThemeSettings.Captured)] = (Action<CapturedAppThemeSettings>)(view => _view = view)
            })));
        return await Markup();
    }

    public async Task<string> Select(string theme)
    {
        await _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(AppThemeSettings).GetMethod("ChangeTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, [theme]);
            if (result is Task task) await task;
            await _view!.RefreshNow();
        });
        return await Markup();
    }

    public Task ChangeExternally(string theme) => _renderer.Dispatcher.InvokeAsync(() => Settings.SetAppTheme(theme));
    public async Task<string> Markup() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _provider.DisposeAsync();
        if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        UiText.Language = _language;
    }
}

public sealed class CapturedAppThemeSettings : AppThemeSettings
{
    [Parameter] public Action<CapturedAppThemeSettings>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}
