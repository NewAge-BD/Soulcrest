using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class PetsViewIdentityTests
{
    private const string PatchUrl = "https://steamcommunity.com/games/3393110/announcements/detail/675136702174461957";

    [Fact]
    public async Task TheSourcedGatekeeperPinopiHasItsSoulIdAndNoGenericGatekeeperRow()
    {
        var pinopi = Sourced(1146);
        var gatekeeper = new PetDefinition { Id = "gatekeeper", En = "Gatekeeper", Genus = "unknown" };
        await using var fixture = new Fixture([pinopi], learned: [gatekeeper]);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Equal("Gatekeeper Pinopi", PetIdTable.Find(1146)!.En);
        Assert.Contains("data-pet-id=\"gatekeeper-pinopi\"", html);
        Assert.DoesNotContain("data-pet-id=\"gatekeeper\"", html);
        Assert.Contains("data-pet-soul-id=\"1146\"", html);
        Assert.Contains("Soul-ID: 1146", html);
        Assert.Contains("Nur Pets mit belegter Soul-ID.", html);
        Assert.Single(fixture.Progress.VerifiedPets);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Fact]
    public async Task UnverifiedLearnedPetsCannotEnterTheListOrTheSelectedDetail()
    {
        var unverified = new PetDefinition
            { Id = "synthetic-unverified-companion", En = "Synthetic Unverified Companion", Genus = "unknown" };
        await using var fixture = new Fixture([Sourced(1146)], learned: [unverified]);
        Assert.NotNull(fixture.Progress.Catalog.Find(unverified.Id));
        Assert.Null(fixture.Progress.SoulId(unverified.Id));
        var before = fixture.ProfileBytes;

        await fixture.Render();
        await fixture.SelectUnverified(unverified.Id);
        var html = await fixture.Html();

        Assert.DoesNotContain(unverified.En, html);
        Assert.DoesNotContain("data-pet-id=\"" + unverified.Id + "\"", html);
        Assert.Contains("Gatekeeper Pinopi", html);
        Assert.Contains("data-pet-soul-id=\"1146\"", html);
        Assert.Equal(1, fixture.Count("_total"));
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Fact]
    public async Task VerifiedSoulIdentitiesAreCountedOnceAndUnverifiedFocusAndMaxValuesStayExcluded()
    {
        var primary = Sourced(1098);
        var legacy = primary with { Id = "kalnif-02", LegacyDuplicate = true };
        var unverified = new PetDefinition
            { Id = "synthetic-unverified-map-pet", En = "Synthetic Unverified Map Pet", Genus = "unknown" };
        var profile = new ProgressProfile();
        profile.Pets[primary.Id] = new() { Souls = 40 };
        profile.Pets[legacy.Id] = new() { Souls = 105, Focus = true };
        profile.Pets[unverified.Id] = new() { Souls = 105, Focus = true };
        await using var fixture = new Fixture([primary, legacy, Sourced(1146), unverified], profile);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Equal(2, Regex.Matches(html, "class=\"pets-card pets-g-").Count);
        Assert.Contains("data-pet-id=\"modified-karnif\"", html);
        Assert.DoesNotContain("data-pet-id=\"kalnif-02\"", html);
        Assert.DoesNotContain(unverified.En, html);
        Assert.Equal(2, fixture.Count("_total"));
        Assert.Equal(1, fixture.Count("_unlocked"));
        Assert.Equal(1, fixture.Count("_done"));
        Assert.Equal(1, fixture.Count("_focusCount"));
        Assert.Equal((1, 1), fixture.GenusCounts["fera"]);
        Assert.Equal((1, 0), fixture.GenusCounts["varian"]);
        Assert.DoesNotContain("unknown", fixture.GenusCounts.Keys);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData(0, 5, 5)]
    [InlineData(1, 100, 25)]
    [InlineData(2, 75, 75)]
    public async Task AvailableSoulsDoNotPromoteTheAcquiredLevelInThePetView(int level, int balance, int needed)
    {
        var pet = Sourced(1146);
        await using var fixture = new Fixture([pet]);
        var at = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var context = new NetworkProgressContext(new(101, 501, 1001, "Synthetic Alpha"), 1, 1, at);
        Assert.True(fixture.Progress.BindNetworkContext(context));
        Assert.True(fixture.Progress.ApplyNetworkSnapshot(new PetProgressSnapshot(
            new Dictionary<int, int> { [1146] = level }, new Dictionary<int, int> { [1146] = balance }),
            context with { Sequence = 2, At = at.AddSeconds(1) }));
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Equal(level == 0 ? 0 : 1, fixture.Count("_unlocked"));
        Assert.Equal(0, fixture.Count("_done"));
        Assert.Contains($"class=\"pets-count\">{balance}/{needed}</span>", html);
        Assert.DoesNotContain("class=\"pets-count is-max\"", html);
        Assert.Contains($"Verfügbare Souls: {balance}", html);
        if (level > 0) Assert.Contains($"class=\"pets-badge\">{level}</span>", html);
        else Assert.DoesNotContain("class=\"pets-badge\"", html);
        Assert.DoesNotContain("width:400%", html);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de", "Netzwerkstand übernehmen", "Manuelle Korrektur aktiv.")]
    [InlineData("en", "Use network progress", "Manual correction active.")]
    public async Task AManualCorrectionCanReturnToObservedNetworkProgress(string language, string action, string notice)
    {
        var pet = Sourced(1146);
        await using var fixture = new Fixture([pet], language: language);
        var at = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var context = new NetworkProgressContext(new(101, 501, 1001, "Synthetic Alpha"), 1, 1, at);
        Assert.True(fixture.Progress.BindNetworkContext(context));
        Assert.True(fixture.Progress.ApplyNetworkSnapshot(new PetProgressSnapshot(
            new Dictionary<int, int> { [1146] = 1 }, new Dictionary<int, int> { [1146] = 3 }),
            context with { Sequence = 2, At = at.AddSeconds(1) }));
        fixture.Progress.SetSouls(pet.Id, fixture.Progress.Thresholds.Max);
        var html = await fixture.Render();
        Assert.Contains(action, html);
        Assert.Contains(notice, html);
        Assert.Equal(1, fixture.Count("_done"));

        await fixture.UseNetworkProgress(pet.Id);
        html = await fixture.Html();

        Assert.False(fixture.Progress.EntrySnapshot(pet.Id).ManualOverride);
        Assert.DoesNotContain(action, html);
        Assert.DoesNotContain(notice, html);
        Assert.Contains("class=\"pets-count\">3/25</span>", html);
        Assert.Contains("class=\"pets-badge\">1</span>", html);
        Assert.Equal(0, fixture.Count("_done"));
    }

    [Theory]
    [InlineData("de", "Soul-Quellen", "Insel des Blauatems", "Dropchance nicht bekannt.")]
    [InlineData("en", "Soul sources", "Azure Breath Island", "Drop rate unknown.")]
    public async Task DungeonSoulSourcesAreVisibleWithoutInventingMapPositionsOrChangingProgress(
        string language, string heading, string dungeon, string chanceNote)
    {
        var pet = Sourced(1009);
        await using var fixture = new Fixture([pet], language: language);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Contains("data-pet-acquisition=\"1009\"", html);
        Assert.Contains("data-pet-dungeon-source=\"true\"", html);
        Assert.Contains(heading, html);
        Assert.Contains(dungeon, html);
        Assert.Contains(chanceNote, html);
        Assert.Empty(fixture.Progress.Catalog.Find(pet.Id)!.Maps);
        AssertMapAction(html, enabled: false);
        Assert.Equal(before, fixture.ProfileBytes);
        Assert.Equal(0, fixture.Progress.Souls(pet.Id));
    }

    [Theory]
    [InlineData("de", 1009, "Dungeon · zukünftig", "Zukünftige Varianten · noch nicht freigeschaltet")]
    [InlineData("en", 1009, "Dungeon · upcoming", "Future variants · not released yet")]
    [InlineData("de", 1043, "Dungeon · zukünftig", "Zukünftige Varianten · noch nicht freigeschaltet")]
    [InlineData("en", 1080, "Dungeon · upcoming", "Future variants · not released yet")]
    public async Task FutureSourcesRemainVisibleWithoutBeingAdvertisedAsCurrentGlobalFarmLocations(
        string language, int number, string badge, string explanation)
    {
        await using var fixture = new Fixture([Sourced(number)], language: language);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Contains("data-pet-global-availability=\"upcoming\"", html);
        Assert.Contains(badge, html);
        Assert.Contains(explanation, html);
        Assert.Contains("data-pet-source-status=\"upcoming\"", html);
        Assert.DoesNotContain("data-pet-source-status=\"available\"", html);
        Assert.DoesNotContain("data-pet-source-status=\"unconfirmed\"", html);
        Assert.Contains("2026-11-05", html);
        Assert.Contains(PatchUrl, html);
        AssertMapAction(html, enabled: false);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de", "Auf Global verfügbare Varianten", "Zukünftige Varianten · noch nicht freigeschaltet")]
    [InlineData("en", "Variants available on Global", "Future variants · not released yet")]
    public async Task AnOpenDungeonDoesNotPromoteItsFutureVariantToAvailable(
        string language, string currentLabel, string futureLabel)
    {
        await using var fixture = new Fixture([Sourced(1082)], language: language);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.Contains("data-pet-global-availability=\"available\"", html);
        Assert.Contains("data-pet-source-status=\"available\"", html);
        Assert.Contains("data-pet-source-status=\"upcoming\"", html);
        Assert.Contains(currentLabel, html);
        Assert.Contains(futureLabel, html);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de", "Azure Breath")]
    [InlineData("en", "Insel des Blauatems")]
    public async Task SourceDungeonSearchFindsPetsInEitherOriginalLanguage(string language, string query)
    {
        var kernon = Sourced(1009);
        var pinopi = Sourced(1146);
        await using var fixture = new Fixture([kernon, pinopi], language: language);
        var before = fixture.ProfileBytes;
        await fixture.Render();

        await fixture.SetState("_query", query);
        var html = await fixture.Html();

        Assert.Contains($"data-pet-id=\"{kernon.Id}\"", html);
        Assert.DoesNotContain($"data-pet-id=\"{pinopi.Id}\"", html);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de", "Keneire", 1009)]
    [InlineData("en", "Keneire", 1009)]
    [InlineData("de", "Air Raiders", 1080)]
    [InlineData("en", "Luftangriffstruppwaffe", 1080)]
    public async Task SourceMonsterSearchUsesTheProvenDropperRatherThanThePetName(
        string language, string query, int petNumber)
    {
        var pet = Sourced(petNumber);
        var pinopi = Sourced(1146);
        await using var fixture = new Fixture([pet, pinopi], language: language);
        var before = fixture.ProfileBytes;
        await fixture.Render();

        await fixture.SetState("_query", query);
        var html = await fixture.Html();

        Assert.DoesNotContain(query, pet.En, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"data-pet-id=\"{pet.Id}\"", html);
        Assert.DoesNotContain($"data-pet-id=\"{pinopi.Id}\"", html);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Fact]
    public async Task LegacyPetAliasesShowOneAcquisitionPanelAndOldPackagesResolveTheSoulIdentity()
    {
        var primary = Sourced(1009);
        var legacy = primary with { Id = "synthetic-legacy-kernon", LegacyDuplicate = true };
        await using (var fixture = new Fixture([primary, legacy]))
        {
            var before = fixture.ProfileBytes;
            var html = await fixture.Render();
            Assert.Single(fixture.Progress.VerifiedPets);
            Assert.Single(Regex.Matches(html, "data-pet-acquisition=\"1009\"").Cast<Match>());
            Assert.DoesNotContain($"data-pet-id=\"{legacy.Id}\"", html);
            Assert.Equal(before, fixture.ProfileBytes);
        }

        await using (var fixture = new Fixture([primary with { NumericId = null }]))
        {
            var before = fixture.ProfileBytes;
            var html = await fixture.Render();
            Assert.Equal(1009, fixture.Progress.SoulId(primary.Id));
            Assert.Contains("data-pet-acquisition=\"1009\"", html);
            Assert.Equal(before, fixture.ProfileBytes);
        }
    }

    [Theory]
    [InlineData("de", "Mit Dungeonquelle (2)")]
    [InlineData("en", "With dungeon source (2)")]
    public async Task DungeonSourceFilterAndResetKeepOrdinaryPetsAndProgressIntact(string language, string filterLabel)
    {
        var kernon = Sourced(1009);
        var generalDungeonPet = Sourced(1093);
        var pinopi = Sourced(1146);
        await using var fixture = new Fixture([kernon, generalDungeonPet, pinopi], language: language);
        var before = fixture.ProfileBytes;
        await fixture.Render();

        await fixture.SetState("_source", "dungeon");
        var filtered = await fixture.Html();
        Assert.Contains($"data-pet-id=\"{kernon.Id}\"", filtered);
        Assert.Contains($"data-pet-id=\"{generalDungeonPet.Id}\"", filtered);
        Assert.DoesNotContain($"data-pet-id=\"{pinopi.Id}\"", filtered);
        Assert.Equal(2, Regex.Matches(filtered, "data-pet-id=\"").Count);
        Assert.Contains(filterLabel, filtered);
        Assert.DoesNotContain("Neue Dungeonquellen", filtered);
        Assert.DoesNotContain("New dungeon sources", filtered);

        await fixture.Invoke("ResetFilters");
        var reset = await fixture.Html();
        Assert.Contains($"data-pet-id=\"{kernon.Id}\"", reset);
        Assert.Contains($"data-pet-id=\"{generalDungeonPet.Id}\"", reset);
        Assert.Contains($"data-pet-id=\"{pinopi.Id}\"", reset);
        Assert.Equal("", fixture.State("_source"));
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de", 1093, "Expedition", "available", "Dungeon")]
    [InlineData("en", 1093, "Expedition", "available", "Dungeon")]
    [InlineData("de", 1176, "Raid", "upcoming", "Dungeon · zukünftig")]
    [InlineData("en", 1176, "Raid", "upcoming", "Dungeon · upcoming")]
    public async Task AdditionalDungeonAndRaidSourcesUseTheirOwnAvailabilityAndDoNotBorrowTheNovemberPatch(
        string language, int number, string category, string availability, string badge)
    {
        var pet = Sourced(number);
        await using var fixture = new Fixture([pet], language: language);
        var before = fixture.ProfileBytes;
        var html = await fixture.Render();
        Assert.Contains($"data-pet-acquisition=\"{number}\"", html);
        Assert.Contains($"data-pet-global-availability=\"{availability}\"", html);
        Assert.Contains($"data-pet-source-status=\"{availability}\"", html);
        Assert.Matches("data-pet-global-availability=\"" + availability + "\"[^>]*>" + Regex.Escape(badge) + "</span>", html);
        Assert.Contains($"<span class=\"pill\">{category}</span>", html);
        if (category == "Raid") Assert.DoesNotContain("<span class=\"pill\">Expedition</span>", html);
        Assert.DoesNotContain(PatchUrl, html);
        foreach (var source in PetAcquisitionCatalog.Find(number)!.Sources)
        {
            Assert.Contains($"data-pet-acquisition-source=\"{source.Id}\"", html);
            Assert.Contains(source.DungeonDisplayName(language), html);
        }
        AssertMapAction(html, enabled: false);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task OpenWorldMapPositionsAndDungeonSourcesRemainIndependent(string language)
    {
        var pet = Sourced(1093) with { Spawns = new() { ["synthetic-map"] = 3 }, Monsters = new() { ["synthetic-map"] = 9 } };
        await using var fixture = new Fixture([pet], language: language,
            maps: [new("synthetic-map", "Synthetic map", "Synthetic")]);
        var before = fixture.ProfileBytes;
        await fixture.Render();
        await fixture.SetState("_source", "dungeon");
        var html = await fixture.Html();
        Assert.Contains("data-pet-acquisition=\"1093\"", html);
        Assert.Contains("data-pet-source-status=\"available\"", html);
        AssertMapAction(html, enabled: true);
        Assert.Equal(3, fixture.Progress.Catalog.Find(pet.Id)!.Spawns["synthetic-map"]);
        Assert.Equal(9, fixture.Progress.Catalog.Find(pet.Id)!.Monsters["synthetic-map"]);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task FullDungeonCatalogueAndItsVariantDetailsAreNotLimitedToTheNovemberAdditions(string language)
    {
        var pets = PetIdTable.All.Values.Where(pet => pet.CatalogId is not null).OrderBy(pet => pet.Id)
            .Select(pet => Sourced(pet.Id)).DistinctBy(pet => pet.Id).ToArray();
        var maps = Array.Empty<MapInfo>();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOULCREST_PETS_UI_PREVIEW")))
        {
            var mapDirectory = Path.Combine(RepositoryDirectory(), "imports", "generated", "mapdata");
            var realPets = PetCatalog.Load(Path.Combine(mapDirectory, "pets.json"));
            pets = pets.Select(pet => realPets.Find(pet.Id) is { } real ? pet with
            {
                De = real.De, Icon = real.Icon, Spawns = real.Spawns, Monsters = real.Monsters
            } : pet).ToArray();
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapDirectory, "manifest.json")));
            maps = manifest.RootElement.GetProperty("maps").EnumerateArray().Select(map => new MapInfo(
                map.GetProperty("id").GetString()!, map.GetProperty("label").GetString()!,
                map.GetProperty("group").GetString()!)).ToArray();
        }
        var profile = new ProgressProfile();
        profile.Pets[Sourced(1093).Id] = new() { Souls = 3, Focus = true };
        profile.Pets[Sourced(1082).Id] = new() { Souls = 30 };
        profile.Pets[Sourced(1176).Id] = new() { Souls = 105 };
        await using var fixture = new Fixture(pets, profile, language: language, maps: maps, dungeonMaps: PetDungeonMapCatalog.All);
        var before = fixture.ProfileBytes;
        var html = await fixture.Render();
        Assert.Equal(PetAcquisitionCatalog.All.Count, fixture.Count("_dungeonSourceCount"));
        Assert.Equal(pets.Length, Regex.Matches(html, "data-pet-id=\"").Count);
        await fixture.SetState("_selectedId", Sourced(1093).Id);
        await fixture.Export("pets-catalogue");
        await fixture.SetState("_source", "dungeon");
        html = await fixture.Html();
        Assert.Equal(PetAcquisitionCatalog.All.Count, Regex.Matches(html, "data-pet-id=\"").Count);
        Assert.DoesNotContain("data-pet-id=\"gatekeeper-pinopi\"", html);
        await fixture.Export("pets-dungeon-filter");
        foreach (var (number, name) in new[] { (1093, "pets-dungeon-available"), (1176, "pets-dungeon-raid") })
        {
            await fixture.SetState("_selectedId", Sourced(number).Id);
            await fixture.Export(name);
        }
        // An unconfirmed variant must remain explicit beside an available variant of the same source.
        var uncertain = PetAcquisitionCatalog.All.Values.First(entry => entry.Sources.Any(source => source.HasUnconfirmedVariants));
        await fixture.SetState("_selectedId", Sourced(uncertain.PetNumber).Id);
        html = await fixture.Html();
        Assert.Contains("data-pet-source-status=\"unconfirmed\"", html);
        Assert.Contains("data-pet-source-status=\"available\"", html);
        Assert.Contains("data-pet-global-availability=\"available\"", html);
        foreach (var source in uncertain.Sources.Where(source => source.HasUnconfirmedVariants))
        {
            var sourceHtml = Regex.Match(html, "<li data-pet-acquisition-source=\"" + Regex.Escape(source.Id)
                + "\">(?<source>.*?)</li>", RegexOptions.Singleline).Groups["source"].Value;
            Assert.NotEmpty(sourceHtml);
            Assert.Contains("data-pet-source-status=\"unconfirmed\"", sourceHtml);
            Assert.DoesNotContain("data-pet-source-status=\"available\"", sourceHtml);
        }
        await fixture.Export("pets-dungeon-unconfirmed-variant");
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    public async Task MapActionRequiresPositivePositionsOnAnAvailableMap(
        int positions, bool mapAvailable, bool expectedEnabled)
    {
        var pet = Sourced(1009) with { Spawns = new() { ["synthetic-map"] = positions } };
        IReadOnlyList<MapInfo> maps = mapAvailable ? [new("synthetic-map", "Synthetic map", "Synthetic")] : [];
        await using var fixture = new Fixture([pet], maps: maps);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        AssertMapAction(html, expectedEnabled);
        Assert.Equal(positions, fixture.Progress.Catalog.Find(pet.Id)!.Spawns["synthetic-map"]);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Fact]
    public async Task UnlistedPetsDoNotReceiveAnotherPetsDungeonSource()
    {
        var pinopi = Sourced(1146);
        await using var fixture = new Fixture([pinopi]);
        var before = fixture.ProfileBytes;

        var html = await fixture.Render();

        Assert.DoesNotContain("data-pet-acquisition=", html);
        Assert.DoesNotContain("data-pet-dungeon-source=", html);
        Assert.DoesNotContain("data-pet-dungeon-action=", html);
        AssertMapAction(html, enabled: false);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Fact]
    public async Task DungeonActionIsDisabledWithoutProvenImagesAndNeverEnablesTheWorldMapAction()
    {
        await using var fixture = new Fixture([Sourced(1093)]);
        var before = fixture.ProfileBytes;
        var html = await fixture.Render();
        var action = Regex.Match(html, "<button\\b[^>]*data-pet-dungeon-action=\"true\"[^>]*>");
        Assert.True(action.Success);
        Assert.Contains("disabled", action.Value);
        Assert.Contains("data-pet-dungeon-unavailable", html);
        Assert.True(html.IndexOf("data-pet-dungeon-action", StringComparison.Ordinal)
            < html.IndexOf("data-pet-map-action", StringComparison.Ordinal));
        await fixture.Invoke("OpenDungeonMap", Sourced(1093).Id);
        Assert.Null(fixture.State("_dungeonSelection"));
        AssertMapAction(await fixture.Html(), enabled: false);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    [Fact]
    public async Task DungeonActionOpensLocallyAndOldPetCallbacksCannotReplaceTheCurrentSelection()
    {
        var pet = Sourced(1093);
        await using var fixture = new Fixture([pet, Sourced(1146)],
            dungeonMaps: PetDungeonMapCatalog.ForPet(1093).Take(1).ToArray());
        fixture.Ui.Navigate("pets");
        fixture.Settings.Current.LastMap = "verteron";
        var before = fixture.ProfileBytes;
        await fixture.Render();
        await fixture.SelectUnverified(pet.Id);
        await fixture.Invoke("OpenDungeonMap", pet.Id);
        var opened = Assert.IsType<DungeonPetMapSelection>(fixture.State("_dungeonSelection"));
        Assert.Equal(pet.Id, opened.PetId);
        Assert.Contains("data-dungeon-pet-dialog=\"" + pet.Id + "\"", await fixture.Html());
        Assert.Equal("pets", fixture.Ui.Tab);
        Assert.Null(fixture.Ui.PendingPetFocus);
        Assert.Equal("verteron", fixture.Settings.Current.LastMap);
        await fixture.Invoke("CloseDungeonMap");
        await fixture.SelectUnverified(Sourced(1146).Id);
        await fixture.Invoke("OpenDungeonMap", pet.Id);
        Assert.Null(fixture.State("_dungeonSelection"));
        Assert.Equal("pets", fixture.Ui.Tab);
        Assert.Equal("verteron", fixture.Settings.Current.LastMap);
        Assert.Equal(before, fixture.ProfileBytes);
    }

    private static void AssertMapAction(string html, bool enabled)
    {
        var action = Regex.Match(html, "<button\\b[^>]*data-pet-map-action=\"true\"[^>]*>");
        Assert.True(action.Success, "The map action needs an explicit testable button.");
        Assert.Equal(!enabled, Regex.IsMatch(action.Value, "\\bdisabled(?:\\s|=|>)"));
    }

    private static PetDefinition Sourced(int soulId)
    {
        var source = PetIdTable.Find(soulId);
        Assert.NotNull(source);
        Assert.NotNull(source.CatalogId);
        return new PetDefinition { Id = source.CatalogId, En = source.En,
            Genus = source.Genus.ToLowerInvariant(), NumericId = soulId.ToString(CultureInfo.InvariantCulture) };
    }

    private static string RepositoryDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "Soulcrest.App", "wwwroot", "css", "app.css")))
                    return directory.FullName;
        throw new DirectoryNotFoundException("The offline preview requires the local repository assets.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-pets-view-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly string _language;
        private readonly Dictionary<string, byte[]?> _backups;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private PetsView _view = default!;
        public ProgressService Progress { get; }
        public UiState Ui => _provider.GetRequiredService<UiState>();
        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public byte[] ProfileBytes => File.ReadAllBytes(AppPaths.ProgressFile);
        public int Count(string field) => (int)typeof(PetsView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_view)!;
        public Dictionary<string, (int Total, int Unlocked)> GenusCounts =>
            (Dictionary<string, (int Total, int Unlocked)>)typeof(PetsView)
                .GetField("_genusCounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_view)!;

        public Fixture(IReadOnlyList<PetDefinition> pets, ProgressProfile? profile = null, IReadOnlyList<PetDefinition>? learned = null, string language = "de", IReadOnlyList<MapInfo>? maps = null,
            IReadOnlyList<PetDungeonMap>? dungeonMaps = null)
        {
            _language = language;
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), JsonSerializer.Serialize(new
            {
                maps = (maps ?? []).Select(map => new { id = map.Id, label = map.Label, group = map.Group }).ToArray(),
            }));
            File.WriteAllText(Path.Combine(_folder, "pets.json"), JsonSerializer.Serialize(pets));
            _backups = new[] { AppPaths.SettingsFile, AppPaths.ProgressFile, AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile }
                .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
            JsonFile.Save(AppPaths.ProgressFile, profile ?? new ProgressProfile());
            JsonFile.Save(AppPaths.LearnedPetsFile, learned ?? []);
            JsonFile.Save(AppPaths.LearnedNamesFile, new Dictionary<string, string>());
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<Soulcrest.App.Network.NetworkLootService>();
            services.AddSingleton<UiState>();
            services.AddSingleton<IJSRuntime>(new SilentJs());
            services.AddSingleton(provider => new PetDungeonMapService(provider.GetRequiredService<ProgressService>(), dungeonMaps ?? [], _ => true));
            _provider = services.BuildServiceProvider();
            var settings = _provider.GetRequiredService<SettingsService>();
            settings.Current.NameLanguage = settings.Current.UiLanguage = UiText.Language = language;
            Progress = _provider.GetRequiredService<ProgressService>();
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public async Task<string> Render()
        {
            _root = await _renderer.Dispatcher.InvokeAsync(async () =>
                await _renderer.RenderComponentAsync<CaptureView>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(CaptureView.Capture)] = (Action<PetsView>)(view => _view = view) })));
            return await Html();
        }

        public async Task<string> Html() => WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString));
        public async Task Export(string state)
        {
            var output = Environment.GetEnvironmentVariable("SOULCREST_PETS_UI_PREVIEW");
            if (string.IsNullOrWhiteSpace(output)) return;
            var repository = RepositoryDirectory();
            var assets = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
            var mapAssets = Path.Combine(repository, "imports", "generated", "mapdata") + Path.DirectorySeparatorChar;
            var raw = await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
            raw = raw.Replace("https://mapdata.soulcrest/", new Uri(mapAssets).AbsoluteUri, StringComparison.Ordinal);
            var styles = string.Join("", new[] { "app.css", "app-themes.css" }.Select(name =>
                "<link rel=\"stylesheet\" href=\"" + new Uri(Path.Combine(assets, "css", name)).AbsoluteUri + "\">"));
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, state + "-" + _language + ".html"),
                "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + styles + "</head><body><main class=\"content\" style=\"height:100vh\">" + raw + "</main></body></html>");
        }
        public async Task UseNetworkProgress(string id) => await _renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(PetsView).GetMethod("UseNetworkProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, [id]);
        });

        public async Task SelectUnverified(string id) => await _renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(PetsView).GetField("_selectedId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_view, id);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, null);
        });

        public object? State(string field) =>
            typeof(PetsView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_view);

        public Task SetState(string field, object value) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(PetsView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_view, value);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, null);
        });

        public Task Invoke(string method, params object?[] args) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(PetsView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, args);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, null);
        });

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
            {
                if (bytes is null) File.Delete(path);
                else File.WriteAllBytes(path, bytes);
            }
            UiText.Language = _previousLanguage;
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(_folder), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class SilentJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class CaptureView : ComponentBase
    {
        [Parameter] public Action<PetsView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<PetsView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((PetsView)value));
            builder.CloseComponent();
        }
    }
}
