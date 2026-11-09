using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Pantheon;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatPantheonViewTests
{
    [Fact]
    public async Task MissingFactionCannotBorrowBothFactionCollectionsOrAnEmptyOwnershipTotal()
    {
        await using var fixture = new Fixture("en", "unknown");
        var html = await fixture.Render(new() { IsOwnershipKnown = true });
        Assert.Empty(Ids(html));
        Assert.DoesNotContain("0 von 0", html); Assert.DoesNotContain("0 of 0", html);
        Assert.DoesNotContain("data-ownership=\"missing\"", html);
    }

    [Theory]
    [InlineData("asmodian", "de")]
    [InlineData("elyos", "en")]
    public async Task UnknownSourceShowsFactionCatalogAndSeventeenUnknownSlotsWithoutInventingMissingOwnership(string faction, string language)
    {
        await using var fixture = new Fixture(language, faction);
        var html = await fixture.Render(PantheonSnapshot.Empty);
        Assert.Equal(98, Ids(html).Length);
        Assert.Equal(17, Regex.Matches(html, "data-pantheon-slot=\"").Count);
        Assert.Equal(17, Regex.Matches(html, "data-slot-known=\"false\"").Count);
        Assert.Equal(98, Regex.Matches(html, "data-ownership=\"unknown\"").Count);
        Assert.DoesNotContain("data-ownership=\"missing\"", html);
        Assert.Contains("data-pantheon-effects-known=\"false\"", html);
        foreach (var filter in new[] { "owned", "missing" })
            Assert.Contains("disabled", Regex.Match(html, "<button[^>]*data-pantheon-filter=\"" + filter + "\"[^>]*>").Value);
        Assert.All(Ids(html), id => Assert.True(PantheonCatalog.Default.Find(id)!.IncludesFaction(faction)));
    }

    [Fact]
    public async Task CollectionUsesCategorySideButtonsAndKeepsTheSeventeenSlotGroupsInTheRightSidebar()
    {
        await using var fixture = new Fixture("en", "asmodian");
        var html = await fixture.Render(PantheonSnapshot.Empty);
        Assert.Contains("data-pantheon-layout=\"collection-sidebar\"", html);
        Assert.True(html.IndexOf("data-pantheon-browser", StringComparison.Ordinal)
            < html.IndexOf("data-pantheon-sidebar", StringComparison.Ordinal));
        Assert.Equal(new[] { "", "Colossus", "Statue", "Artwork" },
            Regex.Matches(html, "<button[^>]*data-pantheon-category=\"([^\"]*)\"").Select(match => match.Groups[1].Value));
        foreach (var (category, count) in new[] { ("Colossus", 1), ("Statue", 4), ("Artwork", 12) })
            Assert.Contains("data-pantheon-slot-category=\"" + category + "\" data-slot-count=\"" + count + "\"", html);
        Assert.Equal(98, Regex.Matches(html, "role=\"listitem\"").Count);
        Assert.Contains("Pantheon Display Effect", html);
        Assert.DoesNotContain("src=\"\"", html);
        foreach (var item in fixture.Items.Where(item => string.IsNullOrWhiteSpace(item.IconPath)))
        {
            var card = Regex.Match(html, "<article[^>]*data-pantheon-decor=\"" + item.Id + "\"[^>]*>.*?</article>", RegexOptions.Singleline).Value;
            Assert.Contains("pt-icon-missing", card);
        }
        html = await fixture.Click("data-pantheon-category", "Artwork");
        Assert.All(Ids(html), id => Assert.Equal(PantheonDecorCategory.Artwork, PantheonCatalog.Default.Find(id)!.Category));
        Assert.Equal(79, Ids(html).Length);
    }

    [Fact]
    public async Task SavedOwnershipSlotsAndDisplayEffectsStaySeparateAndNeverRenderTheProfileBinding()
    {
        await using var fixture = new Fixture("en", "asmodian");
        var item = fixture.Items.First(item => item.Category == PantheonDecorCategory.Colossus);
        var snapshot = new PantheonSnapshot
        {
            ProfileId = "private-fixture-profile", SourceKind = PantheonSourceKind.Saved,
            ObservedAt = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero),
            IsOwnershipKnown = true, OwnedIds = ImmutableHashSet.Create(item.Id),
            Slots = [new(17, PantheonDecorCategory.Colossus, true, item.Id), new(13, PantheonDecorCategory.Statue, true, null)],
            AreEffectsKnown = true, EquippedEffects = [new("Justice", 12), new("Time", 7)]
        };
        var html = await fixture.Render(snapshot);
        Assert.Contains("data-pantheon-source=\"saved\"", html);
        Assert.DoesNotContain("private-fixture-profile", html);
        Assert.Single(Regex.Matches(html, "data-ownership=\"owned\""));
        Assert.Equal(97, Regex.Matches(html, "data-ownership=\"missing\"").Count);
        Assert.Equal(2, Regex.Matches(html, "data-slot-known=\"true\"").Count);
        foreach (var (stat, amount) in new[] { ("Justice", 12), ("Time", 7) })
        {
            var effect = Regex.Match(html, "<div[^>]*data-pantheon-effect=\"" + stat + "\"[^>]*>.*?</div>", RegexOptions.Singleline).Value;
            Assert.Contains(stat, effect); Assert.Contains("<dd>" + amount + "</dd>", effect);
        }
        Assert.Contains(item.IconPath, html); Assert.Contains(item.Name("en"), html);
        Assert.DoesNotContain("data-pantheon-equip-action", html);
    }

    [Fact]
    public async Task KnownEmptySlotDoesNotTurnTheRemainingUnknownSlotsOrOwnershipIntoZeros()
    {
        await using var fixture = new Fixture("en", "asmodian");
        var html = await fixture.Render(new()
        {
            SourceKind = PantheonSourceKind.Live,
            Slots = [new(1, PantheonDecorCategory.Artwork, true, null)]
        });
        Assert.Single(Regex.Matches(html, "data-slot-known=\"true\""));
        Assert.Equal(16, Regex.Matches(html, "data-slot-known=\"false\"").Count);
        Assert.Equal(98, Regex.Matches(html, "data-ownership=\"unknown\"").Count);
        Assert.Contains("data-pantheon-effects-known=\"false\"", html);
        Assert.DoesNotContain("data-pantheon-effect=", html);
        Assert.Contains("data-pantheon-source=\"live\"", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DebugObservationIsOptInAndNeverSerializesTheProfileBinding(bool enabled)
    {
        var services = new ServiceCollection(); services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var id = PantheonCatalog.Default.Items.First().Id;
        var snapshot = new PantheonSnapshot
        {
            ProfileId = "private-fixture-profile", SourceKind = PantheonSourceKind.Saved,
            IsOwnershipKnown = true, OwnedIds = ImmutableHashSet.Create(id)
        };
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<CombatPantheonObservationData>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CombatPantheonObservationData.Snapshot)] = snapshot,
                [nameof(CombatPantheonObservationData.Enabled)] = enabled
            }));
            return root.ToHtmlString();
        });
        Assert.DoesNotContain("private-fixture-profile", html);
        if (!enabled) { Assert.DoesNotContain("data-pantheon-observation", html); return; }
        var json = Regex.Match(html, "<script[^>]*data-pantheon-observation[^>]*>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Saved", document.RootElement.GetProperty("sourceKind").GetString());
        Assert.Equal(id, document.RootElement.GetProperty("ownedIds")[0].GetInt32());
        Assert.False(document.RootElement.TryGetProperty("profileId", out _));
    }

    [Fact]
    public async Task ProductionOwnershipCategorySearchAndGodSortCallbacksFilterOnlyTheCatalogView()
    {
        await using var fixture = new Fixture("en", "asmodian");
        var owned = fixture.Items.Take(3).Select(item => item.Id).ToImmutableHashSet();
        await fixture.Render(new() { IsOwnershipKnown = true, SourceKind = PantheonSourceKind.Live, OwnedIds = owned });
        var html = await fixture.Click("data-pantheon-filter", "owned");
        Assert.Equal(owned.Order(), Ids(html).Order());
        html = await fixture.Click("data-pantheon-filter", "missing");
        Assert.Equal(95, Ids(html).Length); Assert.DoesNotContain(Ids(html), id => owned.Contains(id));
        await fixture.Click("data-pantheon-filter", "all");
        html = await fixture.Click("data-pantheon-category", "Statue");
        Assert.Equal(16, Ids(html).Length);
        Assert.All(Ids(html), id => Assert.Equal(PantheonDecorCategory.Statue, PantheonCatalog.Default.Find(id)!.Category));
        await fixture.Click("data-pantheon-category", "");
        var target = fixture.Items.First();
        html = await fixture.Change("data-pantheon-search", target.Name("en"), "oninput");
        Assert.Contains(target.Id, Ids(html));
        Assert.All(Ids(html), id => Assert.Contains(target.Name("en"), PantheonCatalog.Default.Find(id)!.Name("en")));
        await fixture.Change("data-pantheon-search", "", "oninput");
        html = await fixture.Change("data-pantheon-sort", "Time");
        Assert.Equal(fixture.Items.OrderByDescending(item => item.EffectAmount("Time")).ThenByDescending(item => item.Grade)
            .ThenBy(item => item.Name("en"), StringComparer.CurrentCulture).ThenBy(item => item.Id).Select(item => item.Id), Ids(html));
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousSavedPantheon(string language)
    {
        var repository = TestPrerequisites.RepositoryRoot;
        var input = Path.GetFullPath(TestPrerequisites.RequiredFile("SOULCREST_PANTHEON_RENDER_INPUT_REQUIRED"));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output")) + Path.DirectorySeparatorChar,
            input, StringComparison.OrdinalIgnoreCase);
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_PANTHEON_UI_EXPORT"));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(File.ReadAllText(input));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.True(root.GetProperty("private").GetBoolean());
        Assert.False(root.GetProperty("isFresh").GetBoolean()); Assert.False(root.GetProperty("isLive").GetBoolean());
        var faction = root.GetProperty("ownFaction").GetString();
        Assert.True(faction is "asmodian" or "elyos");
        var decorations = root.GetProperty("rows").EnumerateArray()
            .ToImmutableDictionary(row => row.GetProperty("id").GetInt32(), row => row.GetProperty("slot").GetByte());
        Assert.All(decorations.Keys, id => Assert.True(id > 0));
        var at = root.GetProperty("observedAt").GetDateTimeOffset();
        var snapshot = PantheonStateService.Project("anonymous-render-profile", decorations, at, PantheonSourceKind.Saved);
        Assert.Equal(PantheonSourceKind.Saved, snapshot.SourceKind);
        await using var fixture = new Fixture(language, faction!);
        await fixture.Render(snapshot);
        var html = await fixture.Markup();
        Assert.Contains("data-pantheon-source=\"saved\"", html);
        Assert.DoesNotContain("data-pantheon-source=\"live\"", html);
        Assert.DoesNotContain("anonymous-render-profile", html);
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css", "pantheon.css" }
            .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        var baseUri = new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pantheon-saved-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<base href=\"" + WebUtility.HtmlEncode(baseUri) + "\"><style>" + css
            + "\nhtml,body{height:auto}.pt-view{max-width:1400px;margin:auto}</style></head><body>" + html + "</body></html>");
    }

    private static int[] Ids(string html) => Regex.Matches(html, "data-pantheon-decor=\"([0-9]+)\"")
        .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previousLanguage = UiText.Language;
        private readonly string _faction;
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedView? _view;
        internal IReadOnlyList<PantheonDecor> Items => PantheonCatalog.Default.Items.Where(item => item.IncludesFaction(_faction)).ToArray();
        internal Fixture(string language, string faction)
        {
            _faction = faction; UiText.Language = language;
            var services = new ServiceCollection(); services.AddLogging();
            _services = services.BuildServiceProvider(); _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        internal Task<string> Render(PantheonSnapshot snapshot) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await _renderer.RenderComponentAsync<CapturedView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CombatPantheonView.Snapshot)] = snapshot, [nameof(CombatPantheonView.Faction)] = _faction,
                [nameof(CapturedView.Captured)] = (Action<CapturedView>)(view => _view = view)
            }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        internal Task<string> Click(string attribute, string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Invoke(attribute, value, "onclick", new MouseEventArgs()); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        internal Task<string> Change(string attribute, string value, string callback = "onchange") => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Invoke(attribute, null, callback, new ChangeEventArgs { Value = value }); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        internal Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); UiText.Language = _previousLanguage; }
    }

    public sealed class CapturedView : CombatPantheonView
    {
        [Parameter] public Action<CapturedView>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
#pragma warning disable BL0006
        internal async Task Invoke(string attribute, string? value, string callback, object args)
        {
            using var tree = new RenderTreeBuilder(); base.BuildRenderTree(tree); var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == attribute && (value is null || Equals(frames.Array[i].AttributeValue?.ToString(), value)));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            object? handler = null;
            for (var i = index + 1; i < frames.Count && frames.Array[i].FrameType == RenderTreeFrameType.Attribute; i++)
                if (frames.Array[i].AttributeName == callback) handler = frames.Array[i].AttributeValue;
            switch (handler)
            {
                case EventCallback<MouseEventArgs> mouse: await mouse.InvokeAsync((MouseEventArgs)args); break;
                case EventCallback<ChangeEventArgs> change: await change.InvokeAsync((ChangeEventArgs)args); break;
                case EventCallback untyped: await untyped.InvokeAsync(args); break;
                case Action action: action(); break;
                case Func<Task> function: await function(); break;
                default: throw new InvalidOperationException("Pantheon callback missing.");
            }
            StateHasChanged();
        }
#pragma warning restore BL0006
    }
}
