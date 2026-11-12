using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class GenusGlobalFavoriteWorkspaceTests
{
    [Fact]
    public async Task WorkspaceUsesTheAcceptedGlobalAndLocalPreferencesWithoutCharacterOrMenuServices()
    {
        await using var f = new Fixture();
        var html = await f.Render();
        Assert.Contains("Favorite Stats", html);
        Assert.DoesNotContain("disabled", Button(html, "data-genus-global-favorite", "FixingDamage"));
        var reads = f.Source.Reads;

        html = await f.Change("ToggleGlobalGenusFavorite", "FixingDamage");
        Assert.Contains("FixingDamage", f.Favorites!.Snapshot.GlobalStats);
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-global-favorite", "FixingDamage"));
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-favorite", "FixingDamage"));

        var key = new GenusInsightFavoriteKey("cogni", 1, "FixingDamage");
        html = await f.Change("ToggleGenusFavorite", key);
        Assert.False(f.Favorites.Snapshot.Contains(key));
        Assert.Contains(key.StatId, f.Favorites.Snapshot.GlobalStats);
        Assert.Contains("aria-pressed=\"false\"", Button(html, "data-genus-favorite", key.StatId));
        Assert.DoesNotContain("data-genus-favorite-reset", html);

        html = await f.Change("ToggleGenusFavorite", key);
        Assert.True(f.Favorites.Snapshot.Contains(key));
        Assert.DoesNotContain("data-genus-favorite-reset", html);
        Assert.Equal(reads, f.Source.Reads);
        Assert.Equal("genus-insight", f.View!.Section);
    }

    [Fact]
    public async Task WorkspaceRefreshesForExternalFavoriteChangesEvenWithoutTheGenusStateService()
    {
        await using var f = new Fixture();
        await f.Render();
        await Task.Run(() => f.Favorites!.ToggleGlobal("FixingDamage"));
        var html = await f.Html();
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-global-favorite", "FixingDamage"));
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-favorite", "FixingDamage"));
    }

    [Fact]
    public async Task FailedGlobalSaveKeepsTheOldSelectionAndShowsTheProductionError()
    {
        await using var f = new Fixture(failSave: true);
        await f.Render();
        var html = await f.Change("ToggleGlobalGenusFavorite", "FixingDamage");
        Assert.Empty(f.Favorites!.Snapshot.GlobalStats);
        Assert.Contains("aria-pressed=\"false\"", Button(html, "data-genus-global-favorite", "FixingDamage"));
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains(UiText.T("Favorit konnte nicht gespeichert werden. Bitte erneut versuchen."), html);
        Assert.DoesNotContain(f.Path, html);
    }

    [Fact]
    public async Task MissingFavoriteServiceDisablesBothGlobalAndLocalMutationCallbacks()
    {
        await using var f = new Fixture(includeFavorites: false);
        var html = await f.Render();
        Assert.Contains("disabled", Button(html, "data-genus-global-favorite", "FixingDamage"));
        Assert.Contains("disabled", Button(html, "data-genus-favorite", "FixingDamage"));
        Assert.False(File.Exists(f.Path));
    }

    private static string Button(string html, string attribute, string value) => Regex.Match(html,
        "<button[^>]*" + attribute + "=\"" + Regex.Escape(value) + "\"[^>]*>").Value;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _language = UiText.Language;
        private readonly OwnSkillStateService _skills;
        private readonly CharacterCombatStateService _character;
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-global-favorite-ui-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_folder, "favorites.json");
        internal GenusInsightFavoriteService? Favorites { get; }
        internal QuietSource Source { get; } = new();
        internal CapturedWorkspace? View { get; private set; }
        internal Fixture(bool includeFavorites = true, bool failSave = false)
        {
            Directory.CreateDirectory(_folder);
            UiText.Language = "en";
            _skills = new(Source, static _ => null);
            _character = new(Source, _skills, static _ => null);
            var services = new ServiceCollection().AddLogging().AddSingleton(_character);
            if (includeFavorites)
            {
                Favorites = new(Path, (path, value) =>
                {
                    if (failSave) throw new IOException("Synthetic private path must remain hidden.");
                    JsonFile.Save(path, value);
                });
                services.AddSingleton(Favorites);
            }
            _services = services.BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        internal Task<string> Render() => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await _renderer.RenderComponentAsync<CapturedWorkspace>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CombatScoreWorkspaceView.Section)] = "genus-insight",
                [nameof(CapturedWorkspace.Captured)] = (Action<CapturedWorkspace>)(view => View = view)
            }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        internal Task<string> Change<T>(string callback, T value) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            await View!.Change(callback, value);
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        internal Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _services.DisposeAsync(); _character.Dispose(); _skills.Dispose();
            UiText.Language = _language; Directory.Delete(_folder, true);
        }
    }

    public sealed class CapturedWorkspace : CombatScoreWorkspaceView
    {
        [Parameter] public Action<CapturedWorkspace>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
#pragma warning disable BL0006 // Exercise the actual callbacks forwarded to the production category component.
        internal async Task Change<T>(string name, T value)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatScoreWorkspaceView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var frame = Enumerable.Range(0, frames.Count).Select(index => frames.Array[index]).Single(frame =>
                frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name);
            await Assert.IsType<EventCallback<T>>(frame.AttributeValue).InvokeAsync(value);
            StateHasChanged();
        }
#pragma warning restore BL0006
    }

    private sealed class QuietSource : INetworkCharacterCombatSource, INetworkSkillSource
    {
        internal int Reads;
        public bool Running => false;
        public NetworkCharacterCombatSnapshot CombatSnapshot { get { Interlocked.Increment(ref Reads); return NetworkCharacterCombatSnapshot.Empty; } }
        public NetworkSkillSnapshot SkillSnapshot { get { Interlocked.Increment(ref Reads); return NetworkSkillSnapshot.Empty; } }
        public event Action? Changed { add { } remove { } }
        public event Action? OwnCharacterChanged { add { } remove { } }
    }
}
