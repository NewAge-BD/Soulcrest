using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CharacterCombatStateTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DisplayProjectionUpdatesSynchronouslyWithSeparateOwnMaximaAndRemainsImmutable()
    {
        using var fixture = new Fixture(); fixture.Publish();
        var current = fixture.Source.CombatSnapshot;
        fixture.Source.CombatSnapshot = current with
        {
            Sequence = 2,
            Stats = new(ImmutableDictionary<ushort, int>.Empty.Add(193, 1000).Add(196, 500).Add(199, 200).Add(197, 2500),
                NetworkCombatSourceStatus.Complete, true, At, 2, "own-full-stat-list"),
            HpMaximum = new(new(1050), NetworkCombatSourceStatus.Complete, true, At, 2, "own-entry-resource-maxima"),
            MpMaximum = new(new(250), NetworkCombatSourceStatus.Complete, true, At, 2, "own-entry-resource-maxima")
        };
        fixture.Source.Raise();
        var before = fixture.Character.Snapshot;
        var hp = before.DisplayStats.Rows.Single(row => row.Id == "HPMax");
        Assert.Equal(1050m, hp.Value); Assert.Equal(1050m, hp.ForwardValue);
        Assert.Equal(CharacterDisplayStatQualification.NativeDirect, hp.Qualification);
        Assert.Equal(250m, before.DisplayStats.Rows.Single(row => row.Id == "MPMax").Value);
        fixture.Source.CombatSnapshot = fixture.Source.CombatSnapshot with
        {
            Sequence = 3,
            HpMaximum = new(new(1049), NetworkCombatSourceStatus.Complete, true, At.AddSeconds(1), 3, "own-resource-maxima-update")
        };
        fixture.Source.Raise();
        var changed = fixture.Character.Snapshot.DisplayStats;
        hp = changed.Rows.Single(row => row.Id == "HPMax");
        Assert.Equal(1049m, hp.Value); Assert.Equal(1050m, hp.ForwardValue);
        Assert.Equal(CombatKnowledgeStatus.Conflict, hp.ComparisonStatus);
        Assert.Equal(At.AddSeconds(1), hp.ObservedAt);
        Assert.Equal(At, changed.Rows.Single(row => row.Id == "MPMax").ObservedAt);
        Assert.Equal(1050m, before.DisplayStats.Rows.Single(row => row.Id == "HPMax").Value);
        fixture.Bound = false; fixture.Source.Raise();
        Assert.False(fixture.Character.Snapshot.IsFresh);
        Assert.All(fixture.Character.Snapshot.DisplayStats.Rows, row => Assert.Null(row.Value));
    }

    [Fact]
    public async Task CharacterBoundTitleOwnershipIsProjectedIndependentlyOfStaleCharacterSlots()
    {
        using var fixture = new Fixture(); fixture.Publish();
        var before = fixture.Source.CombatSnapshot;
        fixture.Source.CombatSnapshot = before with
        {
            Sequence = 2,
            TitleOwnership = new(ImmutableHashSet.Create(8001, 8002), NetworkCombatSourceStatus.Complete,
                true, At, 2, "own-title-ownership-list"),
            Titles = before.Titles with { Status = NetworkCombatSourceStatus.Stale, IsFresh = false }
        };
        fixture.Source.Raise(); await fixture.WaitForGraph();
        var snapshot = fixture.Character.Snapshot;
        Assert.True(snapshot.TitleOwnership.IsFresh); Assert.Equal(At, snapshot.TitleOwnership.ObservedAt);
        Assert.Equal(new[] { 8001, 8002 }, snapshot.TitleOwnership.Value!.Order());
        Assert.False(snapshot.Titles.IsFresh);
        Assert.Equal(2, snapshot.Graph!.Sources.Count(row => row.Kind == CombatSourceKind.TitleOwned));
        Assert.DoesNotContain(snapshot.Graph.Sources, row => row.Kind == CombatSourceKind.TitleEquipped
            && row.Status is CombatKnowledgeStatus.Complete or CombatKnowledgeStatus.Partial);
        fixture.Source.Running = false; fixture.Source.Raise();
        Assert.Null(fixture.Character.Snapshot.TitleOwnership.Value);
    }

    [Fact]
    public async Task OwnProfileProjectionKeepsRawStatsAndComponentsWithoutInventingPointBalances()
    {
        using var fixture = new Fixture(); fixture.Publish(); await fixture.WaitForGraph();
        var snapshot = fixture.Character.Snapshot;
        Assert.True(snapshot.IsFresh); Assert.Equal("local-profile", snapshot.ProfileId);
        Assert.Equal("Cleric", snapshot.ClassId); Assert.Equal(45, snapshot.CharacterLevel);
        Assert.Equal(12345, snapshot.CurrentCombatScore);
        Assert.Equal(1250, snapshot.NativeStats.Value![1]);
        Assert.Equal(14, snapshot.Skills[17060000].TotalLevel);
        Assert.Equal(4, snapshot.Skills[17060000].AdditionalLevels?.Daevanion);
        Assert.NotNull(snapshot.Graph); Assert.NotNull(snapshot.Breakdown);
        var normal = snapshot.PointPools.Single(pool => pool.Pool == Soulcrest.Core.Daevanion.DaevanionBudgetPool.Normal);
        Assert.Equal(17, normal.Spent); Assert.Equal(3, normal.Bonus); Assert.Equal(840, normal.Capacity);
        Assert.Null(normal.Free); Assert.Null(normal.Total); Assert.Equal(NetworkCombatSourceStatus.Partial, normal.Status);
        Assert.Equal(0, snapshot.PointPools.Single(pool => pool.Pool == Soulcrest.Core.Daevanion.DaevanionBudgetPool.Ariel).Spent);
        Assert.DoesNotContain("SyntheticIdentity", snapshot.ToString());
    }

    [Fact]
    public void QualifiedBudgetKeepsObservedComponentsAndUpdatesDerivedFreePoints()
    {
        using var fixture = new Fixture(); fixture.Publish();
        PublishPointBudget(fixture);
        var before = fixture.Character.Snapshot;
        Assert.Equal(1, before.CombatLevel); Assert.Equal(0, before.ConquerorLevel);
        Assert.Equal(new int?[] { 416, 120, 44 }, before.PointPools.Select(pool => pool.Total));
        Assert.All(before.PointPools, pool =>
        {
            Assert.Equal(0, pool.Free); Assert.Equal(NetworkCombatSourceStatus.Complete, pool.Status);
            Assert.Equal(CharacterCombatStateService.DerivedPointBudgetSource, pool.Source);
        });
        Assert.Equal(280, before.PointPools[0].Bonus); Assert.Equal(416, before.PointPools[0].Spent);
        PublishPointBudget(fixture, normalSpent: 415);
        Assert.Equal(1, fixture.Character.Snapshot.PointPools[0].Free);
        Assert.Equal(416, fixture.Character.Snapshot.PointPools[0].Total);
        Assert.Equal(0, before.PointPools[0].Free);
        PublishPointBudget(fixture);
        Assert.Equal(0, fixture.Character.Snapshot.PointPools[0].Free);
    }

    [Theory]
    [InlineData(46, 1, 0)]
    [InlineData(45, 2, 0)]
    [InlineData(45, 1, 1)]
    [InlineData(null, 1, 0)]
    [InlineData(45, null, 0)]
    [InlineData(45, 1, null)]
    [InlineData(45, 0, 0)]
    public void UnqualifiedLevelOrGrowthNeverSuppliesBudget(int? level, int? combatLevel, int? conquerorLevel)
    {
        using var fixture = new Fixture(); fixture.Publish();
        PublishPointBudget(fixture, level, combatLevel, conquerorLevel);
        Assert.All(fixture.Character.Snapshot.PointPools, pool =>
        {
            Assert.Null(pool.Free); Assert.Null(pool.Total);
            Assert.Equal(NetworkCombatSourceStatus.Partial, pool.Status);
            Assert.NotNull(pool.Spent); Assert.NotNull(pool.Bonus);
        });
    }

    [Fact]
    public void SparsePointKeysStayUnknownPerPoolAndExplicitZerosRemainKnown()
    {
        using var fixture = new Fixture(); fixture.Publish(); PublishPointBudget(fixture);
        var current = fixture.Source.CombatSnapshot;
        var points = current.DaevanionPoints.Value!;
        fixture.Source.CombatSnapshot = current with { Sequence = current.Sequence + 1,
            DaevanionPoints = current.DaevanionPoints with { Value = points with
                { Used = points.Used.Remove(2), Bonus = points.Bonus.Remove(3) } } };
        fixture.Source.Raise();
        var pools = fixture.Character.Snapshot.PointPools;
        Assert.Equal(416, pools[0].Total);
        Assert.Null(pools[1].Total); Assert.Null(pools[1].Free); Assert.Null(pools[1].Spent);
        Assert.Null(pools[2].Total); Assert.Null(pools[2].Free); Assert.Null(pools[2].Bonus);
        fixture.Source.CombatSnapshot = fixture.Source.CombatSnapshot with { Sequence = current.Sequence + 2,
            DaevanionPoints = new(new(ImmutableDictionary<byte, ushort>.Empty.Add(1, 136).Add(2, 0).Add(3, 0),
                ImmutableDictionary<byte, ushort>.Empty.Add(1, 0).Add(2, 0).Add(3, 0)),
                NetworkCombatSourceStatus.Complete, true, At, 3, "synthetic-points") };
        fixture.Source.Raise();
        Assert.All(fixture.Character.Snapshot.PointPools, pool => { Assert.Equal(0, pool.Free); Assert.Equal(NetworkCombatSourceStatus.Complete, pool.Status); });
        Assert.Equal(new int?[] { 136, 0, 0 }, fixture.Character.Snapshot.PointPools.Select(pool => pool.Total));
    }

    [Theory]
    [InlineData(417, 280, 120, 120)]
    [InlineData(416, 800, 120, 120)]
    [InlineData(416, 280, 120, 233)]
    public void NegativeFreeOrCapacityOverflowIsConflictAndNeverUsable(int normalSpent, int normalBonus, int arielSpent, int arielBonus)
    {
        using var fixture = new Fixture(); fixture.Publish(); PublishPointBudget(fixture);
        var current = fixture.Source.CombatSnapshot;
        var points = current.DaevanionPoints.Value!;
        fixture.Source.CombatSnapshot = current with { Sequence = current.Sequence + 1,
            DaevanionPoints = current.DaevanionPoints with { Value = new(
                points.Used.SetItem(1, (ushort)normalSpent).SetItem(2, (ushort)arielSpent),
                points.Bonus.SetItem(1, (ushort)normalBonus).SetItem(2, (ushort)arielBonus)) } };
        fixture.Source.Raise();
        var conflict = fixture.Character.Snapshot.PointPools.Single(pool => pool.Status == NetworkCombatSourceStatus.Conflict);
        Assert.Null(conflict.Free); Assert.Null(conflict.Total); Assert.NotNull(conflict.Spent); Assert.NotNull(conflict.Bonus);
    }

    [Fact]
    public void DifferentCatalogBuildAndStalePointEvidenceCannotQualifyABudget()
    {
        var catalog = CombatEffectCatalog.Default;
        var otherBuild = new CombatEffectCatalog("different-build", 26262664, [],
            nativeExpPointLevels: catalog.NativeExpPointLevels);
        using (var fixture = new Fixture(otherBuild))
        {
            fixture.Publish(); PublishPointBudget(fixture);
            Assert.All(fixture.Character.Snapshot.PointPools, pool => { Assert.Null(pool.Free); Assert.Null(pool.Total); });
        }
        using var stale = new Fixture(); stale.Publish(); PublishPointBudget(stale);
        stale.Source.CombatSnapshot = stale.Source.CombatSnapshot with { Sequence = 3,
            DaevanionPoints = stale.Source.CombatSnapshot.DaevanionPoints with { IsFresh = false } };
        stale.Source.Raise();
        Assert.All(stale.Character.Snapshot.PointPools, pool =>
        { Assert.Null(pool.Free); Assert.Null(pool.Total); Assert.Equal(NetworkCombatSourceStatus.Stale, pool.Status); });
    }

    private static void PublishPointBudget(Fixture fixture, int? level = 45, int? combatLevel = 1, int? conquerorLevel = 0, int normalSpent = 416)
    {
        var sequence = fixture.Source.CombatSnapshot.Sequence + 1;
        fixture.Source.CombatSnapshot = fixture.Source.CombatSnapshot with
        {
            CharacterLevel = level, CombatLevel = combatLevel, ConquerorLevel = conquerorLevel, Sequence = sequence,
            DaevanionPoints = new(new(ImmutableDictionary<byte, ushort>.Empty.Add(1, (ushort)normalSpent).Add(2, 120).Add(3, 44),
                ImmutableDictionary<byte, ushort>.Empty.Add(1, 280).Add(2, 120).Add(3, 44)),
                NetworkCombatSourceStatus.Complete, true, At, sequence, "synthetic-points")
        };
        fixture.Source.Raise();
    }

    [Fact]
    public async Task PartialAndStaleDomainsNeverTurnMissingValuesIntoZeroOrActiveWitnesses()
    {
        using var fixture = new Fixture(); fixture.Publish(); await fixture.WaitForGraph();
        var first = fixture.Character.Snapshot;
        fixture.Source.CombatSnapshot = fixture.Source.CombatSnapshot with
        {
            Sequence = 2,
            Stats = new(ImmutableDictionary<ushort, int>.Empty.Add(1, 1600), NetworkCombatSourceStatus.Partial, true, At, 2, "synthetic-subset"),
            Abnormals = new(new([]), NetworkCombatSourceStatus.Complete, false, At, 2, "synthetic-stale"),
            DaevanionPoints = NetworkCombatDomain<CharacterDaevanionPointState>.Unknown
        };
        fixture.Source.Raise(); await fixture.WaitForGraph();
        var current = fixture.Character.Snapshot;
        Assert.Equal(NetworkCombatSourceStatus.Partial, current.NativeStats.Status);
        Assert.Equal(NetworkCombatSourceStatus.Stale, current.Abnormals.Status);
        Assert.Equal(1600, current.NativeStats.Value![1]); Assert.Equal(1250, first.NativeStats.Value![1]);
        Assert.All(current.PointPools, pool => { Assert.Null(pool.Spent); Assert.Null(pool.Bonus); Assert.Null(pool.Free); Assert.Null(pool.Total); });
    }

    [Fact]
    public async Task StopProfileMismatchAndDisposalImmediatelyRemoveCurrentAuthority()
    {
        using var fixture = new Fixture(); fixture.Publish(); await fixture.WaitForGraph();
        fixture.Bound = false;
        Assert.Same(CharacterCombatSnapshot.Empty, fixture.Character.Snapshot);
        fixture.Bound = true; fixture.Source.Running = false; fixture.Source.Raise();
        Assert.Same(CharacterCombatSnapshot.Empty, fixture.Character.Snapshot);
        fixture.Character.Dispose(); fixture.Source.Running = true; fixture.Source.Raise();
        Assert.Same(CharacterCombatSnapshot.Empty, fixture.Character.Snapshot);
    }

    [Theory]
    [InlineData("de", "Beobachteter Ingame-CP", "Unbekannt")]
    [InlineData("en", "Observed in-game CP", "Unknown")]
    public async Task ProductionViewSeparatesNativeCpFromTheModelAndShowsReadonlyDetailedSources(string language, string heading, string unknown)
    {
        var previousLanguage = UiText.Language;
        UiText.Language = language;
        try
        {
            using var fixture = new Fixture(); fixture.Publish(); PublishPointBudget(fixture); await fixture.WaitForGraph();
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(fixture.Character);
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            CapturedView? view = null;
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CapturedView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(CapturedView.Captured)] = (Action<CapturedView>)(value => view = value) })));
            var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Contains(heading, html); Assert.Contains("12345", html);
            Assert.Contains("data-model-combat-score>—", html);
            Assert.DoesNotContain("local-profile", html); Assert.DoesNotContain("SyntheticIdentity", html);
            Assert.DoesNotContain("type=\"number\"", html);
            Assert.Contains(UiText.T("Abgeleitet aus Levelbasis, Bonus und verteilten Punkten"), html);
            await renderer.Dispatcher.InvokeAsync(() => view!.Choose("stats"));
            html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Contains(language == "de" ? "Beobachtete Statdaten" : "Observed stat data", html);
            Assert.Contains(language == "de" ? "Rohwert" : "Raw value", html);
            Assert.Contains(language == "de" ? "Vollständigkeit der empfangenen Nachricht" : "completeness of the received message", html);
            Assert.Contains("data-native-stat=\"1\"><th>1</th><td data-native-stat-raw>1250</td>", html);
            Assert.DoesNotContain(language == "de" ? "Beobachtete Gesamtstats" : "Observed total stats", html);
            await renderer.Dispatcher.InvokeAsync(() => view!.Choose("sources"));
            html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Contains("data-source-kind=\"TitleOwned\"", html);
            Assert.Contains("data-source-kind=\"Soulbind\"", html);
            Assert.Contains("data-source-kind=\"Pet\"", html); Assert.Contains("data-source-kind=\"Genus\"", html);
            Assert.Contains(unknown, html);
            await renderer.Dispatcher.InvokeAsync(() => view!.Choose("skills"));
            html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Contains("data-observed-skill=\"17060000\"", html);
            Assert.DoesNotContain("<input", html);
        }
        finally { UiText.Language = previousLanguage; }
    }

    [Theory(Skip = PendingOwnWeights.Reason)]
    [InlineData("de", "So entsteht der Modellwert", "0,45", "1300,45", "Gewicht unbekannt")]
    [InlineData("en", "How the model value is calculated", "0.45", "1300.45", "Unknown weight")]
    public async Task ProductionCompositionShowsExactTermsUnknownWeightsAndOneRounding(
        string language, string heading, string statSubtotal, string unrounded, string unknownWeight)
    {
        var previousLanguage = UiText.Language; UiText.Language = language;
        try
        {
            var catalog = new CombatEffectCatalog("test", 1,
                [new() { StatId = "HPMax" }, new() { StatId = "Unweighted" }],
                [new("HPMax", .15m, CombatStatUnit.Points)]);
            var breakdown = CombatScoreModel.Evaluate(new CombatSourceGraph
            {
                Status = CombatKnowledgeStatus.Partial,
                Sources = [new() { Id = "fictional-source", Status = CombatKnowledgeStatus.Complete,
                    Effects = [new("HPMax", 3, Application: CombatApplication.Confirmed), new("Unweighted", 7, Application: CombatApplication.Confirmed)],
                    Skills = [new(17060000, DaevanionSkillKind.Active, 14, CombatApplication.Confirmed)] }]
            }, catalog);
            using var fixture = new Fixture(); fixture.Publish(); await fixture.WaitForGraph();
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(fixture.Character);
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            CapturedView? view = null;
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CapturedView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(CapturedView.Captured)] = (Action<CapturedView>)(value => view = value) })));
            await renderer.Dispatcher.InvokeAsync(() => view!.ShowBreakdown(breakdown));
            var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Contains(heading, html);
            Assert.Contains("data-score-stat-subtotal>" + statSubtotal, html);
            Assert.Contains("data-score-skill-subtotal>1300", html);
            Assert.Contains("data-score-unrounded>" + unrounded, html);
            Assert.Contains("data-score-rounded>1300", html);
            Assert.Contains("data-score-incomplete", html);
            Assert.Contains("data-model-combat-score>—", html);
            await renderer.Dispatcher.InvokeAsync(() => view!.Choose("stats"));
            html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Contains("data-score-stat=\"HPMax\"", html);
            Assert.Contains("data-score-contribution>" + statSubtotal, html);
            Assert.Contains("data-score-stat=\"Unweighted\"", html);
            Assert.Contains("data-score-weight>—", html);
            Assert.Contains(unknownWeight, html);
            Assert.Contains("data-score-skill=\"17060000\"", html);
            Assert.Contains("data-score-contribution>1300", html);
            Assert.DoesNotContain("SyntheticIdentity", html);
        }
        finally { UiText.Language = previousLanguage; }
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionViewsAndNativeRoute(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_COMBAT_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        var allowed = Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(allowed, folder, StringComparison.OrdinalIgnoreCase);
        var previousLanguage = UiText.Language; UiText.Language = language;
        try
        {
            using var fixture = new Fixture(); fixture.Publish(); PublishPointBudget(fixture); await fixture.WaitForGraph();
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(fixture.Character);
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            CapturedView? view = null;
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CapturedView>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(CapturedView.Captured)] = (Action<CapturedView>)(value => view = value) })));
            var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
            var css = File.ReadAllText(Path.Combine(wwwroot, "css", "app.css")) + "\n" + File.ReadAllText(Path.Combine(wwwroot, "combat-score-calculator.css"));
            Directory.CreateDirectory(folder);
            foreach (var section in new[] { "overview", "sources", "stats", "skills" })
            {
                await renderer.Dispatcher.InvokeAsync(() => view!.Choose(section));
                var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
                Assert.DoesNotContain("SyntheticIdentity", html); Assert.DoesNotContain("local-profile", html);
                File.WriteAllText(Path.Combine(folder, "combat-" + section + "-" + language + ".html"),
                    "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><style>" + css
                    + "\nhtml,body{height:auto}.cc-calculator{height:auto;overflow:visible}</style></head><body>" + html + "</body></html>");
            }

            // The exact production optimizer and native route painter, using explicit synthetic inputs.
            var context = Soulcrest.Core.Daevanion.DaevanionCombatScoreModel.CreateContext("Cleric");
            context = context with { Skills = context.Skills.Select((skill, index) => skill with
                { CurrentLevel = 14, CurrentBoardBonus = 0, TargetLevel = index == 0 ? 16 : null }).ToArray() };
            var request = Soulcrest.Core.Daevanion.DaevanionPlanningDefaults.CreateRequest("Cleric") with
                { CharacterLevel = 45, Budgets = new(20, 8, 8), CombatContext = context };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await new Soulcrest.Core.Daevanion.DaevanionOptimizer().OptimizeAsync(request, cancellationToken: stop.Token);
            Assert.True(result.Optimal);
            Assert.Null(Soulcrest.Core.Daevanion.DaevanionPlanValidator.Validate(request, result));
            var board = result.Boards.OrderByDescending(plan => plan.Cost).First();
            using var painter = new Soulcrest.App.Overlay.DaevanionOverlayRenderer(wwwroot);
            using var image = painter.Preview("Cleric", board.FamilyId, language, route: board);
            image.Save(Path.Combine(folder, "automatic-route-" + language + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        finally { UiText.Language = previousLanguage; }
    }

    private sealed class Fixture : IDisposable
    {
        internal Source Source { get; } = new();
        internal bool Bound { get; set; } = true;
        internal OwnSkillStateService Skills { get; }
        internal CharacterCombatStateService Character { get; }
        internal Fixture(CombatEffectCatalog? catalog = null)
        {
            LootTrackerCharacterSnapshot? Resolve(OwnCharacterContext _) => Bound ? new("local-profile", "Unused") : null;
            Skills = new(Source, Resolve); Character = new(Source, Skills, Resolve, catalog);
        }
        internal void Publish()
        {
            var own = new OwnCharacterContext(new(101, 501, 300, "SyntheticIdentity") { ClassId = "Cleric" }, 7) { CombatScore = 12345, ObservedAt = At };
            Source.SkillSnapshot = new(own, ImmutableDictionary<int, OwnSkillLevel>.Empty.Add(17060000,
                new(17060000, 14, 10, new(4, 0, 0, 0, 0))), true, true, At, 1, "synthetic-skills");
            Source.CombatSnapshot = new()
            {
                Character = own, ClassId = "Cleric", CharacterLevel = 45, CurrentCombatScore = 12345,
                Generation = 7, Sequence = 1, ObservedAt = At,
                Stats = new(ImmutableDictionary<ushort, int>.Empty.Add(1, 1250), NetworkCombatSourceStatus.Complete, true, At, 1, "synthetic-stats"),
                DaevanionPoints = new(new(ImmutableDictionary<byte, ushort>.Empty.Add(1, 17).Add(2, 0).Add(3, 0),
                    ImmutableDictionary<byte, ushort>.Empty.Add(1, 3)), NetworkCombatSourceStatus.Complete, true, At, 1, "synthetic-points"),
                Titles = new(new(ImmutableHashSet<int>.Empty, ImmutableDictionary<byte, int>.Empty.Add(1, 0)), NetworkCombatSourceStatus.Complete, true, At, 1, "synthetic-titles")
            };
            Source.Raise();
        }
        internal async Task WaitForGraph()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Character.Snapshot.IsFresh && Character.Snapshot.Graph is null) await Task.Delay(10, timeout.Token);
            Assert.NotNull(Character.Snapshot.Graph);
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
        [Parameter] public Action<CapturedView>? Captured { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
        internal void ShowBreakdown(CombatEvaluation breakdown)
        {
            var state = typeof(CombatScoreCalculatorView).GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)!;
            state.SetValue(this, ((CharacterCombatSnapshot)state.GetValue(this)!) with { Breakdown = breakdown });
            StateHasChanged();
        }
#pragma warning disable BL0006
        internal async Task Choose(string section)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatScoreCalculatorView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == "data-combat-section" && Equals(frames.Array[i].AttributeValue, section));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeValue;
            if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
            else if (callback is EventCallback untyped) await untyped.InvokeAsync();
            else if (callback is Action action) action();
            else if (callback is Func<Task> function) await function();
            else throw new InvalidOperationException("Unexpected tab callback.");
            StateHasChanged();
        }
#pragma warning restore BL0006
    }
}
