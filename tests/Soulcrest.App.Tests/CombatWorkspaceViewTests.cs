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
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatWorkspaceViewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Sections = ["stat-overview", "daevanion", "wings", "gear", "pets",
        "genus-insight", "pantheon", "achievements", "titles"];
    private static readonly (string Id, ushort NativeId, int Value)[] GodValues =
    [
        ("Justice", 7, 11), ("Freedom", 8, 22), ("Illusion", 9, 33), ("Life", 10, 44), ("Time", 11, 55),
        ("Destruction", 13, 66), ("Death", 14, 77), ("Wisdom", 15, 88), ("Destiny", 16, 99), ("Space", 17, 110)
    ];
    public static IEnumerable<object[]> GodStatCases => GodValues.Select(god => new object[] { god.Id, god.NativeId });

    [Theory]
    [InlineData("de", "Grundattribute", "Gottheitsattribute")]
    [InlineData("en", "Primary Attributes", "God Stats")]
    public async Task DefaultOverviewRendersTheActiveRosterInTheRequestedOrderWithExplicitGaps(
        string language, string primaryLabel, string godLabel)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(snapshot: DemoSnapshot());

        Assert.Contains("data-workspace-section=\"stat-overview\"", html);
        Assert.Contains("data-stat-overview", html);
        Assert.Contains("data-display-stats-count=\"195\"", html);
        Assert.Contains(primaryLabel, html); Assert.Contains(godLabel, html);
        Assert.Contains("Main Stats", html); Assert.Contains("Sub Stats", html);
        var groups = Regex.Matches(html, "data-stat-group=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "primary-attributes", "god-attributes", "main-stats", "combat", "hit-detection",
            "status-effects", "pvp", "pve", "genus", "elements", "special", "resources" }, groups);
        Assert.Equal(new[] { "Might", "Dexterity", "Precision", "Willpower", "Intelligence", "Constitution" },
            RowIds(Group(html, "primary-attributes")));
        Assert.Equal(new[] { "Attack", "Accuracy", "Critical", "HPMax", "CombatSpeed",
            "Defense", "Evasion", "CriticalResist", "MPMax", "MoveSpeed" }, RowIds(Group(html, "main-stats")));
        Assert.Equal(26, RowIds(Group(html, "primary-attributes")).Length + RowIds(Group(html, "god-attributes")).Length
            + RowIds(Group(html, "main-stats")).Length);
        Assert.Equal(169, groups.Skip(3).Sum(group => RowIds(Group(html, group)).Length));
        Assert.Equal(195, RowIds(html).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("data-display-value=\"1350\"", Row(html, "HPMax"));
        Assert.Contains("data-display-value=\"0\"", Row(html, "DecreaseDamage"));
        Assert.Contains("data-display-stat-value>0 %</strong>", Row(html, "DecreaseDamage"));
        foreach (var (id, value) in new[] { ("AmplifyBackAttack", "1.6"), ("AdditionalHitResistRate", "1.4") })
        {
            var row = Row(html, id);
            Assert.Contains("data-display-value=\"" + value + "\"", row);
            Assert.Contains("data-display-qualification=\"ControlledChange\"", row);
            Assert.Contains("data-display-status=\"Complete\"", row);
        }
        Assert.Contains("data-display-value=\"\"", Row(html, "Intelligence"));
        Assert.Contains("data-display-stat-value>—</strong>", Row(html, "FrontAttackDamage"));
        Assert.DoesNotContain("workspace-profile", html); Assert.DoesNotContain("FixtureHiddenIdentity", html);
        Assert.DoesNotContain("<input", html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task EverySectionUsesNativeKeyboardAccessibleButtonsAndItsProductionCallback(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(snapshot: DemoSnapshot());
        Assert.Equal(Sections, Regex.Matches(html, "data-combat-subtab=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value));
        foreach (var section in Sections)
        {
            var button = Regex.Match(html, "<button\\b[^>]*data-combat-subtab=\"" + Regex.Escape(section) + "\"[^>]*>").Value;
            Assert.Contains("type=\"button\"", button);
            Assert.DoesNotContain("tabindex=\"-1\"", button);

            html = await fixture.Choose(section);

            Assert.Equal(section, fixture.ChangedSections[^1]);
            Assert.Contains("data-workspace-section=\"" + section + "\"", html);
            if (section == "stat-overview") Assert.Contains("data-stat-overview", html);
            else if (section == "daevanion")
            {
                Assert.Contains("data-daevanion-boards", html);
                Assert.DoesNotContain("data-daevanion-calculator", html);
            }
            else if (section == "pantheon")
            {
                Assert.Contains("data-pantheon", html);
                Assert.Contains("data-pantheon-source=\"unknown\"", html);
                Assert.DoesNotContain("data-combat-source-category=\"pantheon\"", html);
            }
            else Assert.Contains("data-combat-source-category=\"" + section + "\"", html);
        }
        Assert.Equal(Sections, fixture.ChangedSections);
        Assert.False(File.Exists(fixture.PlanPath));
    }

    [Fact]
    public async Task AchievementFactionPassesThroughWorkspaceAndRefreshesTheSameComponent()
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.Render("achievements", faction: Factions.Elyos);
        AssertFactionView(html, Factions.Elyos, "elyos");

        html = await fixture.SetFaction(Factions.Asmodian);
        AssertFactionView(html, Factions.Asmodian, "asmodian");

        foreach (var faction in new string?[] { null, "unknown" })
        {
            html = await fixture.SetFaction(faction);
            AssertFactionView(html, faction, "unknown");
        }
        Assert.Empty(fixture.ChangedSections);
        Assert.False(File.Exists(fixture.PlanPath));

        static void AssertFactionView(string markup, string? faction, string expected)
        {
            Assert.Contains("data-achievement-faction=\"" + expected + "\"", markup);
            var visibleIds = Regex.Matches(markup, "data-achievement-card=\"([0-9]+)\"")
                .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.NotEmpty(visibleIds);
            Assert.All(visibleIds, id => Assert.True(CombatAchievementFactionCatalog.Default.IncludesFamily(id, faction),
                $"Achievement {id} does not match faction {expected}."));
        }
    }

    [Fact]
    public async Task PantheonFactionPassesThroughTheWorkspaceWithoutRequiringARegisteredStateService()
    {
        await using var fixture = new Fixture("en");
        foreach (var faction in new[] { Factions.Asmodian, Factions.Elyos })
        {
            var html = await fixture.Render("pantheon", snapshot: DemoSnapshot(), faction: faction);
            Assert.Contains("data-pantheon-source=\"unknown\"", html);
            Assert.Equal(98, Regex.Matches(html, "data-pantheon-decor=").Count);
            Assert.DoesNotContain("data-ownership=\"missing\"", html);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task UnsupportedSectionParametersFallBackToOverview(string section)
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.Render(section, DemoSnapshot());
        Assert.Contains("data-workspace-section=\"stat-overview\"", html);
        Assert.Contains("data-stat-overview", html);
        Assert.Empty(fixture.ChangedSections);
    }

    [Fact]
    public async Task UnavailableLightAndDarkStayOutsideTheActiveRosterEvenWhenSameNamedValuesExist()
    {
        var snapshot = DemoSnapshot();
        snapshot = snapshot with
        {
            NativeStats = snapshot.NativeStats with { Value = snapshot.NativeStats.Value!.Add(12, 0).Add(18, 0) },
            DisplayStats = snapshot.DisplayStats with
            {
                Rows = [.. snapshot.DisplayStats.Rows,
                    new() { Id = "Light", Value = 0, Qualification = CharacterDisplayStatQualification.ObservedMatch, Status = CombatKnowledgeStatus.Complete },
                    new() { Id = "Dark", Value = 42, Qualification = CharacterDisplayStatQualification.ObservedMatch, Status = CombatKnowledgeStatus.Complete }]
            }
        };
        await using var fixture = new Fixture("en");
        var html = await fixture.Render(snapshot: snapshot);
        foreach (var id in new[] { "Light", "Dark" })
        {
            Assert.DoesNotContain("data-display-stat=\"" + id + "\"", html);
        }
        Assert.Contains("data-display-stats-count=\"195\"", html);
        Assert.Contains("data-display-stat-value>—</strong>", Row(html, "FrontAttackDamage"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task TenQualifiedGodsRenderTheirExplicitOwnValuesWithUnchangedOrderLabelsAndIcons(string language)
    {
        var snapshot = DemoSnapshot();
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(snapshot: snapshot);
        var group = Group(html, "god-attributes");
        Assert.Equal(new[] { "Justice", "Freedom", "Illusion", "Life", "Time", "Destruction", "Death", "Wisdom", "Destiny", "Space" },
            RowIds(group));
        var observation = Assert.IsType<CombatStatObservationDto>(CombatStatObservationPresentation.Create(snapshot));
        foreach (var god in GodValues)
        {
            var row = Row(group, god.Id);
            Assert.Contains($"data-display-value=\"{god.Value}\"", row);
            Assert.Contains($"data-display-stat-value>{god.Value}</strong>", row);
            Assert.Contains("data-display-qualification=\"ObservedMatch\"", row);
            Assert.Contains("data-display-status=\"Complete\"", row);
            Assert.Contains(CombatWorkspaceCatalog.IconPath(god.Id)!, row);
            var definition = CombatStatOverviewCatalog.MainGroups.SelectMany(item => item.Rows).Single(item => item.Id == god.Id);
            Assert.Contains(language == "de" ? definition.LabelDe : definition.LabelEn, row);
            var projected = Assert.Single(observation.ProjectedRows, item => item.Id == god.Id);
            Assert.Equal(god.Value, projected.Value);
            var dependency = Assert.Single(projected.RawDependencies);
            Assert.Equal(god.NativeId, dependency.NativeId); Assert.Equal(god.Value, dependency.RawValue);
        }
        foreach (var id in new[] { "Light", "Dark" }) Assert.DoesNotContain("data-display-stat=\"" + id + "\"", group);
    }

    [Theory]
    [MemberData(nameof(GodStatCases))]
    public async Task EachGodRequiresItsOwnExplicitInputAndOnlyAnExplicitZeroRendersZero(string id, ushort nativeId)
    {
        var raw = GodValues.Where(god => god.Id != id).ToDictionary(god => god.NativeId, god => god.Value);
        var missing = DemoSnapshot(raw);
        await using var fixture = new Fixture("en");
        var html = await fixture.Render(snapshot: missing);
        var row = Row(html, id);
        Assert.Contains("data-display-value=\"\"", row);
        Assert.Contains("data-display-stat-value>—</strong>", row);
        Assert.Contains("data-display-status=\"Unknown\"", row);
        foreach (var other in GodValues.Where(god => god.Id != id))
            Assert.Contains($"data-display-value=\"{other.Value}\"", Row(html, other.Id));
        var missingDto = Assert.IsType<CombatStatObservationDto>(CombatStatObservationPresentation.Create(missing));
        var missingRow = Assert.Single(missingDto.ProjectedRows, item => item.Id == id);
        Assert.Null(missingRow.Value); Assert.Equal(new[] { nativeId }, missingRow.MissingInputIds);
        Assert.Null(Assert.Single(missingRow.RawDependencies).RawValue);

        raw[nativeId] = 0;
        var zero = DemoSnapshot(raw);
        html = await fixture.Render(snapshot: zero);
        row = Row(html, id);
        Assert.Contains("data-display-value=\"0\"", row);
        Assert.Contains("data-display-stat-value>0</strong>", row);
        Assert.Contains("data-display-status=\"Complete\"", row);
        var zeroDto = Assert.IsType<CombatStatObservationDto>(CombatStatObservationPresentation.Create(zero));
        var zeroRow = Assert.Single(zeroDto.ProjectedRows, item => item.Id == id);
        Assert.Equal(0, zeroRow.Value); Assert.Equal(0, Assert.Single(zeroRow.RawDependencies).RawValue);
    }

    [Theory]
    [InlineData("own")]
    [InlineData("native")]
    public async Task StaleOwnOrNativeEvidenceCannotRenderCurrentGodValues(string staleSource)
    {
        var snapshot = DemoSnapshot();
        if (staleSource == "own") snapshot = snapshot with { IsFresh = false };
        else snapshot = snapshot with
        {
            NativeStats = snapshot.NativeStats with { Status = NetworkCombatSourceStatus.Stale, IsFresh = false },
            DisplayStats = CharacterDisplayStatProjection.Evaluate(new()
            {
                ClientBuild = CharacterDisplayStatProjection.QualifiedClientBuild, ClassId = "Cleric", Generation = 7,
                NativeStats = new(snapshot.NativeStats.Value!, CombatKnowledgeStatus.Stale, false, 7, At, "own-full-stat-list")
            })
        };
        await using var fixture = new Fixture("en");
        var html = await fixture.Render(snapshot: snapshot);
        foreach (var god in GodValues)
        {
            var row = Row(html, god.Id);
            Assert.Contains("data-display-value=\"\"", row);
            Assert.Contains("data-display-stat-value>—</strong>", row);
            Assert.Contains("data-display-status=\"Stale\"", row);
        }
        var observation = CombatStatObservationPresentation.Create(snapshot);
        if (staleSource == "own") Assert.Null(observation);
        else Assert.All(observation!.ProjectedRows.Where(row => GodValues.Any(god => god.Id == row.Id)), row =>
        {
            Assert.Null(row.Value); Assert.All(row.RawDependencies, dependency => Assert.Null(dependency.RawValue));
        });
    }

    [Fact]
    public async Task OwnSourceChangesRefreshValuesAndStaleOrStoppedSourcesRemoveCurrentNumbers()
    {
        await using var fixture = new Fixture("en");
        fixture.Publish(1200, 1);
        var html = await fixture.Render();
        Assert.Contains("data-display-value=\"1320\"", Row(html, "HPMax"));

        await fixture.Dispatch(() => fixture.Publish(1300, 2));
        html = await fixture.Html();
        Assert.Contains("data-display-value=\"1430\"", Row(html, "HPMax"));

        await fixture.Dispatch(() => fixture.Publish(1300, 3, fresh: false));
        html = await fixture.Html();
        Assert.Contains("data-display-status=\"Stale\"", Row(html, "HPMax"));
        Assert.Contains("data-display-value=\"\"", Row(html, "HPMax"));

        await fixture.Dispatch(() => { fixture.Source.Running = false; fixture.Source.Raise(); });
        html = await fixture.Html();
        Assert.Contains("data-native-combat-score>—", html);
        Assert.Contains("data-display-value=\"\"", Row(html, "HPMax"));
        Assert.Contains("data-workspace-section=\"stat-overview\"", html);
    }

    [Fact]
    public async Task GlobalPetProgressRefreshesWithoutOwnCharacterAndForwardsTheRealPetNavigationCallbacks()
    {
        await using var fixture = new Fixture("en", withPetProgress: true);
        var progress = fixture.PetProgress!;
        var pet = Assert.Single(progress.VerifiedPets, row => row.Genus == "cogni");
        Assert.Equal("cogni", pet.Genus);
        progress.SetLevel(pet.Id, 1, 0);

        var html = await fixture.Render("pets", CharacterCombatSnapshot.Empty);
        Assert.Contains("data-combat-pets", html);
        Assert.Contains("data-pet-completed-stages=\"1\"", PetCategory(html));
        Assert.Contains("data-pet-maximum-stages=\"3\"", PetCategory(html));
        Assert.Contains("data-pet-effect-value=\"10\"", OwnedHp(html));
        Assert.Contains("data-pet-effect-value=\"20\"", MissingHp(html));
        Assert.Null(fixture.Source.CombatSnapshot.Character);

        await fixture.Dispatch(() => progress.SetLevel(pet.Id, 2, 0));
        html = await fixture.Html();
        Assert.Contains("data-pet-completed-stages=\"2\"", PetCategory(html));
        Assert.Contains("data-pet-effect-value=\"20\"", OwnedHp(html));
        Assert.Contains("data-pet-effect-value=\"10\"", MissingHp(html));
        Assert.DoesNotContain("data-source-pet-level", html);

        html = await fixture.SetFaction(Factions.Elyos);
        Assert.Contains("data-pet-completed-stages=\"2\"", PetCategory(html));
        Assert.Contains("data-pet-effect-value=\"20\"", OwnedHp(html));
        html = await fixture.SetFaction(Factions.Asmodian);
        Assert.Contains("data-pet-effect-value=\"20\"", OwnedHp(html));

        fixture.Ui.ShowCombatScoreSection("pets");
        await fixture.InvokePetNavigation();
        Assert.Equal("pets", fixture.Ui.Tab);
        Assert.Null(fixture.Ui.TakePendingPetCategory());
        fixture.Ui.ShowCombatScoreSection("pets");
        await fixture.InvokePetNavigation("cogni");
        Assert.Equal("pets", fixture.Ui.Tab);
        Assert.Equal("cogni", fixture.Ui.TakePendingPetCategory());
        Assert.Null(fixture.Ui.TakePendingPetCategory());
        Assert.Null(fixture.Source.CombatSnapshot.Character);
        Assert.False(File.Exists(fixture.PlanPath));

        static string PetCategory(string markup) => Regex.Match(markup,
            "<article[^>]*data-pet-category=\"cogni\"[^>]*>.*?</article>", RegexOptions.Singleline).Value;
        static string OwnedHp(string markup) => Effect(markup, "Owned");
        static string MissingHp(string markup) => Effect(markup, "Missing");
        static string Effect(string markup, string channel) => Regex.Match(markup,
            "<(?:div|button)[^>]*data-pet-effect-channel=\"" + channel
            + "\"[^>]*data-pet-effect-stat=\"HPMax\\|Points\"[^>]*>", RegexOptions.Singleline).Value;
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task OverviewOmitsTheModelDetailsSectionWhileKeepingIndividualStatEvidence(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(snapshot: DemoSnapshot());

        Assert.Contains("data-stat-overview", html);
        Assert.DoesNotContain("data-combat-calculations-toggle", html);
        Assert.DoesNotContain("cw-advanced", html);
        Assert.DoesNotContain("cw-legacy-details", html);
        Assert.DoesNotContain("data-combat-score-calculator", html);
        Assert.Contains("cw-stat-proof", Row(html, "HPMax"));
        Assert.Contains("data-display-value=\"1350\"", Row(html, "HPMax"));
        Assert.Contains("data-display-qualification=\"NativeDirect\"", Row(html, "HPMax"));
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionWorkspace(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_COMBAT_WORKSPACE_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        await using var fixture = new Fixture(language);
        await fixture.Render(snapshot: DemoSnapshot(new Dictionary<ushort, int>
        {
            [7] = 51, [8] = 63, [9] = 50, [10] = 52, [11] = 76,
            [13] = 76, [14] = 38, [15] = 31, [16] = 25, [17] = 38
        }), faction: Factions.Asmodian);
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css", "daevanion-calculator.css", "daevanion-board.css", "pantheon.css" }
            .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
        var baseUri = new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri;
        Directory.CreateDirectory(folder);
        foreach (var section in new[] { "stat-overview", "gear", "daevanion", "pantheon" })
        {
            await fixture.Choose(section);
            var html = await fixture.Markup();
            Assert.DoesNotContain("workspace-profile", html); Assert.DoesNotContain("FixtureHiddenIdentity", html);
            File.WriteAllText(Path.Combine(folder, "workspace-" + section + "-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<base href=\"" + WebUtility.HtmlEncode(baseUri) + "\"><style>" + css
                + "\nhtml,body{height:auto}.cw-workspace,.dc-calculator{height:auto;overflow:visible}</style></head><body>" + html + "</body></html>");
        }
    }

    private static string[] RowIds(string html) => Regex.Matches(html, "data-display-stat=\"([^\"]+)\"")
        .Select(match => match.Groups[1].Value).ToArray();
    private static string Row(string html, string id)
    {
        var match = Regex.Match(html, "<details\\b[^>]*data-display-stat=\"" + Regex.Escape(id) + "\"[^>]*>.*?</details>", RegexOptions.Singleline);
        Assert.True(match.Success, "Stat row missing: " + id); return match.Value;
    }
    private static string Group(string html, string key)
    {
        var match = Regex.Match(html, "<section\\b[^>]*data-stat-group=\"" + Regex.Escape(key) + "\"[^>]*>.*?</section>", RegexOptions.Singleline);
        Assert.True(match.Success, "Stat group missing: " + key); return match.Value;
    }

    private static CharacterCombatSnapshot DemoSnapshot(IReadOnlyDictionary<ushort, int>? godValues = null)
    {
        var raw = new Dictionary<ushort, int>
        {
            [1] = 60, [2] = 45, [4] = 35, [5] = 40, [6] = 10,
            [193] = 1200, [196] = 1250, [199] = 500, [197] = 2000,
            [317] = 150, [31] = 100, [33] = 140, [19] = 30, [425] = 2500,
            [52] = 200, [307] = 1000, [426] = 1000, [104] = 100, [318] = 40, [427] = 500,
            [116] = 50, [312] = 10, [428] = 500, [128] = 150, [429] = 500, [133] = 125, [430] = 600,
            [282] = 1834, [205] = 475, [284] = 125, [69] = 17, [38] = 8, [41] = 40,
            [98] = 12, [99] = 60, [28] = 725, [70] = 0, [564] = 0, [102] = 160, [147] = 140
        };
        foreach (var value in godValues ?? GodValues.ToDictionary(god => god.NativeId, god => god.Value)) raw[value.Key] = value.Value;
        var display = CharacterDisplayStatProjection.Evaluate(new()
        {
            ClientBuild = 26262663, ClassId = "Cleric", Generation = 7,
            NativeStats = new(raw, CombatKnowledgeStatus.Complete, true, 7, At, "own-full-stat-list"),
            HpMaximum = new(1350, CombatKnowledgeStatus.Complete, true, 7, At, "own-entry-resource-maxima", CharacterDisplayVitalSourceKind.Own),
            MpMaximum = new(600, CombatKnowledgeStatus.Complete, true, 7, At, "own-entry-resource-maxima", CharacterDisplayVitalSourceKind.Own)
        });
        return new()
        {
            ProfileId = "workspace-profile", ClassId = "Cleric", CharacterLevel = 45, IsFresh = true,
            Generation = 7, CurrentCombatScore = 45678, ObservedAt = At, DisplayStats = display,
            NativeStats = new(raw.ToImmutableDictionary(), NetworkCombatSourceStatus.Complete, true, At, 1, "own-full-stat-list"),
            Equipment = new(new CharacterEquipmentState([]), NetworkCombatSourceStatus.Complete, true, At, 1, "synthetic-equipment")
        };
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previousLanguage = UiText.Language;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-workspace-ui-" + Guid.NewGuid().ToString("N"));
        private readonly OwnSkillStateService _skills;
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private readonly CharacterCombatStateService _character;
        private HtmlRootComponent _root;
        private CapturedWorkspace? _view;
        public Source Source { get; } = new();
        public UiState Ui { get; } = new();
        public ProgressService? PetProgress { get; }
        public string PlanPath => Path.Combine(_directory, "plans.json");
        public List<string> ChangedSections { get; } = [];

        public Fixture(string language, bool withPetProgress = false)
        {
            static LootTrackerCharacterSnapshot? Resolve(OwnCharacterContext _) => new("workspace-profile", "Unused");
            _skills = new(Source, Resolve); _character = new(Source, _skills, Resolve);
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(_character);
            services.AddSingleton<INetworkCharacterCombatSource>(Source);
            services.AddSingleton<DaevanionBoardEditService>();
            services.AddSingleton(new DaevanionPlanService(PlanPath, new NoSolve()));
            if (withPetProgress)
            {
                var source = PetIdTable.All.Values.First(pet => pet.Genus == "Cogni" && pet.CatalogId is not null);
                var catalog = new PetCatalog([new() { Id = source.CatalogId!, En = source.En, Genus = "cogni",
                    NumericId = source.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }]);
                PetProgress = new ProgressService(Path.Combine(_directory, "progress.json"), catalog);
                services.AddSingleton(PetProgress);
            }
            _services = services.BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
            UiText.Language = language;
        }
        public Task<string> Render(string section = "stat-overview", CharacterCombatSnapshot? snapshot = null, string? faction = null) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            _root = await _renderer.RenderComponentAsync<CapturedWorkspace>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CombatScoreWorkspaceView.Section)] = section,
                [nameof(CombatScoreWorkspaceView.Faction)] = faction,
                [nameof(CombatScoreWorkspaceView.SectionChanged)] = EventCallback.Factory.Create<string>(this, (Action<string>)ChangedSections.Add),
                [nameof(CombatScoreWorkspaceView.OpenPets)] = EventCallback.Factory.Create(this, () => Ui.Navigate("pets")),
                [nameof(CombatScoreWorkspaceView.OpenPetCategory)] = EventCallback.Factory.Create<string>(this, (Action<string>)Ui.ShowPetCategory),
                [nameof(CapturedWorkspace.InitialSnapshot)] = snapshot,
                [nameof(CapturedWorkspace.Captured)] = (Action<CapturedWorkspace>)(view => _view = view)
            }));
            return WebUtility.HtmlDecode(_root.ToHtmlString());
        });
        public Task<string> SetFaction(string? faction) => _renderer.Dispatcher.InvokeAsync(() =>
        { _view!.SetFaction(faction); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task Dispatch(Action action) => _renderer.Dispatcher.InvokeAsync(action);
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public Task<string> Choose(string section) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Click("data-combat-subtab", section); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task InvokePetNavigation(string? genus = null) => _renderer.Dispatcher.InvokeAsync(() => _view!.InvokePetNavigation(genus));
        public void Publish(int flatHp, long sequence, bool fresh = true)
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
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _services.DisposeAsync(); _character.Dispose(); _skills.Dispose();
            UiText.Language = _previousLanguage;
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    public sealed class Source : INetworkCharacterCombatSource, INetworkSkillSource
    {
        public bool Running { get; set; } = true;
        public NetworkCharacterCombatSnapshot CombatSnapshot { get; set; } = NetworkCharacterCombatSnapshot.Empty;
        public NetworkSkillSnapshot SkillSnapshot { get; set; } = NetworkSkillSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        public void Raise() => Changed?.Invoke();
    }

    public sealed class CapturedWorkspace : CombatScoreWorkspaceView
    {
        [Parameter] public CharacterCombatSnapshot? InitialSnapshot { get; set; }
        [Parameter] public Action<CapturedWorkspace>? Captured { get; set; }
        internal void SetFaction(string? faction) { Faction = faction; OnParametersSet(); StateHasChanged(); }
        protected override void OnInitialized()
        {
            base.OnInitialized();
            if (InitialSnapshot is not null)
                typeof(CombatScoreWorkspaceView).GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, InitialSnapshot);
            Captured?.Invoke(this);
        }
#pragma warning disable BL0006 // Exercise the production button callback, shared by pointer and native keyboard activation.
        public async Task InvokePetNavigation(string? genus)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatScoreWorkspaceView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Component
                && frames.Array[i].ComponentType == typeof(CombatSourceCategoryView));
            var name = genus is null ? nameof(CombatSourceCategoryView.OpenPets) : nameof(CombatSourceCategoryView.OpenPetCategory);
            var callback = Enumerable.Range(index + 1, frames.Array[index].ComponentSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name).AttributeValue;
            if (genus is null) await Assert.IsType<EventCallback>(callback).InvokeAsync();
            else await Assert.IsType<EventCallback<string>>(callback).InvokeAsync(genus);
        }

        public async Task Click(string attribute, string? value = null)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatScoreWorkspaceView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == attribute && (value is null || Equals(frames.Array[i].AttributeValue, value)));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeValue;
            if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
            else if (callback is EventCallback untyped) await untyped.InvokeAsync();
            else if (callback is Action synchronous) synchronous();
            else if (callback is Func<Task> asynchronous) await asynchronous();
            else throw new InvalidOperationException("Unexpected production button callback.");
            StateHasChanged();
        }
#pragma warning restore BL0006
    }

    private sealed class NoSolve : IDaevanionOptimizer
    {
        public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
            IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A workspace render must not start optimization.");
    }
}
