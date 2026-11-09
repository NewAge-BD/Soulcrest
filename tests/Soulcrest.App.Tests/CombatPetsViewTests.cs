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
using Soulcrest.Core.Combat;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatPetsViewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("de", "12,5 %", "15 / 120 Pet-Stufen")]
    [InlineData("en", "12.5 %", "15 / 120 pet stages")]
    public async Task SummaryUsesStageCompletionInFixedGenusOrderAndSeparatesOwnedFromMissing(
        string language, string percent, string stages)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(Complete());
        Assert.Equal(new[] { "cogni", "fera", "natura", "varian" }, Categories(html));
        Assert.Contains(percent, Category(html, "cogni")); Assert.Contains(stages, Category(html, "cogni"));
        Assert.Contains("data-pet-completed-stages=\"15\"", Category(html, "cogni"));
        Assert.Contains("data-pet-maximum-stages=\"120\"", Category(html, "cogni"));
        Assert.Contains("aria-valuenow=\"15\"", Category(html, "cogni"));
        Assert.Contains("aria-valuemax=\"120\"", Category(html, "cogni"));
        Assert.Contains("aria-valuenow=\"0\"", Category(html, "fera"));
        Assert.Contains("data-pet-completion-percent=\"100\"", Category(html, "natura"));
        Assert.Contains("data-pet-effect-channel=\"Owned\"", html);
        Assert.Contains("data-pet-effect-channel=\"Missing\"", html);
        Assert.Contains("data-pet-effect-value=\"20\"", html);
        Assert.Contains("data-pet-effect-value=\"10\"", html);
        Assert.Contains("data-pet-effect-value=\"0\"", html);
        Assert.Equal(4, Regex.Matches(html, "<img ").Count);
        Assert.All(new[] { "intellect", "feral", "nature", "trans" }, icon => Assert.Contains("creatureslot_" + icon + "_sprite.png", html));
        Assert.DoesNotContain("data-source-pet-level", html); Assert.DoesNotContain("data-source-model-section", html);
        Assert.DoesNotContain("data-title-passive-filter", html); Assert.DoesNotContain("<a ", html);
    }

    [Fact]
    public async Task UnknownStagesAndBonusesRemainUnknownInsteadOfZero()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(CombatPetsSnapshot.Empty);
        Assert.Equal(4, Categories(html).Length);
        Assert.DoesNotContain("aria-valuenow=", html); Assert.DoesNotContain("aria-valuemax=", html);
        Assert.DoesNotContain("data-pet-completed-stages=", html); Assert.DoesNotContain("data-pet-completion-percent=", html);
        Assert.DoesNotContain("data-pet-effect-value=", html);
        Assert.DoesNotContain("0 /", html); Assert.DoesNotContain("0 %", html);
        Assert.Contains("data-pet-summary-status=\"Unknown\"", html);
        Assert.Contains("data-pet-summary-source=\"Unknown\"", html);
        Assert.Contains("disabled", Regex.Match(html, "<button[^>]*data-open-main-pets[^>]*>").Value);
    }

    [Fact]
    public async Task HistoricalCollectionHasOneSharedNoteAndMarksManualStages()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(Complete() with { IsHistorical = true, IncludesManualValues = true, ObservedAt = At });
        Assert.Single(Regex.Matches(html, "data-pet-saved-stand").Cast<Match>());
        Assert.Single(Regex.Matches(html, "data-pet-manual-values").Cast<Match>());
        Assert.Contains("data-pet-summary-source=\"Saved\"", html);
        Assert.Contains("datetime=\"2026-11-09T15:00:00.0000000+00:00\"", html);
        Assert.DoesNotContain("Aktueller Charakter", html); Assert.DoesNotContain("Current character", html);
        Assert.DoesNotContain("data-pet-category=\"special\"", html);
        await fixture.Update(Complete() with { IncludesManualValues = true });
        html = await fixture.Html();
        Assert.Contains("data-pet-summary-source=\"IncludesManual\"", html);
        Assert.DoesNotContain("data-pet-saved-stand", html);
    }

    [Fact]
    public async Task NativeOpenActionInvokesNavigationDelegateAndStateRefreshRemovesOldValues()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(Complete(), canNavigate: true);
        Assert.DoesNotContain("disabled", Regex.Match(html, "<button[^>]*data-open-main-pets[^>]*>").Value);
        await fixture.Open();
        Assert.Equal(1, fixture.OpenCount);
        await fixture.Update(CombatPetsSnapshot.Empty);
        html = await fixture.Html();
        Assert.DoesNotContain("data-pet-effect-value=\"20\"", html);
        Assert.DoesNotContain("aria-valuenow=", html);
        Assert.DoesNotContain("data-pet-saved-stand", html);
        Assert.Equal(4, Categories(html).Length);
    }

    [Fact]
    public async Task PartialEffectShowsItsQualifiedSubtotalWithoutClaimingAnExactBonus()
    {
        await using var fixture = new Fixture();
        var snapshot = Complete() with
        {
            Status = CombatKnowledgeStatus.Partial,
            OwnedEffects = [new(new("Attack", "Points"), "Angriff", "Attack", null, 17, CombatKnowledgeStatus.Partial)]
        };
        var html = await fixture.Render(snapshot);
        Assert.Contains("data-pet-summary-source=\"Partial\"", html);
        var row = Regex.Match(html, "<div[^>]*data-pet-effect-channel=\"Owned\"[^>]*>.*?</div>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("data-pet-effect-value=", row);
        Assert.Contains("data-pet-effect-known-subtotal=\"17\"", row);
        Assert.Contains("Known subtotal: +17", row);
        Assert.Contains(">—</strong>", row);
    }

    [Fact]
    public async Task MissingStatsOpenTheirNativeCategoryEvenWhileTheMissingValueIsUnknown()
    {
        await using var fixture = new Fixture();
        var snapshot = Complete() with
        {
            MissingEffects = [new(new("STR", "Points"), "Macht", "Might", null, null, CombatKnowledgeStatus.Unknown),
                new(new("DEX", "Points"), "Agilität", "Dexterity", 2, 2, CombatKnowledgeStatus.Complete),
                new(new("AGI", "Points"), "Präzision", "Precision", 3, 3, CombatKnowledgeStatus.Complete),
                new(new("CON", "Points"), "Vitalität", "Constitution", 4, 4, CombatKnowledgeStatus.Complete),
                new(new("Unmapped", "Points"), "Unbekannt", "Unknown", 5, 5, CombatKnowledgeStatus.Complete)]
        };
        var html = await fixture.Render(snapshot, canNavigate: true);
        foreach (var stat in new[] { "STR", "DEX", "AGI", "CON" })
        {
            var button = Regex.Match(html, "<button[^>]*data-pet-missing-stat=\"" + stat + "\\|Points\"[^>]*>").Value;
            Assert.Contains("type=\"button\"", button); Assert.DoesNotContain("disabled", button);
            await fixture.OpenCategory(stat + "|Points");
        }
        Assert.Equal(new[] { "cogni", "fera", "natura", "varian" }, fixture.OpenedCategories);
        Assert.Contains("disabled", Regex.Match(html, "<button[^>]*data-pet-missing-stat=\"Unmapped\\|Points\"[^>]*>").Value);
        Assert.Empty(Regex.Matches(html, "<button[^>]*data-pet-effect-channel=\"Owned\""));
        Assert.Equal(0, fixture.OpenCount);
        html = await fixture.Render(snapshot);
        Assert.Contains("disabled", Regex.Match(html, "<button[^>]*data-pet-missing-stat=\"STR\\|Points\"[^>]*>").Value);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionPets(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_COMBAT_PETS_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var mapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA") ?? Path.Combine(repository, "imports", "generated", "mapdata");
        var catalog = PetCatalog.Load(Path.Combine(mapData, "pets.json"));
        var sample = PetSoulIdentity.Verified(catalog)
            .Where(pet => CombatPetsPresentation.Genera.Contains(pet.Genus, StringComparer.Ordinal))
            .Select((pet, index) => new PetCollectionEntry(pet.Id, pet.Genus, index % 4, true, true, false, At)).ToArray();
        Assert.NotEmpty(sample);
        await using var fixture = new Fixture(language);
        await fixture.Render(CombatPetsPresentation.Create(new(Array.AsReadOnly(sample))), canNavigate: true);
        var markup = await fixture.Markup();
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css" }.Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pets-" + language + ".html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\""
            + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri) + "\"><style>" + css
            + "\nhtml,body{height:auto;overflow:visible}body{padding:24px;background:#0e141e}.cpet-view{max-width:1450px;margin:0 auto}</style></head><body>" + markup + "</body></html>");
    }

    private static string[] Categories(string html) => Regex.Matches(html, "data-pet-category=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();
    private static string Category(string html, string genus) => Regex.Match(html, "<article[^>]*data-pet-category=\"" + genus + "\"[^>]*>.*?</article>", RegexOptions.Singleline).Value;
    private static CombatPetsSnapshot Complete() => new()
    {
        Status = CombatKnowledgeStatus.Complete, ObservedAt = At,
        OwnedEffects = [new(new("Attack", "Points"), "Angriff", "Attack", 20, 20, CombatKnowledgeStatus.Complete),
            new(new("Defense", "Points"), "Verteidigung", "Defense", 50, 50, CombatKnowledgeStatus.Complete),
            new(new("HPMax", "Points"), "LP", "HP", 150, 150, CombatKnowledgeStatus.Complete),
            new(new("CooldownReduction", "Percent"), "Abklingzeitverringerung", "Cooldown Reduction", 1.5m, 1.5m, CombatKnowledgeStatus.Complete)],
        MissingEffects = [new(new("Attack", "Points"), "Angriff", "Attack", 10, 10, CombatKnowledgeStatus.Complete),
            new(new("Defense", "Points"), "Verteidigung", "Defense", 0, 0, CombatKnowledgeStatus.Complete),
            new(new("HPMax", "Points"), "LP", "HP", 250, 250, CombatKnowledgeStatus.Complete),
            new(new("CooldownReduction", "Percent"), "Abklingzeitverringerung", "Cooldown Reduction", 2.5m, 2.5m, CombatKnowledgeStatus.Complete)],
        Categories = [new("varian", 5, 6, 15, 40, CombatKnowledgeStatus.Complete),
            new("cogni", 40, 15, 120, 12.5m, CombatKnowledgeStatus.Complete),
            new("natura", 10, 30, 30, 100, CombatKnowledgeStatus.Complete),
            new("fera", 20, 0, 60, 0, CombatKnowledgeStatus.Complete)]
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previousLanguage = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedPets? _view;
        public int OpenCount { get; private set; }
        public List<string> OpenedCategories { get; } = [];
        public Fixture(string language = "en")
        {
            UiText.Language = language;
            var services = new ServiceCollection(); services.AddLogging(); _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public Task<string> Render(CombatPetsSnapshot state, bool canNavigate = false) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await _renderer.RenderComponentAsync<CapturedPets>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedPets.State)] = state,
                [nameof(CapturedPets.OpenPets)] = canNavigate ? EventCallback.Factory.Create(this, () => OpenCount++) : default(EventCallback),
                [nameof(CapturedPets.OpenPetCategory)] = canNavigate ? EventCallback.Factory.Create<string>(this, (Action<string>)OpenedCategories.Add) : default(EventCallback<string>),
                [nameof(CapturedPets.Captured)] = (Action<CapturedPets>)(view => _view = view)
            }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public Task Update(CombatPetsSnapshot state) => _renderer.Dispatcher.InvokeAsync(() => _view!.Update(state));
        public Task Open() => _renderer.Dispatcher.InvokeAsync(() => _view!.Open());
        public Task OpenCategory(string key) => _renderer.Dispatcher.InvokeAsync(() => _view!.Click("data-pet-missing-stat", key));
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); UiText.Language = _previousLanguage; }
    }

    public sealed class CapturedPets : CombatPetsView
    {
        [Parameter] public Action<CapturedPets>? Captured { get; set; }
        protected override void OnInitialized() => Captured?.Invoke(this);
        internal void Update(CombatPetsSnapshot state) { State = state; StateHasChanged(); }
#pragma warning disable BL0006 // Native keyboard and pointer activation use this production callback.
        internal Task Open() => Click("data-open-main-pets", "open");
        internal async Task Click(string attribute, string value)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatPetsView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
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
            else throw new InvalidOperationException("Unexpected Pet navigation callback.");
        }
#pragma warning restore BL0006
    }
}
