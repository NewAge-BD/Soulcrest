using System.Collections.Immutable;
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
using Soulcrest.App.Services;
using Soulcrest.Core.GenusInsight;
using Xunit;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatGenusGlobalFavoritesViewTests
{
    [Theory]
    [InlineData("de", "Favorit Stats", "1 von 89 Favoriten", "Verfügbare Slots")]
    [InlineData("en", "Favorite Stats", "1 of 89 favorites", "Eligible slots")]
    public async Task GlobalTableListsEveryLocalizedStatOnceAndCountsEligiblePoolsRatherThanRanks(
        string language, string heading, string count, string slotsLabel)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(globals: ["FixingDamage"]);
        var catalog = GenusInsightCatalog.Default;
        Assert.Equal(89, catalog.Stats.Count);
        Assert.Equal(45, catalog.Slots.Count);
        Assert.Equal(catalog.Stats.Keys.Order(StringComparer.Ordinal), GlobalIds(html).Order(StringComparer.Ordinal));
        Assert.Equal(89, GlobalIds(html).Count);
        Assert.Contains(heading, html);
        Assert.Contains(count, html);
        Assert.Contains(slotsLabel, html);
        Assert.True(html.IndexOf("data-genus-applied-status", StringComparison.Ordinal)
            < html.IndexOf("data-genus-global-favorites", StringComparison.Ordinal));
        Assert.True(html.IndexOf("data-genus-global-favorites", StringComparison.Ordinal)
            < html.IndexOf("class=\"cgi-categories\"", StringComparison.Ordinal));
        foreach (var stat in catalog.Stats.Values)
        {
            var expectedPools = catalog.Slots.Count(slot => slot.Options.Any(option => option.StatId == stat.StatId));
            Assert.Contains(language == "de" ? stat.LabelDe : stat.LabelEn, GlobalRow(html, stat.StatId));
            Assert.Contains("data-genus-global-slots=\"" + stat.StatId + "\">" + expectedPools + "</td>", html);
        }
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-global-favorite", "FixingDamage"));
        Assert.Equal(89, Regex.Matches(html, "data-genus-global-favorite=\"").Count);
    }

    [Theory]
    [InlineData("de", "SCHADENSVERSTÄRKUNG")]
    [InlineData("en", "DAMAGE BOOST")]
    public async Task SearchUsesLocalizedLabelsAndNativeInputCallbackWithoutChangingTheSelectedPool(string language, string query)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render(globals: ["FixingDamage"]);
        await fixture.Click("data-genus-category", "special");
        await fixture.Click("data-genus-slot", "5");
        var html = await fixture.Search(query);
        var culture = CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
        var expected = GenusInsightCatalog.Default.Stats.Values
            .Where(stat => culture.CompareInfo.IndexOf(language == "de" ? stat.LabelDe : stat.LabelEn, query, CompareOptions.IgnoreCase) >= 0)
            .OrderBy(stat => language == "de" ? stat.LabelDe : stat.LabelEn, StringComparer.Create(culture, ignoreCase: false))
            .Select(stat => stat.StatId);
        Assert.Equal(expected, GlobalIds(html));
        Assert.Contains("AmplifyAllDamage", GlobalIds(html));
        Assert.Contains("data-genus-options=\"special:5\"", html);
        Assert.Contains(UiText.F("{0} von {1} Favoriten", 1, 89), html);
        html = await fixture.Search("  no-such-localized-stat  ");
        Assert.Empty(GlobalIds(html));
        Assert.Contains(UiText.T("Keine Stats gefunden."), html);
        html = await fixture.Search("");
        Assert.Equal(89, GlobalIds(html).Count);
        Assert.Contains("data-genus-options=\"special:5\"", html);
        Assert.Empty(fixture.GlobalToggled);
        Assert.Empty(fixture.LocalToggled);
    }

    [Fact]
    public async Task ActualGlobalAndLocalCallbacksKeepPoolAndPresetAndUseEffectiveFavorites()
    {
        await using var fixture = new Fixture();
        await fixture.Render(global: true, local: true);
        await fixture.Click("data-genus-category", "special");
        await fixture.Click("data-genus-slot", "5");
        var html = await fixture.Click("data-genus-global-favorite", "FixingDamage");
        Assert.Equal("FixingDamage", Assert.Single(fixture.GlobalToggled));
        Assert.Empty(fixture.LocalToggled);
        Assert.Contains("data-genus-options=\"special:5\"", html);
        Assert.Contains("Preset 2", html);
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-favorite", "FixingDamage"));
        Assert.DoesNotContain("data-genus-favorite-reset=", html);

        var key = new GenusInsightFavoriteKey("special", 5, "FixingDamage");
        html = await fixture.Click("data-genus-favorite", "FixingDamage");
        Assert.Equal(key, Assert.Single(fixture.LocalToggled));
        Assert.Contains("aria-pressed=\"false\"", Button(html, "data-genus-favorite", "FixingDamage"));
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-global-favorite", "FixingDamage"));
        Assert.DoesNotContain("Globale Auswahl verwenden", html);
        Assert.DoesNotContain("Use global selection", html);
        Assert.DoesNotContain("data-genus-favorite-reset=", html);
        html = await fixture.Click("data-genus-slot", "1");
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-favorite", "FixingDamage"));
        Assert.DoesNotContain("data-genus-favorite-reset=", html);
        html = await fixture.Click("data-genus-slot", "5");
        Assert.Contains("aria-pressed=\"false\"", Button(html, "data-genus-favorite", "FixingDamage"));
        Assert.DoesNotContain("data-genus-favorite-reset=", html);
        Assert.Contains("data-genus-options=\"special:5\"", html);
        Assert.Contains("Preset 2", html);
        Assert.Single(fixture.GlobalToggled);
        Assert.Single(fixture.LocalToggled);
        html = await fixture.Click("data-genus-global-favorite", "FixingDamage");
        Assert.Equal(new[] { "FixingDamage", "FixingDamage" }, fixture.GlobalToggled);
        Assert.Contains("aria-pressed=\"false\"", Button(html, "data-genus-global-favorite", "FixingDamage"));
        Assert.Contains("aria-pressed=\"false\"", Button(html, "data-genus-favorite", "FixingDamage"));
        Assert.Contains("data-genus-options=\"special:5\"", html);
    }

    [Fact]
    public async Task ExplicitPositiveAndNegativeEffectiveFavoritesOnlyShowStarsAndAbsentCallbacksDisableEveryAction()
    {
        var catalog = GenusInsightCatalog.Default;
        var stats = catalog.Options("cogni", 1).Select(option => option.StatId).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        var positive = new GenusInsightFavoriteKey("cogni", 1, stats[0]);
        var negative = new GenusInsightFavoriteKey("cogni", 1, stats[1]);
        await using var fixture = new Fixture();
        var html = await fixture.Render(globals: [negative.StatId], overrides: new Dictionary<GenusInsightFavoriteKey, bool>
        { [positive] = true, [negative] = false }, error: "Synthetic favorite save failed");
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-favorite", positive.StatId));
        Assert.Contains("aria-pressed=\"false\"", Button(html, "data-genus-favorite", negative.StatId));
        Assert.DoesNotContain("data-genus-favorite-reset=", html);
        Assert.DoesNotContain("Globale Auswahl verwenden", html);
        Assert.DoesNotContain("Use global selection", html);
        Assert.Contains("Synthetic favorite save failed", html);
        Assert.Contains("role=\"alert\"", html);
        foreach (var attribute in new[] { "data-genus-global-favorite", "data-genus-favorite" })
            Assert.All(Regex.Matches(html, "<button[^>]*" + attribute + "=[^>]*>").Cast<Match>(),
                match => Assert.Contains("disabled", match.Value));
        Assert.Empty(fixture.GlobalToggled);
        Assert.Empty(fixture.LocalToggled);
    }

    [Fact]
    public async Task ExistingEffectiveFavoriteParameterRemainsAuthoritativeForLocalStars()
    {
        await using var fixture = new Fixture();
        await fixture.Render(globals: ["FixingDamage"]);
        await fixture.Click("data-genus-category", "special");
        var html = await fixture.SetEffectiveFavorites(ImmutableHashSet<GenusInsightFavoriteKey>.Empty);
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-global-favorite", "FixingDamage"));
        Assert.Contains("aria-pressed=\"false\"", Button(html, "data-genus-favorite", "FixingDamage"));
    }

    private static IReadOnlyList<string> GlobalIds(string html) => Regex.Matches(html, "data-genus-global-stat=\"([^\"]+)\"")
        .Select(match => match.Groups[1].Value).ToArray();
    private static string GlobalRow(string html, string stat) => Regex.Match(html,
        "<tr[^>]*data-genus-global-stat=\"" + Regex.Escape(stat) + "\"[^>]*>.*?</tr>", RegexOptions.Singleline).Value;
    private static string Button(string html, string attribute, string value) => Regex.Match(html,
        "<button[^>]*" + attribute + "=\"" + Regex.Escape(value) + "\"[^>]*>").Value;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previousLanguage = UiText.Language;
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedGenus? _view;
        private ImmutableHashSet<string> _globals = ImmutableHashSet<string>.Empty;
        private ImmutableDictionary<GenusInsightFavoriteKey, bool> _overrides = ImmutableDictionary<GenusInsightFavoriteKey, bool>.Empty;
        public List<string> GlobalToggled { get; } = [];
        public List<GenusInsightFavoriteKey> LocalToggled { get; } = [];

        public Fixture(string language = "en")
        {
            UiText.Language = language;
            _services = new ServiceCollection().AddLogging().BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public Task<string> Render(IEnumerable<string>? globals = null, IReadOnlyDictionary<GenusInsightFavoriteKey, bool>? overrides = null,
            bool global = false, bool local = false, string? error = null) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _globals = (globals ?? []).ToImmutableHashSet(StringComparer.Ordinal);
            _overrides = overrides?.ToImmutableDictionary() ?? ImmutableDictionary<GenusInsightFavoriteKey, bool>.Empty;
            _root = await _renderer.RenderComponentAsync<CapturedGenus>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedGenus.State)] = new GenusInsightSnapshot { Preset = 2 },
                [nameof(CapturedGenus.Favorites)] = Effective(),
                [nameof(CapturedGenus.GlobalFavoriteStats)] = _globals,
                [nameof(CapturedGenus.FavoriteError)] = error,
                [nameof(CapturedGenus.ToggleGlobalFavorite)] = global ? EventCallback.Factory.Create<string>(this, (Action<string>)ToggleGlobal) : default,
                [nameof(CapturedGenus.ToggleFavorite)] = local ? EventCallback.Factory.Create<GenusInsightFavoriteKey>(this, (Action<GenusInsightFavoriteKey>)ToggleLocal) : default,
                [nameof(CapturedGenus.Captured)] = (Action<CapturedGenus>)(view => _view = view)
            }));
            return Html();
        });
        private ImmutableHashSet<GenusInsightFavoriteKey> Effective() => GenusInsightCatalog.Default.Slots
            .SelectMany(slot => slot.Options.Select(option => new GenusInsightFavoriteKey(slot.Genus, slot.Slot, option.StatId)))
            .Where(key => _overrides.TryGetValue(key, out var value) ? value : _globals.Contains(key.StatId)).ToImmutableHashSet();
        private void Publish() => _view!.SetFavorites(_globals, Effective());
        private void ToggleGlobal(string stat)
        { GlobalToggled.Add(stat); _globals = _globals.Contains(stat) ? _globals.Remove(stat) : _globals.Add(stat); Publish(); }
        private void ToggleLocal(GenusInsightFavoriteKey key)
        { LocalToggled.Add(key); _overrides = _overrides.SetItem(key, !Effective().Contains(key)); Publish(); }
        public Task<string> Click(string attribute, string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Invoke(attribute, value, "onclick", new MouseEventArgs()); return Html(); });
        public Task<string> Search(string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Invoke("data-genus-favorite-search", "global", "oninput", new ChangeEventArgs { Value = value }); return Html(); });
        public Task<string> SetEffectiveFavorites(IReadOnlySet<GenusInsightFavoriteKey> favorites) => _renderer.Dispatcher.InvokeAsync(() =>
        { _view!.SetFavorites(_globals, favorites); return Html(); });
        private string Html() => WebUtility.HtmlDecode(_root.ToHtmlString());
        public async ValueTask DisposeAsync()
        { await _renderer.DisposeAsync(); await _services.DisposeAsync(); UiText.Language = _previousLanguage; }
    }

    public sealed class CapturedGenus : CombatGenusInsightView
    {
        [Parameter] public Action<CapturedGenus>? Captured { get; set; }
        protected override void OnInitialized() => Captured?.Invoke(this);
        internal void SetFavorites(IReadOnlySet<string> globals, IReadOnlySet<GenusInsightFavoriteKey> effective)
        { GlobalFavoriteStats = globals; Favorites = effective; StateHasChanged(); }
#pragma warning disable BL0006 // Exercise the actual callbacks emitted by the production Razor view.
        internal async Task Invoke(string attribute, string value, string eventName, object argument)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatGenusInsightView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == attribute
                && Convert.ToString(frames.Array[i].AttributeValue, CultureInfo.InvariantCulture) == value);
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                .Single(frame => frame.AttributeName == eventName).AttributeValue;
            if (callback is EventCallback<MouseEventArgs> mouse) await mouse.InvokeAsync((MouseEventArgs)argument);
            else if (callback is EventCallback<ChangeEventArgs> change) await change.InvokeAsync((ChangeEventArgs)argument);
            else if (callback is EventCallback untyped) await untyped.InvokeAsync(argument);
            else if (callback is Action synchronous) synchronous();
            else if (callback is Func<Task> asynchronous) await asynchronous();
            else throw new InvalidOperationException("Unexpected Genus favorite production callback.");
            StateHasChanged();
        }
#pragma warning restore BL0006
    }
}
