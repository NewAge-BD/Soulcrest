using System.Net;
using System.Reflection;
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
public sealed class AchievementOverlayOptionsViewTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task TimerFieldShowsTwentySecondsAndPersistsChanges(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render();
        Assert.Contains("min=\"5\" max=\"300\" step=\"1\"", html);
        Assert.Contains("value=\"20\"", html);
        Assert.Contains("aria-busy=\"false\"", html);
        Assert.Contains(UiText.T("Favoriten bleiben dauerhaft sichtbar."), html);
        html = await fixture.Change("40");
        Assert.Equal(40, new SettingsService().Current.AchievementOverlayDisplaySeconds);
        Assert.Contains(UiText.T("Anzeigedauer gespeichert."), html);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("4")]
    [InlineData("301")]
    [InlineData("20.5")]
    public async Task InvalidInputPreservesTheSavedValueAndExplainsTheRange(string value)
    {
        await using var fixture = new Fixture("en");
        await fixture.Render();
        var html = await fixture.Change(value);
        Assert.Equal(20, fixture.Settings.Current.AchievementOverlayDisplaySeconds);
        Assert.Contains("value=\"20\"", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains(UiText.T("Bitte eine ganze Zahl zwischen 5 und 300 eingeben."), html);
    }

    [Fact]
    public async Task BusyParentDisablesInputAndFailedSaveShowsGenericFeedback()
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.Render(busy: true);
        Assert.Contains("disabled", html);
        await fixture.Change("40");
        Assert.Equal(20, fixture.Settings.Current.AchievementOverlayDisplaySeconds);
        await fixture.Render();
        File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            html = await fixture.Change("40");
            Assert.Equal(20, fixture.Settings.Current.AchievementOverlayDisplaySeconds);
            Assert.Contains(UiText.T("Anzeigedauer konnte nicht gespeichert werden."), html);
            Assert.DoesNotContain(AppPaths.SettingsFile, html);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); }
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportProductionAchievementSettings(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_ACHIEVEMENT_SETTINGS_RENDER_DIRECTORY"));
        await using var fixture = new Fixture(language);
        var html = await fixture.Render();
        var wwwroot = Path.Combine(TestPrerequisites.RepositoryRoot, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "css/overlay.css" }.Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "achievement-settings-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<base href=\"" + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri) + "\"><style>" + css
            + "\nhtml,body{height:auto}body{padding:24px}</style></head><body>" + html + "</body></html>");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedOptions? _view;
        internal SettingsService Settings { get; }
        internal Fixture(string language)
        {
            var realData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");
            Assert.NotEqual(Path.GetFullPath(realData), Path.GetFullPath(AppPaths.DataDirectory));
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.Delete(AppPaths.SettingsFile);
            Settings = new SettingsService(); Settings.Current.UiLanguage = language; UiText.Language = language;
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(Settings);
            _provider = services.BuildServiceProvider(); _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        internal Task<string> Render(bool busy = false) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await _renderer.RenderComponentAsync<CapturedOptions>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(CapturedOptions.Capture)] = (Action<CapturedOptions>)(view => _view = view), [nameof(CapturedOptions.Busy)] = busy }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        internal Task<string> Change(string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(AchievementOverlayOptions).GetMethod("SetDuration", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_view, [new ChangeEventArgs { Value = value }])!;
            _view!.Refresh();
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _provider.DisposeAsync();
            if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _language;
        }
    }
    public sealed class CapturedOptions : AchievementOverlayOptions
    {
        [Parameter] public Action<CapturedOptions>? Capture { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Capture?.Invoke(this); }
        public void Refresh() => StateHasChanged();
    }
}
