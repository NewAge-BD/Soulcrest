using System.Collections.Immutable;
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
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatScoreDisplayStatViewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("de", "Hauptwerte", "Nebenwerte", "0,125 %", "Vorwärtsrechnung weicht ab")]
    [InlineData("en", "Main Stats", "Sub Stats", "0.125 %", "Forward calculation differs")]
    public async Task ProductionStatsViewSeparatesDisplayValuesEvidenceRawComponentsAndTheCpModel(
        string language, string main, string sub, string percent, string conflict)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            using var fixture = new Fixture();
            await using var provider = Services(fixture);
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            CapturedView? view = null;
            var root = await Render(renderer, DemoSnapshot(), value => view = value);
            await renderer.Dispatcher.InvokeAsync(() => view!.ChooseStats());
            var html = await Html(renderer, root);
            Assert.Contains(main, html); Assert.Contains(sub, html);
            Assert.Contains(language == "de" ? "Übersicht" : "Overview", html);
            Assert.Contains(language == "de" ? "1 Zuordnung ungeklärt" : "1 mapping unqualified", html);
            Assert.Contains("data-display-stats-count=\"4\"", html);
            Assert.Contains("data-display-generation=\"7\"", html);
            Assert.Contains("data-display-version=\"" + CharacterDisplayStatProjection.Version + "\"", html);
            Assert.Contains("data-display-status=\"Partial\"", html);
            Assert.Contains("data-display-category=\"Main\"", html);
            Assert.Contains("data-display-category=\"Sub\"", html);
            Assert.Contains("data-display-stat-at=\"2026-11-03T12:00:00.0000000+00:00\"", html);
            Assert.Contains("data-display-value=\"0.125\"", DisplayRow(html, "SyntheticPercent"));
            Assert.Contains(percent, DisplayRow(html, "SyntheticPercent"));
            Assert.Contains("data-display-qualification=\"ObservedMatch\"", DisplayRow(html, "SyntheticPercent"));
            Assert.Contains("data-display-value=\"0\"", DisplayRow(html, "SyntheticZero"));
            Assert.Contains("data-display-stat-value>0</td>", DisplayRow(html, "SyntheticZero"));
            Assert.Contains("data-display-value=\"\"", DisplayRow(html, "SyntheticMissing"));
            Assert.Contains("data-display-stat-value>—</td>", DisplayRow(html, "SyntheticMissing"));
            Assert.Contains("data-display-value=\"1400\"", DisplayRow(html, "HPMax"));
            Assert.Contains("data-display-forward-value=\"1399\"", DisplayRow(html, "HPMax"));
            Assert.Contains(conflict, DisplayRow(html, "HPMax"));
            Assert.Contains("data-display-conflict-count=\"1\"", html);
            Assert.Contains("data-native-stats-details", html); Assert.Contains("data-model-stats-details", html);
            Assert.Contains("data-native-stat=\"193\"><th>193</th><td data-native-stat-raw>1200</td>", html);
            Assert.Contains("data-native-combat-score>45678", html);
            Assert.Contains("data-model-combat-score>—", html);
            Assert.DoesNotContain("fixture-profile", html); Assert.DoesNotContain("FixtureHiddenIdentity", html);
            Assert.DoesNotContain("<input", html);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task ProductionViewRefreshesFromOwnNetworkChangesAndRemovesCurrentValuesOnStaleAndStop()
    {
        using var fixture = new Fixture(); fixture.Publish(1200, 1);
        await using var provider = Services(fixture);
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        CapturedView? view = null;
        var root = await Render(renderer, null, value => view = value);
        await renderer.Dispatcher.InvokeAsync(() => view!.ChooseStats());
        Assert.Contains("data-display-value=\"1320\"", DisplayRow(await Html(renderer, root), "HPMax"));

        await renderer.Dispatcher.InvokeAsync(() => fixture.Publish(1300, 2));
        Assert.Contains("data-display-value=\"1430\"", DisplayRow(await Html(renderer, root), "HPMax"));

        await renderer.Dispatcher.InvokeAsync(() => fixture.Publish(1300, 3, fresh: false));
        var stale = DisplayRow(await Html(renderer, root), "HPMax");
        Assert.Contains("data-display-status=\"Stale\"", stale);
        Assert.Contains("data-display-value=\"\"", stale);
        Assert.Contains("data-display-stat-value>—</td>", stale);

        await renderer.Dispatcher.InvokeAsync(() => { fixture.Source.Running = false; fixture.Source.Raise(); });
        var stopped = await Html(renderer, root);
        Assert.Contains("data-display-value=\"\"", DisplayRow(stopped, "HPMax"));
        Assert.Contains("data-native-combat-score>—", stopped);
    }

    [Fact]
    public async Task EmptyCategoryShowsAnExplicitGapAndParentRerenderUsesTheNewLanguage()
    {
        var previous = UiText.Language; UiText.Language = "de";
        try
        {
            using var fixture = new Fixture();
            await using var provider = Services(fixture);
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            CapturedView? view = null;
            var snapshot = DemoSnapshot() with { DisplayStats = new() { Rows = [DemoSnapshot().DisplayStats.Rows[0]] } };
            var root = await Render(renderer, snapshot, value => view = value);
            await renderer.Dispatcher.InvokeAsync(() => view!.ChooseStats());
            var german = await Html(renderer, root);
            Assert.Contains("Für diese Kategorie liegt noch keine qualifizierte Zuordnung vor.", german);
            Assert.Contains("data-display-category=\"Sub\" data-display-category-count=\"0\"", german);
            UiText.Language = "en";
            await renderer.Dispatcher.InvokeAsync(() => view!.RefreshLanguage());
            var english = await Html(renderer, root);
            Assert.Contains("Main Stats", english); Assert.Contains("Sub Stats", english);
            Assert.Contains("No qualified mapping is available for this category yet.", english);
            Assert.Contains("Directly observed", english);
        }
        finally { UiText.Language = previous; }
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionDisplayStats(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_DISPLAY_STATS_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            using var fixture = new Fixture();
            await using var provider = Services(fixture);
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            CapturedView? view = null;
            var root = await Render(renderer, ExportSnapshot(), value => view = value);
            await renderer.Dispatcher.InvokeAsync(() => view!.ChooseStats());
            var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            Assert.DoesNotContain("fixture-profile", html); Assert.DoesNotContain("FixtureHiddenIdentity", html);
            var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
            var css = File.ReadAllText(Path.Combine(wwwroot, "css", "app.css")) + "\n"
                + File.ReadAllText(Path.Combine(wwwroot, "combat-score-calculator.css"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "display-stats-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><style>" + css
                + "\nhtml,body{height:auto}.cc-calculator{height:auto;overflow:visible}</style></head><body>" + html + "</body></html>");
        }
        finally { UiText.Language = previous; }
    }

    private static ServiceProvider Services(Fixture fixture)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(fixture.Character);
        return services.BuildServiceProvider();
    }

    private static Task<HtmlRootComponent> Render(HtmlRenderer renderer, CharacterCombatSnapshot? snapshot, Action<CapturedView> captured) =>
        renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CapturedView>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(CapturedView.InitialSnapshot)] = snapshot, [nameof(CapturedView.Captured)] = captured })));
    private static async Task<string> Html(HtmlRenderer renderer, HtmlRootComponent root) =>
        WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
    private static string DisplayRow(string html, string id)
    {
        var match = Regex.Match(html, "<tr\\b[^>]*data-display-stat=\"" + Regex.Escape(id) + "\"[^>]*>.*?</tr>", RegexOptions.Singleline);
        Assert.True(match.Success, "Display row missing: " + id);
        return match.Value;
    }

    private static CharacterCombatSnapshot DemoSnapshot() => new()
    {
        ProfileId = "fixture-profile", ClassId = "Cleric", CharacterLevel = 45, IsFresh = true, CurrentCombatScore = 45678,
        Generation = 7, ObservedAt = At,
        NativeStats = new(ImmutableDictionary<ushort, int>.Empty.Add(193, 1200), NetworkCombatSourceStatus.Complete, true, At, 1, "own-full-stat-list"),
        DisplayStats = new()
        {
            Generation = 7, ClientBuild = 26262663, Status = CombatKnowledgeStatus.Partial,
            Rows =
            [
                new() { Id = "HPMax", LabelDe = "Max. LP", LabelEn = "Max HP", Category = CharacterDisplayStatCategory.Main,
                    Value = 1400, Status = CombatKnowledgeStatus.Complete, Qualification = CharacterDisplayStatQualification.NativeDirect,
                    ObservedAt = At, Source = "own-entry-resource-maxima", Formula = "own.hpMax", ForwardValue = 1399,
                    ForwardStatus = CombatKnowledgeStatus.Complete, ComparisonStatus = CombatKnowledgeStatus.Conflict },
                new() { Id = "SyntheticZero", LabelDe = "Testwert Null", LabelEn = "Test zero", Category = CharacterDisplayStatCategory.Main,
                    Value = 0, Status = CombatKnowledgeStatus.Complete, Qualification = CharacterDisplayStatQualification.ControlledChange },
                new() { Id = "SyntheticPercent", LabelDe = "Test-Prozentwert", LabelEn = "Test percentage", Category = CharacterDisplayStatCategory.Sub,
                    Value = .125m, Unit = CombatStatUnit.Percent, Status = CombatKnowledgeStatus.Complete,
                    Qualification = CharacterDisplayStatQualification.ObservedMatch },
                new() { Id = "SyntheticMissing", LabelDe = "Testwert ohne Eingabe", LabelEn = "Test missing input", Category = CharacterDisplayStatCategory.Sub,
                    Status = CombatKnowledgeStatus.Unknown, Qualification = CharacterDisplayStatQualification.Unknown, MissingInputIds = [999] }
            ]
        }
    };

    private static CharacterCombatSnapshot ExportSnapshot()
    {
        var raw = new Dictionary<ushort, int>
        {
            [1] = 60, [2] = 45, [4] = 35, [5] = 40, [6] = 10,
            [193] = 1200, [196] = 1250, [199] = 500, [197] = 2000,
            [317] = 150, [31] = 100, [33] = 140, [19] = 30, [425] = 2500,
            [52] = 200, [307] = 1000, [426] = 1000, [104] = 100, [318] = 40, [427] = 500,
            [116] = 50, [312] = 10, [428] = 500, [128] = 150, [429] = 500, [133] = 125, [430] = 600,
            [282] = 1834, [205] = 475,
            [284] = 125, [69] = 17, [38] = 8, [41] = 40, [98] = 12, [99] = 60, [28] = 725, [70] = 0
        };
        var display = CharacterDisplayStatProjection.Evaluate(new()
        {
            ClientBuild = 26262663, ClassId = "Cleric", Generation = 7,
            NativeStats = new(raw, CombatKnowledgeStatus.Complete, true, 7, At, "own-full-stat-list"),
            HpMaximum = new(1350, CombatKnowledgeStatus.Complete, true, 7, At, "own-entry-resource-maxima", CharacterDisplayVitalSourceKind.Own),
            MpMaximum = new(600, CombatKnowledgeStatus.Complete, true, 7, At, "own-entry-resource-maxima", CharacterDisplayVitalSourceKind.Own)
        });
        Assert.Equal(26, display.Rows.Count(row => row.Category == CharacterDisplayStatCategory.Main));
        Assert.Contains(display.Rows, row => row.Category == CharacterDisplayStatCategory.Sub && row.Value == 0);
        Assert.Contains(display.Rows, row => row.Category == CharacterDisplayStatCategory.Sub && row.Value is null);
        return DemoSnapshot() with
        {
            DisplayStats = display,
            NativeStats = new(raw.ToImmutableDictionary(), NetworkCombatSourceStatus.Complete, true, At, 1, "own-full-stat-list")
        };
    }

    private sealed class Fixture : IDisposable
    {
        internal Source Source { get; } = new();
        private OwnSkillStateService Skills { get; }
        internal CharacterCombatStateService Character { get; }
        internal Fixture()
        {
            static LootTrackerCharacterSnapshot? Resolve(OwnCharacterContext _) => new("fixture-profile", "Unused");
            Skills = new(Source, Resolve); Character = new(Source, Skills, Resolve);
        }
        internal void Publish(int flatHp, long sequence, bool fresh = true)
        {
            var own = new OwnCharacterContext(new(101, 501, 300, "FixtureHiddenIdentity") { ClassId = "Cleric" }, 7)
                { CombatScore = 45678, ObservedAt = At };
            Source.SkillSnapshot = new(own, ImmutableDictionary<int, OwnSkillLevel>.Empty, true, true, At, sequence, "synthetic-skills");
            Source.CombatSnapshot = new()
            {
                Character = own, ClassId = "Cleric", CharacterLevel = 45, Generation = 7, Sequence = sequence,
                CurrentCombatScore = 45678, ObservedAt = At,
                Stats = new(ImmutableDictionary<ushort, int>.Empty.Add(193, flatHp).Add(196, 1000),
                    NetworkCombatSourceStatus.Complete, fresh, At, sequence, "own-full-stat-list")
            };
            Source.Raise();
        }
        public void Dispose() { Character.Dispose(); Skills.Dispose(); }
    }

    private sealed class Source : INetworkCharacterCombatSource, INetworkSkillSource
    {
        public bool Running { get; set; } = true;
        public NetworkCharacterCombatSnapshot CombatSnapshot { get; set; } = NetworkCharacterCombatSnapshot.Empty;
        public NetworkSkillSnapshot SkillSnapshot { get; set; } = NetworkSkillSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        internal void Raise() => Changed?.Invoke();
    }

    private sealed class CapturedView : CombatScoreCalculatorView
    {
        [Parameter] public CharacterCombatSnapshot? InitialSnapshot { get; set; }
        [Parameter] public Action<CapturedView>? Captured { get; set; }
        protected override void OnInitialized()
        {
            base.OnInitialized();
            if (InitialSnapshot is not null)
                typeof(CombatScoreCalculatorView).GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, InitialSnapshot);
            Captured?.Invoke(this);
        }
        internal void RefreshLanguage() => StateHasChanged();
#pragma warning disable BL0006
        internal async Task ChooseStats()
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatScoreCalculatorView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "data-combat-section" && Equals(frames.Array[i].AttributeValue, "stats"));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeValue;
            if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
            else if (callback is EventCallback untyped) await untyped.InvokeAsync();
            else if (callback is Action action) action();
            else if (callback is Func<Task> function) await function();
            else throw new InvalidOperationException("Unexpected stats tab callback.");
            StateHasChanged();
        }
#pragma warning restore BL0006
    }
}
