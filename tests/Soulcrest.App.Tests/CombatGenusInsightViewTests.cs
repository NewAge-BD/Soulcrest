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
using Soulcrest.Core.Combat;
using Soulcrest.Core.GenusInsight;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatGenusInsightViewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("de", "Alle angewendeten Effekte", "LP", "Schadensverstärkung", "1,5 %")]
    [InlineData("en", "All Applied Effects", "HP", "Damage Boost", "1.5 %")]
    public async Task AppliedSummaryUsesLocalizedLabelsAndUnitsWithoutExposingRuntimeIdentity(
        string language, string title, string hpLabel, string percentLabel, string percentValue)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(Complete());
        Assert.Contains(title, html);
        var hp = AppliedRow(html, "HPMax");
        Assert.Contains(hpLabel, hp); Assert.Contains(">30</strong>", hp);
        var percent = AppliedRow(html, "AmplifyAllDamage");
        Assert.Contains(percentLabel, percent); Assert.Contains(percentValue, percent);
        Assert.Contains("data-genus-applied-status=\"Complete\"", html);
        Assert.DoesNotContain("data-genus-partial", html);
        Assert.DoesNotContain("PRIVATE_PROFILE", html);
        Assert.DoesNotContain("PRIVATE_SERVER", html);
    }

    [Fact]
    public async Task NativeCategoryAndSlotCallbacksSelectEveryPoolAndResetSlotOnCategoryChange()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(GenusInsightSnapshot.Empty);
        Assert.Equal(GenusInsightPresentation.Genera, Regex.Matches(html, "data-genus-category=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value));
        foreach (var genus in GenusInsightPresentation.Genera)
        {
            html = await fixture.ChooseGenus(genus);
            Assert.Contains("data-genus-options=\"" + genus + ":1\"", html);
            Assert.Equal(Enumerable.Range(1, 9), Regex.Matches(html, "data-genus-slot=\"([0-9]+)\"")
                .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
            Assert.Contains("type=\"button\"", Button(html, "data-genus-category", genus));
            Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-category", genus));
            Assert.Contains("icons/combat/genus-insight/" + genus + ".png", html);
            html = await fixture.ChooseSlot(8);
            Assert.Contains("data-genus-options=\"" + genus + ":8\"", html);
            Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-slot", "8"));
        }
        Assert.Contains("cgi-slot-grid", html); Assert.Contains("cgi-option-scroll", html);
        Assert.Empty(fixture.Toggled);
    }

    [Theory]
    [InlineData("cogni")]
    [InlineData("fera")]
    [InlineData("natura")]
    [InlineData("varian")]
    [InlineData("special")]
    public async Task EverySlotShowsOnlyItsEligibleStatsAndItsExactFiveRarityRanges(string genus)
    {
        await using var fixture = new Fixture();
        await fixture.Render(GenusInsightSnapshot.Empty);
        await fixture.ChooseGenus(genus);
        var catalog = GenusInsightCatalog.Default;
        for (var slot = 1; slot <= 9; slot++)
        {
            var html = await fixture.ChooseSlot(slot);
            var expected = catalog.Options(genus, slot).Select(option => option.StatId).Distinct().Order(StringComparer.Ordinal);
            var actual = Regex.Matches(html, "data-genus-option-stat=\"([^\"]+)\"").Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal);
            Assert.Equal(expected, actual);
            foreach (var stat in catalog.Options(genus, slot).Select(option => option.StatId).Distinct())
            {
                var row = OptionRow(html, stat);
                var cells = Regex.Matches(row, "<td>(.*?)</td>", RegexOptions.Singleline).Select(match => match.Groups[1].Value).ToArray();
                Assert.Equal(6, cells.Length);
                for (byte rank = 1; rank <= 5; rank++)
                {
                    var range = catalog.Range(genus, slot, stat, rank);
                    Assert.Equal(RangeText(range, catalog.FindStat(stat)!.Unit), cells[rank - 1]);
                }
            }
        }
    }

    [Fact]
    public async Task FavoriteCallbackUsesTheSelectedGenusAndSlotWithoutLeakingIntoAnotherSlot()
    {
        var first = new GenusInsightFavoriteKey("special", 1, "FixingDamage");
        await using var fixture = new Fixture();
        await fixture.Render(GenusInsightSnapshot.Empty, ImmutableHashSet.Create(first), canFavorite: true);
        var html = await fixture.ChooseGenus("special");
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-favorite", "FixingDamage"));
        html = await fixture.ChooseSlot(5);
        Assert.Contains("aria-pressed=\"false\"", Button(html, "data-genus-favorite", "FixingDamage"));
        await fixture.Favorite("FixingDamage");
        Assert.Equal(new GenusInsightFavoriteKey("special", 5, "FixingDamage"), Assert.Single(fixture.Toggled));
        html = await fixture.Html();
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-favorite", "FixingDamage"));
        html = await fixture.ChooseSlot(1);
        Assert.Contains("aria-pressed=\"true\"", Button(html, "data-genus-favorite", "FixingDamage"));
        await fixture.Favorite("FixingDamage");
        Assert.Equal(first, fixture.Toggled[1]);
    }

    [Fact]
    public async Task RankColorsAndMaxIndicatorUseExplicitRollFactsRatherThanValueEquality()
    {
        var colors = new[] { "#f4f4f1", "#39db63", "#2fa9ff", "#f4cf35", "#ff8b22" };
        var slots = Enumerable.Range(1, 5).Select(index => new GenusInsightSlotView
        {
            Slot = (byte)index, StatId = "FixingDamage", LabelDe = "Zusatzangriffskraft", LabelEn = "Attack Bonus",
            Value = 16, Minimum = 2, Maximum = 16, Unit = "Points", Rank = (byte)index,
            RollFraction = 1, IsMaxRoll = index == 3, IsCurrent = true, IsActivated = true
        }).Append(new GenusInsightSlotView { Slot = 6, Value = 16, Minimum = 2, Maximum = 16 }).ToArray();
        await using var fixture = new Fixture();
        var html = await fixture.Render(Complete() with { Boards = [new("cogni", 10, slots)] });
        for (var index = 1; index <= 5; index++)
            Assert.Contains("color:" + colors[index - 1], SlotRow(html, index));
        Assert.Single(Regex.Matches(html, "MAX ROLL!").Cast<Match>());
        Assert.Contains("MAX ROLL!", SlotRow(html, 3));
        Assert.DoesNotContain("MAX ROLL!", SlotRow(html, 2));
        Assert.DoesNotContain("MAX ROLL!", SlotRow(html, 6));
        Assert.DoesNotContain("class=\"cgi-range\"", SlotRow(html, 6));
        await fixture.Update(GenusInsightSnapshot.Empty);
        html = await fixture.Html();
        Assert.DoesNotContain("MAX ROLL!", html);
        Assert.DoesNotContain("data-genus-rank=", html);
        Assert.DoesNotContain("data-genus-applied-stat=", html);
        Assert.Equal(9, Regex.Matches(html, Regex.Escape(UiText.T("Roll noch nicht bestätigt"))).Count);
        Assert.Contains("data-genus-current=\"false\"", html);
    }

    [Fact]
    public async Task UnknownAndPartialEffectsStayUnknownAndFavoriteFailuresRemainVisible()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(Complete() with
        {
            IsCurrent = false, AppliedEffectsStatus = CombatKnowledgeStatus.Partial,
            AppliedEffects = [new("UnknownStat", "Unbekannt", "Unknown", "Points", null)]
        }, error: "Synthetic favorite save failed");
        Assert.Contains("data-genus-partial", html);
        Assert.Contains(">—</strong>", AppliedRow(html, "UnknownStat"));
        Assert.Contains("Synthetic favorite save failed", html); Assert.Contains("role=\"alert\"", html);
        Assert.All(Regex.Matches(html, "<button[^>]*data-genus-favorite=[^>]*>").Cast<Match>(),
            match => Assert.Contains("disabled", match.Value));
        Assert.DoesNotContain("MAX ROLL!", html);
    }

    [Theory]
    [InlineData("de", "Gespeicherter Genus-Stand", "Charakter-Preset 1", "Die Genus-Werte werden gemeinsam gespeichert.")]
    [InlineData("en", "Saved Genus state", "Character preset 1", "Genus stats are saved for the shared collection.")]
    public async Task HistoricalValuesRetainEffectsRangesAndMaximumAfterTheGameMenuCloses(
        string language, string savedLabel, string presetLabel, string sharedLabel)
    {
        await using var fixture = new Fixture(language);
        var favorites = ImmutableHashSet.Create(new GenusInsightFavoriteKey("special", 1, "FixingDamage"));
        await fixture.Render(Historical(AnonymousSample(favorites)), favorites, canFavorite: true);
        var html = await fixture.ChooseGenus("special");
        Assert.Contains("data-genus-current=\"false\"", html);
        Assert.Contains("data-genus-source=\"Saved\"", html);
        Assert.Single(Regex.Matches(html, "data-genus-saved-stand").Cast<Match>());
        Assert.Contains(savedLabel, html); Assert.Contains(presetLabel, html); Assert.Contains(sharedLabel, html);
        Assert.Contains("datetime=\"2026-11-09T15:00:00.0000000+00:00\"", html);
        Assert.Contains("data-genus-applied-stat=", html);
        var first = SlotRow(html, 1);
        Assert.Contains("MAX ROLL!", first); Assert.Contains("class=\"cgi-range\"", first);
        Assert.Contains(">16", first);
        Assert.DoesNotContain("data-genus-partial", html);
        Assert.DoesNotContain(UiText.T("Warte auf aktuelle Genus-Daten deines Charakters."), html);
        Assert.DoesNotContain("PRIVATE_", html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task PartialHistoricalCoverageShowsSavedValuesAndAPartialNoteWithoutWaitingForLiveData(string language)
    {
        await using var fixture = new Fixture(language);
        var saved = Historical(Complete()) with { AppliedEffectsStatus = CombatKnowledgeStatus.Partial, Preset = null };
        var html = await fixture.Render(saved);
        Assert.Contains("data-genus-source=\"Saved\"", html);
        Assert.Contains("data-genus-partial", html);
        Assert.Contains(UiText.T("Teilstand: Einige gespeicherte Effekte sind noch nicht vollständig zugeordnet."), html);
        Assert.Contains(UiText.T("Das aktive Preset dieses Charakters ist noch nicht zugeordnet."), html);
        Assert.Contains(">30</strong>", AppliedRow(html, "HPMax"));
        Assert.DoesNotContain(UiText.T("Warte auf aktuelle Genus-Daten deines Charakters."), html);
        Assert.DoesNotContain(UiText.T("Öffne Genus Insight im Spiel, um das aktive Preset zu bestätigen."), html);
    }

    [Theory]
    [InlineData("de", "Gespeicherter Genus-Stand")]
    [InlineData("en", "Saved Genus state")]
    public async Task SourceCategoryHeaderRecognizesSavedGenusDataWithoutACharacterSnapshot(string language, string label)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.RenderCategory(Historical(Complete()));
        var header = html[..html.IndexOf("</header>", StringComparison.Ordinal)];
        Assert.Contains(label, header);
        Assert.Contains("data-source-observation-status=\"Stale\"", html);
        Assert.Contains("data-genus-source=\"Saved\"", html);
        Assert.Contains(">30</strong>", AppliedRow(html, "HPMax"));
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionGenusInsight(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_COMBAT_GENUS_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var favorites = ImmutableHashSet.Create(new GenusInsightFavoriteKey("special", 1, "FixingDamage"),
            new GenusInsightFavoriteKey("special", 4, "HardHit"));
        await using var fixture = new Fixture(language);
        await fixture.Render(AnonymousSample(favorites), favorites, canFavorite: true);
        await fixture.ChooseGenus("special");
        var markup = await fixture.Markup();
        Assert.Contains("MAX ROLL!", SlotRow(WebUtility.HtmlDecode(markup), 1));
        Assert.DoesNotContain("PRIVATE_", markup);
        ExportMarkup(folder, repository, "genus-insight-" + language + ".html", language, markup);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousSavedProductionGenusInsight(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_COMBAT_GENUS_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var favorites = ImmutableHashSet.Create(new GenusInsightFavoriteKey("special", 1, "FixingDamage"),
            new GenusInsightFavoriteKey("special", 4, "HardHit"));
        await using var fixture = new Fixture(language);
        await fixture.Render(Historical(AnonymousSample(favorites)), favorites, canFavorite: true);
        await fixture.ChooseGenus("special");
        var markup = await fixture.Markup();
        Assert.Contains("data-genus-source=\"Saved\"", markup);
        Assert.Contains("MAX ROLL!", SlotRow(WebUtility.HtmlDecode(markup), 1));
        Assert.DoesNotContain("PRIVATE_", markup);
        ExportMarkup(folder, repository, "genus-insight-saved-" + language + ".html", language, markup);
    }

    private static GenusInsightSnapshot Historical(GenusInsightSnapshot state) => state with
    {
        IsCurrent = false, IsHistorical = true, Generation = 0, Sequence = 0, Preset = 1,
        MenuVisible = false, SelectedGenus = null, ViewedPreset = null, ViewedBoard = null,
        Boards = Array.AsReadOnly(state.Boards.Select(board => board with
        {
            Slots = Array.AsReadOnly(board.Slots.Select(slot => slot with { IsCurrent = false }).ToArray())
        }).ToArray())
    };

    private static void ExportMarkup(string folder, string repository, string filename, string language, string markup)
    {
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css", "genus-insight.css" }
            .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        var baseUri = new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, filename),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<base href=\"" + WebUtility.HtmlEncode(baseUri) + "\"><style>" + css
            + "\nhtml,body{height:auto;overflow:visible}body{padding:24px}.cgi-view{height:auto;overflow:visible;max-width:1450px;margin:auto}</style></head><body>" + markup + "</body></html>");
    }

    private static GenusInsightSnapshot AnonymousSample(IReadOnlySet<GenusInsightFavoriteKey> favorites)
    {
        var catalog = GenusInsightCatalog.Default;
        var preferred = new[] { "FixingDamage", "HPMax", "Accuracy", "Critical", "Defense" };
        var boards = new List<GenusInsightBoardView>();
        foreach (var genus in GenusInsightPresentation.Genera)
        {
            var slots = new List<GenusInsightSlotView>();
            for (byte slot = 1; slot <= 9; slot++)
            {
                var rank = (byte)(1 + (slot - 1) % 5);
                if (genus == "special" && slot == 1) rank = 5;
                var available = catalog.Options(genus, slot).Where(option => option.Rank == rank).ToArray();
                var option = genus == "special" && slot == 4
                    ? available.Single(option => option.StatId == "HardHit")
                    : available.OrderBy(option => Array.IndexOf(preferred, option.StatId) is var priority && priority >= 0 ? priority : int.MaxValue)
                        .ThenBy(option => option.StatId, StringComparer.Ordinal).First();
                var definition = catalog.FindStat(option.StatId)!;
                var range = catalog.Range(genus, slot, option.StatId)!;
                var value = slot % 3 == 1 ? option.Maximum : option.Minimum;
                var fraction = range.Maximum > range.Minimum ? (double)((value - range.Minimum) / (range.Maximum - range.Minimum)) : 1d;
                slots.Add(new()
                {
                    Slot = slot, StatId = option.StatId, LabelDe = definition.LabelDe, LabelEn = definition.LabelEn,
                    Value = value, Unit = definition.Unit, Rank = rank, Minimum = range.Minimum, Maximum = range.Maximum,
                    RollFraction = fraction, IsMaxRoll = value == range.Maximum,
                    IsFavorite = favorites.Contains(new(genus, slot, option.StatId)), IsCurrent = true,
                    IsActivated = true, IsLocked = slot == 1
                });
            }
            boards.Add(new(genus, 10, Array.AsReadOnly(slots.ToArray())));
        }
        var applied = boards.SelectMany(board => board.Slots).GroupBy(slot => slot.StatId!).Select(group =>
        {
            var stat = catalog.FindStat(group.Key)!;
            return new GenusInsightAppliedEffect(stat.StatId, stat.LabelDe, stat.LabelEn, stat.Unit, group.Sum(slot => slot.Value));
        }).ToArray();
        return new()
        {
            IsCurrent = true, Generation = 1, Sequence = 1, ObservedAt = At, Preset = 1,
            Boards = Array.AsReadOnly(boards.ToArray()), AppliedEffects = Array.AsReadOnly(applied),
            AppliedEffectsStatus = CombatKnowledgeStatus.Complete
        };
    }

    private static GenusInsightSnapshot Complete() => new()
    {
        ProfileId = "PRIVATE_PROFILE", IsCurrent = true, Generation = 7, Sequence = 12, ObservedAt = At,
        AppliedEffectsStatus = CombatKnowledgeStatus.Complete,
        AppliedEffects = [new("HPMax", "LP", "HP", "Points", 30),
            new("AmplifyAllDamage", "Schadensverstärkung", "Damage Boost", "Percent", 1.5m)],
        Boards = [new("cogni", 10, [])]
    };

    private static string RangeText(GenusInsightRange? range, string unit)
    {
        if (range is null) return "—";
        var suffix = unit == "Percent" ? " %" : "";
        var maximum = range.Maximum.ToString("0.##", CultureInfo.InvariantCulture) + suffix;
        return range.Minimum == range.Maximum ? maximum : range.Minimum.ToString("0.##", CultureInfo.InvariantCulture) + "–" + maximum;
    }
    private static string AppliedRow(string html, string stat) => Regex.Match(html,
        "<div[^>]*data-genus-applied-stat=\"" + Regex.Escape(stat) + "\"[^>]*>.*?</div>", RegexOptions.Singleline).Value;
    private static string OptionRow(string html, string stat) => Regex.Match(html,
        "<tr[^>]*data-genus-option-stat=\"" + Regex.Escape(stat) + "\"[^>]*>.*?</tr>", RegexOptions.Singleline).Value;
    private static string SlotRow(string html, int slot) => Regex.Match(html,
        "<button[^>]*data-genus-slot=\"" + slot.ToString(CultureInfo.InvariantCulture) + "\"[^>]*>.*?</button>", RegexOptions.Singleline).Value;
    private static string Button(string html, string attribute, string value) => Regex.Match(html,
        "<button[^>]*" + attribute + "=\"" + Regex.Escape(value) + "\"[^>]*>").Value;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previousLanguage = UiText.Language;
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedGenus? _view;
        private ImmutableHashSet<GenusInsightFavoriteKey> _favorites = ImmutableHashSet<GenusInsightFavoriteKey>.Empty;
        public List<GenusInsightFavoriteKey> Toggled { get; } = [];
        public Fixture(string language = "en")
        {
            UiText.Language = language;
            _services = new ServiceCollection().AddLogging().BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        public Task<string> Render(GenusInsightSnapshot state, ImmutableHashSet<GenusInsightFavoriteKey>? favorites = null,
            bool canFavorite = false, string? error = null) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _favorites = favorites ?? ImmutableHashSet<GenusInsightFavoriteKey>.Empty;
            _root = await _renderer.RenderComponentAsync<CapturedGenus>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CapturedGenus.State)] = state, [nameof(CapturedGenus.Favorites)] = _favorites,
                [nameof(CapturedGenus.FavoriteError)] = error,
                [nameof(CapturedGenus.ToggleFavorite)] = canFavorite
                    ? EventCallback.Factory.Create<GenusInsightFavoriteKey>(this, (Action<GenusInsightFavoriteKey>)Toggle)
                    : default(EventCallback<GenusInsightFavoriteKey>),
                [nameof(CapturedGenus.Captured)] = (Action<CapturedGenus>)(view => _view = view)
            }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        private void Toggle(GenusInsightFavoriteKey key)
        {
            Toggled.Add(key); _favorites = _favorites.Contains(key) ? _favorites.Remove(key) : _favorites.Add(key);
            _view!.SetFavorites(_favorites);
        }
        public Task<string> ChooseGenus(string genus) => Click("data-genus-category", genus);
        public Task<string> ChooseSlot(int slot) => Click("data-genus-slot", slot.ToString(CultureInfo.InvariantCulture));
        public Task Favorite(string stat) => _renderer.Dispatcher.InvokeAsync(() => _view!.Click("data-genus-favorite", stat));
        private Task<string> Click(string attribute, string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Click(attribute, value); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task Update(GenusInsightSnapshot state) => _renderer.Dispatcher.InvokeAsync(() => _view!.Update(state));
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public Task<string> RenderCategory(GenusInsightSnapshot state) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await _renderer.RenderComponentAsync<CombatSourceCategoryView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CombatSourceCategoryView.Section)] = "genus-insight",
                [nameof(CombatSourceCategoryView.Snapshot)] = CharacterCombatSnapshot.Empty,
                [nameof(CombatSourceCategoryView.GenusState)] = state
            }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        public async ValueTask DisposeAsync()
        { await _renderer.DisposeAsync(); await _services.DisposeAsync(); UiText.Language = _previousLanguage; }
    }

    public sealed class CapturedGenus : CombatGenusInsightView
    {
        [Parameter] public Action<CapturedGenus>? Captured { get; set; }
        protected override void OnInitialized() => Captured?.Invoke(this);
        internal void Update(GenusInsightSnapshot state) { State = state; StateHasChanged(); }
        internal void SetFavorites(IReadOnlySet<GenusInsightFavoriteKey> favorites) { Favorites = favorites; StateHasChanged(); }
#pragma warning disable BL0006 // Exercise the actual pointer and native-keyboard callbacks produced by the Razor component.
        internal async Task Click(string attribute, string value)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatGenusInsightView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == attribute
                && Convert.ToString(frames.Array[i].AttributeValue, CultureInfo.InvariantCulture) == value);
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeValue;
            if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
            else if (callback is EventCallback untyped) await untyped.InvokeAsync();
            else if (callback is Action synchronous) synchronous();
            else if (callback is Func<Task> asynchronous) await asynchronous();
            else throw new InvalidOperationException("Unexpected Genus Insight production callback.");
            StateHasChanged();
        }
#pragma warning restore BL0006
    }
}
