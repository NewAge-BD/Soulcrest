using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>The route workspace keeps character progress, the visible itinerary and the route library distinct.</summary>
[Collection("Settings file")] // writes targets.json, routes.soulroute and settings.json of the test data folder
public sealed class RouteTileRenderTests
{
    [Fact]
    public async Task RouteTileShowsSaveButtonAndSavedRoutes()
    {
        File.Delete(AppPaths.TargetsFile);
        File.Delete(AppPaths.RoutesFile);
        var progressFile = Path.Combine(AppPaths.DataDirectory, "leveling-progress.json");
        var previousProgress = File.Exists(progressFile) ? File.ReadAllBytes(progressFile) : null;
        File.Delete(progressFile);
        var characterFile = Path.Combine(AppPaths.DataDirectory, "route-tile-characters.json");
        File.Delete(characterFile);
        RouteFile.Save(AppPaths.RoutesFile, new[] { new SavedRoute("leveling", "Asmodian · Episode 2 · Level 10–16",
            Enumerable.Range(1, 10).Select(i => new RouteStop("altgard", i * 100, 200,
                i == 5 ? "Finding Nemon" : i == 6 ? "" : $"Quest {i}",
                i == 4 ? "Kibelisk" : i == 6 ? "Sealed Dungeon" : i == 7 ? "NPCs · Regional Quest" : "NPCs · Hero Quest", null)).ToList(),
            Category: LevelingRouteStyle.Category),
            new SavedRoute("elyos", "Elyos · Episode 2 · Level 10–16",
                [new("verteron", 100, 100, "Quest A", "NPCs · Hero Quest", null),
                 new("verteron", 200, 200, "Quest B", "NPCs · Hero Quest", null)], Category: LevelingRouteStyle.Category),
            new SavedRoute("unassigned", "Custom leveling practice",
                [new("altgard", 50, 50, "Practice", "Locations", null)], Category: LevelingRouteStyle.Category) });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<GameCaptureService>();
        services.AddSingleton<ProgressService>();
        services.AddSingleton<NetworkLootService>();
        services.AddSingleton<TrackerService>();
        services.AddSingleton<MapTrackingService>();
        services.AddSingleton<MapTargetsService>();
        services.AddSingleton<BossRushService>();
            services.AddSingleton(sp => new AlertsService(characterFile + ".alerts.json", runTimer: false));
        services.AddSingleton(new ExplorationService(characterFile, []));
        services.AddSingleton<UiState>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SettingsService>();
        var previous = (settings.Current.UiLanguage, settings.Current.LastMap);
        settings.Update(s => { s.UiLanguage = "de"; s.LastMap = "altgard"; });
        UiText.Language = "de";
        var targets = provider.GetRequiredService<MapTargetsService>();
        var exploration = provider.GetRequiredService<ExplorationService>();
        exploration.Rename("Route Tester A");
        var firstCharacter = exploration.ActiveId;
        try
        {
            targets.Toggle("altgard", 100, 100, "A", "Locations", null);
            targets.Toggle("altgard", 200, 200, "B", "Locations", null, chain: true);
            var before = await Render(provider);
            Assert.Contains("Folge speichern (2)", before);
            Assert.Contains("Ankunftsradius (Kartenpixel)", before);
            Assert.DoesNotContain("Abschlussradius", before);
            Assert.DoesNotContain("Strongholds genügt der Radius", before);
            Assert.DoesNotContain("Ziel als fertig markieren", before);

            var route = targets.SaveRoute("altgard", "Runde")!;
            targets.SetRouteRepeat(route.Id, true);
            var after = await Render(provider);
            Assert.Contains("Runde", after);
            Assert.Contains("2 Ziele", after);
            Assert.Contains("läuft", after);
            Assert.Contains("route-repeat on", after);
            Assert.Contains("route-character-profile", after);
            Assert.Contains("Routen suchen", after);
            Assert.Contains("route-card", after);
            Assert.Equal(2, Regex.Matches(after, "class=\"route-faction-group\"").Count);
            Assert.DoesNotContain("open", FactionTag(after, "Asmodian"));
            Assert.DoesNotContain("open", FactionTag(after, "Elyos"));
            Assert.Contains("Weitere Leveling-Routen", after);
            Assert.Contains("Custom leveling practice", after);

            targets.StartRoute("leveling");
            for (var i = 0; i < 4; i++) targets.AdvanceRoute("leveling");
            var leveling = await Render(provider);
            Assert.Contains("Leveling Routes", leveling);
            Assert.Contains("Eigene Routen", leveling);
            Assert.Contains("objective-colors", leveling);
            Assert.Contains("Nächstes Ziel", leveling);
            Assert.Contains("Vorheriges Ziel", leveling);
            Assert.Contains("Station 5 / 10", leveling);
            Assert.Contains("leveling-navigation", leveling);
            Assert.Contains("Finding Nemon", leveling);
            Assert.Contains("Station 6", leveling); // unnamed map waypoints keep a useful list position
            Assert.Contains("aria-valuenow=\"4\"", leveling);
            Assert.Contains("aria-current=\"step\"", leveling);
            Assert.Contains("Fortschritt für diesen Charakter gespeichert", leveling);
            Assert.Contains("class=\"btn route-cancel\"", leveling);
            Assert.Contains("Route abbrechen", leveling);
            Assert.Contains("Route beenden und Fortschritt behalten", leveling);
            Assert.Contains("Leveling-Overlay", leveling);
            Assert.Contains("data-overlay-link=\"leveling\"", leveling);
            Assert.DoesNotContain("Größe des Leveling-Overlays", leveling);
            Assert.Contains("open", FactionTag(leveling, "Asmodian"));
            Assert.DoesNotContain("open", FactionTag(leveling, "Elyos"));
            Assert.DoesNotContain("name=", FactionTag(leveling, "Asmodian")); // Native details remain independent.
            Assert.DoesNotContain("name=", FactionTag(leveling, "Elyos"));
            Assert.Contains("<span class=\"route-faction-percent\">40 %</span>", leveling);
            Assert.Contains("Am Ende startet die nächste Episode automatisch.", leveling);
            Assert.DoesNotContain("class=\"route-repeat-option\"", leveling);
            Assert.Equal(3, Regex.Matches(leveling, "class=\"btn small route-repeat ").Count); // Running farm, ordinary library and custom leveling routes retain Repeat.
            Assert.DoesNotContain("route-overlay-controls", leveling);
            Assert.DoesNotContain("Auf der Weltkarte im Spiel: Alt + Rechtsklick", leveling);
            Assert.Contains("Leveling-Overlay einstellen", leveling);
            Assert.DoesNotContain("Strg+Alt+L", leveling);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(leveling, "class=\"route-stop completed ").Count);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(leveling, "class=\"route-stop upcoming ").Count);
            Assert.Contains("class=\"route-target-name\">A</span>", leveling); // manually marked targets are retained beside the itinerary
            Assert.Contains("class=\"route-target-name\">B</span>", leveling);
            WritePreview("routes.html", leveling);
            var matchingFaction = await Render(provider, "Elyos");
            Assert.Contains("open", FactionTag(matchingFaction, "Elyos"));
            Assert.DoesNotContain("data-faction=\"Asmodian\"", matchingFaction);
            Assert.DoesNotContain("Weitere Leveling-Routen", matchingFaction);
            var matchingBoth = await Render(provider, "Level");
            Assert.Contains("open", FactionTag(matchingBoth, "Asmodian"));
            Assert.Contains("open", FactionTag(matchingBoth, "Elyos"));
            WritePreview("routes-search.html", matchingBoth);
            UiText.Language = "en";
            var english = await Render(provider);
            Assert.Contains("Progress saved for this character", english);
            Assert.Contains("Stop 5 / 10", english);
            Assert.Contains("Back", english);
            Assert.Contains("Cancel route", english);
            Assert.Contains("Configure leveling overlay", english);
            Assert.Contains("data-overlay-link=\"leveling\"", english);
            Assert.DoesNotContain("Click the overlay lock to interact and move it.", english);
            Assert.DoesNotContain("On the in-game world map: Alt + right-click", english);
            Assert.Contains("Other leveling routes", english);
            Assert.Contains("The next episode starts automatically at the end.", english);
            Assert.Equal(2, Regex.Matches(english, "class=\"route-faction-group\"").Count);
            WritePreview("routes-en.html", english);
            UiText.Language = "de";

            targets.StopRoute("leveling");
            var inactive = await Render(provider);
            Assert.DoesNotContain("class=\"route-active\"", inactive);
            Assert.DoesNotContain("class=\"btn route-cancel\"", inactive);
            Assert.Contains("class=\"route-target-name\">A</span>", inactive);
            Assert.Contains("4 / 10 erledigt", inactive);
            Assert.Contains("Fortsetzen", inactive);
            WritePreview("routes-library.html", inactive);
            targets.StartRoute("leveling");
            Assert.Contains("Station 5 / 10", await Render(provider));

            exploration.Add("Route Tester B");
            targets.StartRoute("leveling");
            var otherCharacter = await Render(provider);
            Assert.Contains("Station 1 / 10", otherCharacter);
            Assert.Contains("aria-valuenow=\"0\"", otherCharacter);
            exploration.Select(firstCharacter);
            Assert.Contains("Station 5 / 10", await Render(provider));
            targets.RewindRoute("leveling");
            var rewound = await Render(provider);
            Assert.Contains("Station 4 / 10", rewound);
            for (var i = 0; i < 10; i++) targets.AdvanceRoute("leveling");
            var finished = await Render(provider);
            Assert.Contains("Route abgeschlossen", finished);
            Assert.Contains("Vorheriges Ziel", finished);
        }
        finally
        {
            settings.Update(s => { s.UiLanguage = previous.UiLanguage; s.LastMap = previous.LastMap; });
            UiText.Language = previous.UiLanguage;
            File.Delete(AppPaths.TargetsFile);
            File.Delete(AppPaths.RoutesFile);
            File.Delete(characterFile);
            if (previousProgress is null) File.Delete(progressFile);
            else File.WriteAllBytes(progressFile, previousProgress);
        }
    }

    [Theory]
    [InlineData("de", 0)]
    [InlineData("de", 2)]
    [InlineData("de", 60)]
    [InlineData("en", 0)]
    [InlineData("en", 2)]
    [InlineData("en", 60)]
    public async Task MarkedTargetsShowOnlyThreeBeforeNativeDisclosureWithoutChangingTheirState(string language, int count)
    {
        await using var fixture = new RouteScenario(language);
        fixture.MarkTargets(count);
        var state = fixture.State();
        var files = fixture.StoredFiles();
        var html = await fixture.Render();
        var marked = ElementByClass(html, "section", "route-marked");
        Assert.Equal(count, MarkedIds(marked).Length);
        Assert.Equal(fixture.Targets.VisibleTargets.Where(target => !target.Leveling).Select(target => target.Id), MarkedIds(marked));
        if (count == 0)
        {
            Assert.DoesNotContain("route-marked-preview", marked);
            Assert.DoesNotContain("route-marked-more", marked);
            Assert.Contains(language == "de" ? "Route aus der Bibliothek starten" : "Start a route from the library", marked);
        }
        else
        {
            var preview = ElementByClass(marked, "div", "route-marked-preview");
            Assert.Equal(Math.Min(3, count), MarkedIds(preview).Length);
            Assert.Matches("<button[^>]*class=\"name\"", preview);
            Assert.Contains("Altgard", marked);
            Assert.Contains("Verteron", marked);
            Assert.Contains("class=\"remove\"", marked);
            if (count <= 3) Assert.DoesNotContain("route-marked-more", marked);
            else
            {
                var more = ElementByClass(marked, "details", "route-marked-more");
                var opening = more[..(more.IndexOf('>') + 1)];
                Assert.DoesNotMatch(@"\bopen(?:\s|=|>)", opening);
                Assert.DoesNotContain("name=", opening);
                Assert.Contains(language == "de" ? "Weitere 57 Ziele" : "57 more targets", more);
                Assert.Contains(language == "de" ? "Weniger anzeigen" : "Show fewer", more);
                var overflow = ElementByClass(more, "div", "route-marked-overflow");
                Assert.Equal(57, MarkedIds(overflow).Length);
                Assert.Contains("tabindex=\"0\"", overflow);
                Assert.Contains("role=\"region\"", overflow);
                Assert.Contains("<span class=\"route-target-index\"", overflow);
                Assert.Matches(">60</span>", overflow);
                Assert.DoesNotContain("route-save", more);
                Assert.DoesNotContain(language == "de" ? "Alle entfernen" : "Remove all", more);
            }
        }
        Assert.Equal(state, fixture.State());
        Assert.Equal(files, fixture.StoredFiles());
        await fixture.Export($"my-routes-marked-{count}");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task LibraryRepeatIsDirectlyAvailableForOrdinaryAndCustomRoutesButNotEpisodes(string language)
    {
        await using var fixture = new RouteScenario(language);
        var state = fixture.State();
        var html = await fixture.Render();
        foreach (var id in new[] { "synthetic-farm", "synthetic-custom" })
        {
            var card = ElementByAttribute(html, "article", "data-route-card", id);
            var actions = ElementByClass(card, "div", "route-card-actions");
            Assert.True(actions.IndexOf("data-route-repeat=", StringComparison.Ordinal) < actions.IndexOf("<details", StringComparison.Ordinal));
            Assert.Contains("aria-pressed=\"false\"", RepeatButton(actions, id));
            Assert.Contains(language == "de" ? "aria-label=\"Wiederholung für " : "aria-label=\"Repeat ", actions);
            Assert.DoesNotContain("data-route-repeat", ElementByClass(actions, "details", "route-card-menu"));
        }
        Assert.DoesNotContain("data-route-repeat", ElementByAttribute(html, "article", "data-route-card", "synthetic-episode"));
        Assert.Equal(state, fixture.State());
    }

    [Theory]
    [InlineData("de", false)]
    [InlineData("de", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task RunningFarmRepeatIsAboveTargetsAndAnOlderClickStillTogglesTheCurrentPreference(string language, bool repeat)
    {
        await using var fixture = new RouteScenario(language, longFarm: true);
        const string id = "synthetic-farm";
        fixture.Targets.SetRouteRepeat(id, repeat);
        fixture.Targets.StartRoute(id);
        var marks = System.Text.Json.JsonSerializer.Serialize(fixture.Targets.Targets);
        var settings = System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current);
        var html = await fixture.Render();
        var marked = ElementByClass(html, "section", "route-marked");
        var running = ElementByAttribute(marked, "div", "data-running-route", id);
        Assert.Contains(fixture.FarmName, running);
        Assert.True(marked.IndexOf("data-running-route=", StringComparison.Ordinal) < marked.IndexOf("route-marked-preview", StringComparison.Ordinal));
        Assert.Contains($"aria-pressed=\"{repeat.ToString().ToLowerInvariant()}\"", RepeatButton(running, id));
        Assert.Equal(2, Regex.Matches(html, "data-route-repeat=\"synthetic-farm\"").Count);
        await fixture.Export("my-routes-active-farm-repeat-" + (repeat ? "on" : "off"));
        if (repeat) await fixture.Export("my-routes-active-farm-long");
        var callback = await fixture.RepeatCallback(id);
        await fixture.Click(callback);
        Assert.Equal(!repeat, fixture.Targets.Routes.Single(route => route.Id == id).Repeat);
        await fixture.Click(callback); // The old render must not keep writing its captured Repeat value.
        Assert.Equal(repeat, fixture.Targets.Routes.Single(route => route.Id == id).Repeat);
        Assert.Equal(marks, System.Text.Json.JsonSerializer.Serialize(fixture.Targets.Targets));
        Assert.Equal(settings, System.Text.Json.JsonSerializer.Serialize(fixture.Settings.Current));
    }

    [Theory]
    [InlineData("de", false)]
    [InlineData("de", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task ActiveLevelingKeepsManualMarksAndCustomRepeatOutsideItsOptions(string language, bool episode)
    {
        await using var fixture = new RouteScenario(language);
        var id = episode ? "synthetic-episode" : "synthetic-custom";
        fixture.Targets.StartRoute(id);
        fixture.Targets.AdvanceRoute(id);
        fixture.MarkTargets(60);
        var state = fixture.State();
        var files = fixture.StoredFiles();
        var html = await fixture.Render();
        var active = ElementByClass(html, "section", "route-active");
        if (episode) Assert.DoesNotContain("data-route-repeat", active);
        else
        {
            Assert.True(active.IndexOf("data-route-repeat=", StringComparison.Ordinal) < active.IndexOf("route-active-options", StringComparison.Ordinal));
            Assert.Contains("aria-pressed=\"false\"", RepeatButton(active, id));
        }
        Assert.DoesNotContain("data-route-repeat", ElementByClass(active, "details", "route-active-options"));
        var marked = ElementByClass(html, "section", "route-marked");
        Assert.Equal(60, MarkedIds(marked).Length);
        Assert.Equal(3, MarkedIds(ElementByClass(marked, "div", "route-marked-preview")).Length);
        Assert.Equal(57, MarkedIds(ElementByClass(marked, "div", "route-marked-overflow")).Length);
        Assert.DoesNotContain("data-running-route", marked); // The leveling itinerary has its own panel.
        Assert.Equal(state, fixture.State());
        Assert.Equal(files, fixture.StoredFiles());
        await fixture.Export("my-routes-active-leveling-" + (episode ? "episode" : "custom") + "-marks");
    }

    private static string[] MarkedIds(string html) => Regex.Matches(html, "data-marked-target=\"([^\"]+)\"")
        .Select(match => match.Groups[1].Value).ToArray();

    private static string RepeatButton(string html, string id)
    {
        var button = Regex.Match(html, "<button\\b[^>]*data-route-repeat=\"" + Regex.Escape(id) + "\"[^>]*>[\\s\\S]*?</button>");
        Assert.True(button.Success, $"Missing repeat button for {id}.");
        return button.Value;
    }

    private static string ElementByClass(string html, string tag, string className) => Element(html, tag,
        opening => Regex.Match(opening, "\\bclass=\"([^\"]*)\"").Groups[1].Value.Split(' ').Contains(className));
    private static string ElementByAttribute(string html, string tag, string attribute, string value) => Element(html, tag,
        opening => Regex.IsMatch(opening, "\\b" + Regex.Escape(attribute) + "=\"" + Regex.Escape(value) + "\""));
    private static string Element(string html, string tag, Func<string, bool> matches)
    {
        var opening = Regex.Matches(html, "<" + tag + "\\b[^>]*>").FirstOrDefault(match => matches(match.Value));
        Assert.NotNull(opening);
        var depth = 0;
        foreach (Match token in Regex.Matches(html[opening.Index..], "</?" + tag + "\\b[^>]*>"))
        {
            depth += token.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0) return html.Substring(opening.Index, token.Index + token.Length);
        }
        throw new InvalidOperationException("Unclosed HTML element: " + tag);
    }

    private sealed class RouteScenario : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-routes-view-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly HtmlRenderer _renderer;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        private MapView? _view;
        private readonly string _language;
        public ServiceProvider Provider { get; }
        public SettingsService Settings => Provider.GetRequiredService<SettingsService>();
        public MapTargetsService Targets => Provider.GetRequiredService<MapTargetsService>();
        public string FarmName => _language == "de" ? "Synthetische Farmrunde mit besonders langem Namen durch Altgard und Verteron" : "Synthetic farming circuit with a particularly long name through Altgard and Verteron";

        public RouteScenario(string language, bool longFarm = false)
        {
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            _language = language;
            Directory.CreateDirectory(_folder);
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.TargetsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile,
                AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile, Path.Combine(AppPaths.DataDirectory, "routes.seeded.json"),
                Path.Combine(AppPaths.DataDirectory, "leveling-progress.json") })
            {
                _backups[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (File.Exists(path)) File.Delete(path);
            }
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian"},{"id":"verteron","label":"Verteron","group":"Elyos"}]}""");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            RouteFile.Save(AppPaths.RoutesFile,
            [
                new SavedRoute("synthetic-farm", FarmName, Stops("Synthetic farming target with a long descriptive waypoint name", "Locations", longFarm ? 60 : 6), Color: "#38bdf8"),
                new SavedRoute("synthetic-custom", "Synthetic custom leveling practice", Stops("Synthetic custom objective", "NPCs · Hero Quest"), Category: LevelingRouteStyle.Category),
                new SavedRoute("synthetic-episode", "Asmodian · Episode 2 · Level 10–16", Stops("Synthetic episode objective", "NPCs · Hero Quest"), Category: LevelingRouteStyle.Category)
            ]);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton(new ProgressService(Path.Combine(_folder, "progress.json"), new Soulcrest.Core.Pets.PetCatalog([])));
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton(sp => new TrackerService(sp.GetRequiredService<SettingsService>(), network: null));
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton(new ExplorationService(Path.Combine(_folder, "exploration.json"), []));
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton<UiState>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton(_ => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false));
            services.AddSingleton<IJSRuntime, NoJs>();
            Provider = services.BuildServiceProvider();
            Settings.Update(settings => { settings.UiLanguage = settings.NameLanguage = language; settings.LastMap = "altgard"; settings.FollowPlayer = false; settings.ProgressionEnabled = false; });
            Provider.GetRequiredService<ExplorationService>().Rename("Synthetic route profile");
            _ = Targets;
            _renderer = new HtmlRenderer(Provider, Provider.GetRequiredService<ILoggerFactory>());
        }

        private static RouteStop[] Stops(string name, string kind, int count = 6) => Enumerable.Range(1, count)
            .Select(index => new RouteStop(index % 2 == 1 ? "altgard" : "verteron", 100 + index * 25, 100 + index * 30,
                name + " " + index, kind, null)).ToArray();
        public void MarkTargets(int count)
        {
            for (var index = 0; index < count; index++)
                Targets.Toggle(index % 2 == 0 ? "altgard" : "verteron", 1000 + index * 20, 2000 + index * 20,
                    (_language == "de" ? "Synthetisches markiertes Ziel " : "Synthetic marked target ") + (index + 1).ToString("00") +
                    " · LongTargetNameWithoutSpacesForLayoutVerification · additional descriptive location text", "Locations", null, chain: true);
        }
        public string State() => System.Text.Json.JsonSerializer.Serialize(new { Targets.Targets, Targets.Routes, Settings = Settings.Current,
            Progress = Provider.GetRequiredService<ExplorationService>().Completed.Order().ToArray() });
        public string StoredFiles() => System.Text.Json.JsonSerializer.Serialize(_backups.Keys.ToDictionary(path => path,
            path => File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null));
        public async Task<string> Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<RouteQueryHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(RouteQueryHost.Capture)] = (Action<MapView>)(view => _view = view) })));
            return await Html();
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => System.Net.WebUtility.HtmlDecode(_root.ToHtmlString()));
        // Extract only real rendered callbacks; no UI logic is reproduced in the fixture.
#pragma warning disable BL0006
        public Task<object?> RepeatCallback(string id) => _renderer.Dispatcher.InvokeAsync<object?>(() =>
        {
            using var tree = new RenderTreeBuilder();
            typeof(MapView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, [tree]);
            var frames = tree.GetFrames();
            var attribute = Enumerable.Range(0, frames.Count).First(index => frames.Array[index].FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute
                && frames.Array[index].AttributeName == "data-route-repeat" && Equals(frames.Array[index].AttributeValue, id));
            var parent = attribute;
            while (frames.Array[parent].FrameType != Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Element) parent--;
            var end = parent + frames.Array[parent].ElementSubtreeLength;
            return Enumerable.Range(parent + 1, end - parent - 1).Select(index => frames.Array[index])
                .First(frame => frame.FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeValue;
        });
#pragma warning restore BL0006
        public Task Click(object? callback) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            switch (callback)
            {
                case EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed: await typed.InvokeAsync(new()); break;
                case EventCallback untyped: await untyped.InvokeAsync(); break;
                case MulticastDelegate handler: if (handler.DynamicInvoke() is Task task) await task; break;
                default: throw new InvalidOperationException("Unexpected route click callback.");
            }
            await _view!.SetParametersAsync(ParameterView.Empty);
        });
        public async Task Export(string state)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_ROUTE_UI_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            var raw = await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
            await File.WriteAllTextAsync(Path.Combine(output, state + "-" + _language + ".html"),
                "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/map-legend.css\"><link rel=\"stylesheet\" href=\"/css/monster-search.css\"><style>html,body{height:100%;margin:0}.mapview{height:100vh}#map{background:linear-gradient(135deg,#1b302c,#253c32)}</style></head><body>" + raw + "</body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await Provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
            {
                if (bytes is null) { if (File.Exists(path)) File.Delete(path); }
                else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
            }
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, true);
        }
    }


    private static void WritePreview(string filename, string markup)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_ROUTE_UI_PREVIEW") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, filename), "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"/css/app.css\"></head><body>" + markup + "</body></html>");
    }

    private static string FactionTag(string html, string faction)
    {
        var match = Regex.Match(html, "<details\\b[^>]*class=\"route-faction-group\"[^>]*data-faction=\"" + faction + "\"[^>]*>");
        Assert.True(match.Success, $"Missing faction group {faction}.");
        return match.Value;
    }

    private static async Task<string> Render(ServiceProvider provider, string? query = null)
    {
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (query is null) return (await renderer.RenderComponentAsync<MapView>()).ToHtmlString();
            MapView? view = null;
            var root = await renderer.RenderComponentAsync<RouteQueryHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(RouteQueryHost.Capture)] = (Action<MapView>)(value => view = value) }));
            Assert.NotNull(view);
            typeof(MapView).GetField("_routeQuery", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, query);
            await view!.SetParametersAsync(ParameterView.Empty);
            return root.ToHtmlString();
        });
        return System.Net.WebUtility.HtmlDecode(html);
    }

    private sealed class RouteQueryHost : ComponentBase
    {
        [Parameter] public Action<MapView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MapView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((MapView)value));
            builder.CloseComponent();
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
