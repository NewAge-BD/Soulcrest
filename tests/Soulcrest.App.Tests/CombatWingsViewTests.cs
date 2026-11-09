using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.Json;
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
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatWingsViewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BaseOwnedBonusesStackOnceWithoutEquippedValuesAndKeepUnitsIndependent()
    {
        var catalog = Catalog();
        var result = CombatWingsPresentation.OwnedPassives(catalog, [1, 1, 2], true, "en");
        Assert.Equal(CombatKnowledgeStatus.Complete, result.Status);
        Assert.Equal(15m, result.Rows.Single(row => row.Key == new CombatTitlePassiveKey("Attack", "Points")).Value);
        Assert.Equal(2m, result.Rows.Single(row => row.Key == new CombatTitlePassiveKey("AttackPercent", "Percent")).Value);
        Assert.Equal(2, result.Rows.Single(row => row.Key.Unit == "Points").WingCount);
        var unknown = CombatWingsPresentation.OwnedPassives(catalog, [1, 999], true, "en");
        Assert.Equal(CombatKnowledgeStatus.Partial, unknown.Status);
        Assert.All(unknown.Rows, row => Assert.Null(row.Value));
    }

    [Fact]
    public void MissingBonusesUseOnlyUnownedBaseWingsOfKnownFaction()
    {
        var result = CombatWingsPresentation.MissingPassives(Catalog(), [1], true, Factions.Elyos, "en");
        Assert.Equal(CombatKnowledgeStatus.Complete, result.Status);
        var attack = Assert.Single(result.Rows);
        Assert.Equal(5m, attack.Value); Assert.Equal(1, attack.WingCount);
        foreach (var resultUnknown in new[]
        {
            CombatWingsPresentation.MissingPassives(Catalog(), [1], false, Factions.Elyos, "en"),
            CombatWingsPresentation.MissingPassives(Catalog(), [1], true, null, "en")
        })
        {
            Assert.Equal(CombatKnowledgeStatus.Partial, resultUnknown.Status);
            Assert.All(resultUnknown.Rows, row => Assert.Null(row.Value));
        }
        var empty = CombatWingsPresentation.OwnedPassives(Catalog(), [], true, "en");
        Assert.Equal(CombatKnowledgeStatus.Complete, empty.Status); Assert.Empty(empty.Rows);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task MissingStatUsesProductionCallbackAndLeavesActiveWingVisible(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(Snapshot([1]));
        Assert.Equal(new[] { 1, 2, 4 }, WingIds(html));
        Assert.Contains("icons/combat/wings/example.png", html);
        Assert.DoesNotContain("offensive-tab", html);
        Assert.Contains("data-wing-owned-count=\"1\"", html);
        Assert.Contains("cw-wing-owned", WingCard(html, 1));
        Assert.DoesNotContain("cw-wing-owned", WingCard(html, 2));
        Assert.Contains("data-wing-owned-stat-value=\"10\"", html);
        Assert.Contains("data-wing-missing-stat-value=\"5\"", html);
        Assert.Contains("data-equipped-wing=\"1\"", html);
        Assert.DoesNotContain("255", html); // Stored enchantment never appears in the base UI.
        html = await fixture.Click("data-wing-passive-filter", "Attack|Points");
        Assert.Equal(new[] { 2 }, WingIds(html));
        Assert.Contains("data-equipped-wing=\"1\"", html);
        Assert.Contains("data-wing-passive-selection=\"Attack|Points\"", html);
        Assert.Contains("ct-effect-selected", html);
        html = await fixture.Click("data-wing-clear-passive-filter", "clear");
        Assert.Equal(new[] { 1, 2, 4 }, WingIds(html));
        html = await fixture.Click("data-wing-ownership-filter", "Owned");
        Assert.Equal(new[] { 1 }, WingIds(html));
        Assert.Contains("data-equipped-wing=\"1\"", html);
    }

    [Fact]
    public async Task HistoricalOwnershipNeverRestoresCurrentEquipmentOrLeaksAcrossProfiles()
    {
        await using var fixture = new Fixture();
        var saved = new ProfileWingSnapshot("fixture-one", Wings([1]), At.AddDays(-1), "own-wing-ownership-list");
        var snapshot = Snapshot([1]) with { IsFresh = false };
        var html = await fixture.Render(snapshot, saved);
        Assert.Contains("data-wing-ownership-source=\"Saved\"", html);
        Assert.Contains("data-saved-wing-count=\"1\"", html);
        Assert.Contains("cw-wing-owned", WingCard(html, 1));
        Assert.DoesNotContain("cw-wing-owned", WingCard(html, 2));
        Assert.DoesNotContain("data-wing-owned-count", html);
        Assert.DoesNotContain("data-equipped-wing", html);
        Assert.DoesNotContain("fixture-one", html);
        await fixture.Click("data-wing-passive-filter", "Attack|Points");
        await fixture.Update(CharacterCombatSnapshot.Empty with { ProfileId = "fixture-two" }, saved);
        html = await fixture.Html();
        Assert.DoesNotContain("data-wing-passive-selection", html);
        Assert.Contains("data-wing-ownership-source=\"Unknown\"", html);
        Assert.DoesNotContain("data-saved-wing-count", html);
        Assert.DoesNotContain("data-wing-owned=\"NotOwned\"", html);
        Assert.DoesNotContain("data-wing-owned=\"SavedOwned\"", html);
        Assert.DoesNotContain("data-wing-missing-stat-value=", html);
    }

    [Fact]
    public async Task EquipmentAndOwnershipStayIndependentAndUnknownDoesNotBecomeZero()
    {
        await using var fixture = new Fixture();
        var snapshot = Snapshot([1]);
        snapshot = snapshot with { WingOwnership = NetworkCombatDomain<CharacterWingOwnership>.Unknown };
        var html = await fixture.Render(snapshot);
        Assert.Contains("data-equipped-wing=\"1\"", html);
        Assert.DoesNotContain("data-wing-owned-count", html);
        Assert.Contains("data-wing-owned=\"Unknown\"", html);
        Assert.DoesNotContain("cw-wing-owned", WingCard(html, 1));
        Assert.DoesNotContain("data-wing-missing-stat-value=", html);
        await fixture.Update(Snapshot([]) with { EquippedWing = new(new(0, null, null), NetworkCombatSourceStatus.Complete, true, At, 1, "fixture") });
        html = await fixture.Html();
        Assert.Contains("data-wing-owned-count=\"0\"", html);
        Assert.Contains("data-equipped-wing=\"0\"", html);
        await fixture.Update(Snapshot([1]) with { EquippedWing = new(new(null, null, 2), NetworkCombatSourceStatus.Partial, true, At, 1, "fixture-skin-only") });
        html = await fixture.Html();
        Assert.DoesNotContain("data-equipped-wing", html);
        Assert.Contains("data-wing-equipped-awaiting-current", html);
        Assert.Contains("data-wing-owned-count=\"1\"", html);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionWings(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_COMBAT_WINGS_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var catalog = CombatWingDisplayCatalog.Default;
        var active = catalog.Entries.Values.First(wing => wing.NameEn.Contains("Talisra", StringComparison.OrdinalIgnoreCase));
        var owned = catalog.Entries.Values.Where(wing => wing.Race == active.Race && wing.Grade is "Common" or "Rare")
            .Take(5).Select(wing => wing.Id).Append(active.Id).Distinct().ToArray();
        var snapshot = Snapshot(owned) with { EquippedWing = new(new(active.Id, null, null), NetworkCombatSourceStatus.Complete, true, At, 1, "fixture") };
        await using var fixture = new Fixture(language);
        await fixture.Render(snapshot, catalog: catalog, faction: active.Race == "Light" ? Factions.Elyos : Factions.Asmodian);
        await fixture.Click("data-wing-ownership-filter", "Owned");
        var markup = await fixture.Markup();
        Assert.DoesNotContain("fixture-one", markup);
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css" }.Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "wings-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\""
            + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri) + "\"><style>" + css
            + "\nhtml,body{height:auto;overflow:visible}body{padding:24px;background:#0e141e}.cw-wings{max-width:1650px;margin:auto}</style></head><body>" + markup + "</body></html>");
    }

    private static int[] WingIds(string html) => Regex.Matches(html, "data-wing-id=\"(\\d+)\"").Select(match => int.Parse(match.Groups[1].Value)).Order().ToArray();
    private static string WingCard(string html, int id) => Regex.Match(html, "<article\\b[^>]*data-wing-id=\"" + id + "\"[^>]*>.*?</article>", RegexOptions.Singleline).Value;
    private static ImmutableDictionary<int, byte> Wings(IEnumerable<int> ids) => ids.ToImmutableDictionary(id => id, _ => (byte)255);
    private static CharacterCombatSnapshot Snapshot(int[] ids) => new()
    {
        ProfileId = "fixture-one", IsFresh = true, Generation = 7,
        WingOwnership = new(new(Wings(ids)), NetworkCombatSourceStatus.Complete, true, At, 1, "fixture"),
        EquippedWing = new(new(1, 255, 2), NetworkCombatSourceStatus.Complete, true, At, 1, "fixture")
    };
    private static CombatWingDisplayCatalog Catalog()
    {
        static CombatTitleDisplayEffect Effect(string stat, decimal value, string unit = "Points") => new(stat, stat, stat, value, unit);
        static CombatWingDisplayEntry Wing(int id, string race, params CombatTitleDisplayEffect[] owned) =>
            new(id, "Flügel " + id, "Wing " + id, race, "Common", "icons/combat/wings/example.png")
            { EquippedEffects = [Effect("Attack", 900)], OwnedEffects = owned };
        return CombatWingDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = 26425393, presentationOnly = true, doesNotEstablishOwnershipOrEffectApplicability = true,
            wings = new[] { Wing(1, "Light", Effect("Attack", 10), Effect("AttackPercent", 2, "Percent")), Wing(2, "Light", Effect("Attack", 5)),
                Wing(3, "Dark", Effect("Attack", 100)), Wing(4, "Light") }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previousLanguage = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedWings? _view;
        public Fixture(string language = "en")
        {
            UiText.Language = language;
            var services = new ServiceCollection(); services.AddLogging(); _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public Task<string> Render(CharacterCombatSnapshot snapshot, ProfileWingSnapshot? saved = null, CombatWingDisplayCatalog? catalog = null, string? faction = Factions.Elyos) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await _renderer.RenderComponentAsync<CapturedWings>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedWings.Snapshot)] = snapshot, [nameof(CapturedWings.SavedWings)] = saved,
                [nameof(CapturedWings.Catalog)] = catalog ?? Catalog(), [nameof(CapturedWings.Faction)] = faction,
                [nameof(CapturedWings.Captured)] = (Action<CapturedWings>)(view => _view = view)
            }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public Task<string> Click(string attribute, string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Click(attribute, value); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task Update(CharacterCombatSnapshot snapshot, ProfileWingSnapshot? saved = null) => _renderer.Dispatcher.InvokeAsync(() => _view!.Update(snapshot, saved));
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _previousLanguage; }
    }

    public sealed class CapturedWings : CombatWingsView
    {
        [Parameter] public Action<CapturedWings>? Captured { get; set; }
        protected override void OnInitialized() => Captured?.Invoke(this);
        internal void Update(CharacterCombatSnapshot snapshot, ProfileWingSnapshot? saved) { Snapshot = snapshot; SavedWings = saved; OnParametersSet(); StateHasChanged(); }
#pragma warning disable BL0006 // Exercise the production callback used by pointer and keyboard activation.
        public async Task Click(string attribute, string value)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatWingsView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == attribute && Equals(frames.Array[i].AttributeValue, value));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeValue;
            if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
            else if (callback is EventCallback untyped) await untyped.InvokeAsync();
            else if (callback is Action synchronous) synchronous();
            else if (callback is Func<Task> asynchronous) await asynchronous();
            else throw new InvalidOperationException("Unexpected Wing callback.");
            StateHasChanged();
        }
#pragma warning restore BL0006
    }
}
