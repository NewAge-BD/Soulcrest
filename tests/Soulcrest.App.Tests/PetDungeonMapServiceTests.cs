using System.Globalization;
using System.Security.Cryptography;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class PetDungeonMapServiceTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EverySceneKeepsAllProvenPositionsAndSelectsOnlyTheExactRequestedPetNumber(string language)
    {
        using var fixture = new Fixture(language);
        var service = new PetDungeonMapService(fixture.Progress, PetDungeonMapCatalog.All, _ => true);
        var requested = fixture.Pet(1093);
        var selection = service.For(requested, language);

        Assert.Equal(requested.Id, selection.PetId);
        Assert.Equal(1093, selection.PetNumber);
        Assert.Null(selection.UnavailableReason);
        Assert.Equal(3, selection.Scenes.Count);
        foreach (var scene in selection.Scenes)
        {
            var map = PetDungeonMapCatalog.All.Single(map => map.Id == scene.Key);
            Assert.Equal(map.MapId, scene.MapId);
            Assert.Equal(map.DisplayName(language), scene.Title);
            Assert.Equal((map.Width, map.Height), (scene.Width, scene.Height));
            Assert.Equal("https://mapdata.soulcrest/" + map.Image, scene.ImageUrl);
            Assert.Equal(map.Spawns.Count, scene.Pins.Count);
            Assert.Contains(scene.Pins, pin => pin.Selected);
            Assert.Contains(scene.Pins, pin => !pin.Selected);
            foreach (var pin in scene.Pins)
            {
                var original = map.Spawns.Single(spawn => spawn.Id == pin.Id);
                var identity = PetIdTable.Find(original.PetNumber)!;
                var source = PetAcquisitionCatalog.Find(original.PetNumber)!.Sources.SelectMany(source => source.Npcs)
                    .First(npc => npc.Id == original.NpcId && npc.MapIds.Contains(map.MapId));
                Assert.Equal(original.PetNumber, pin.PetNumber);
                Assert.Equal(original.PetNumber == 1093, pin.Selected);
                Assert.Equal((original.X, original.Y), (pin.X, pin.Y));
                Assert.Equal(fixture.Progress.Catalog.Find(identity.CatalogId!)!.DisplayName(language), pin.PetName);
                Assert.Equal(source.DisplayName(language), pin.NpcName);
            }
        }
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<DungeonPetMapScene>>(selection.Scenes).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<DungeonPetMapPin>>(selection.Scenes[0].Pins).Clear());
    }

    [Fact]
    public void AvailabilityComesFromEachExactVariantAndFutureMapsNeverBecomeReleasedBecausePinsExist()
    {
        using var fixture = new Fixture();
        var service = new PetDungeonMapService(fixture.Progress, PetDungeonMapCatalog.All, _ => true);
        var seen = new HashSet<PetGlobalAvailability>();
        foreach (var number in PetAcquisitionCatalog.All.Keys)
        {
            var selection = service.For(fixture.Pet(number), "en");
            Assert.NotEmpty(selection.Scenes);
            Assert.Equal(selection.Scenes.Count, selection.Scenes.Select(scene => (scene.Title, scene.Variant)).Distinct().Count());
            Assert.Equal(selection.Scenes.Select(scene => scene.Availability).Order(), selection.Scenes.Select(scene => scene.Availability));
            foreach (var scene in selection.Scenes)
            {
                var statuses = PetAcquisitionCatalog.Find(number)!.Sources.SelectMany(source => source.GlobalAvailability)
                    .Where(status => status.MapId == scene.MapId).ToArray();
                var distinct = statuses.Select(status => status.Status).Distinct().ToArray();
                Assert.Equal(distinct.Length == 1 ? distinct[0] : PetGlobalAvailability.Unconfirmed, scene.Availability);
                Assert.Equal(string.Join(" / ", statuses.Select(status => status.CheckedAt).Distinct(StringComparer.Ordinal)), scene.CheckedAt);
                seen.Add(scene.Availability);
            }
        }
        Assert.Contains(PetGlobalAvailability.Available, seen);
        Assert.Contains(PetGlobalAvailability.Upcoming, seen);
        Assert.Contains(PetGlobalAvailability.Unconfirmed, seen);
        Assert.All(service.For(fixture.Pet(1176), "en").Scenes, scene => Assert.Equal(PetGlobalAvailability.Upcoming, scene.Availability));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void OriginalDuplicateVariantLabelsAreDisambiguatedWithoutInventingADifficulty(string language)
    {
        using var fixture = new Fixture(language);
        var service = new PetDungeonMapService(fixture.Progress, PetDungeonMapCatalog.All, _ => true);
        var duplicateGroups = PetDungeonMapCatalog.All.GroupBy(map => (map.En, map.VariantEn))
            .Where(group => group.Key.VariantEn is not null && group.Count() > 1).ToArray();
        Assert.Contains(duplicateGroups, group => group.Key.En == "Draupnir");
        Assert.Contains(duplicateGroups, group => group.Key.En == "Fire Temple");
        foreach (var group in duplicateGroups)
        {
            var sharedPet = group.First().Spawns.Select(spawn => spawn.PetNumber).Distinct()
                .First(number => group.All(map => map.Spawns.Any(spawn => spawn.PetNumber == number)));
            var scenes = service.For(fixture.Pet(sharedPet), language).Scenes
                .Where(scene => group.Any(map => map.Id == scene.Key)).ToArray();
            Assert.Equal(group.Count(), scenes.Length);
            Assert.Equal(scenes.Length, scenes.Select(scene => scene.Key).Distinct().Count());
            Assert.Equal(scenes.Length, scenes.Select(scene => scene.MapId).Distinct().Count());
            Assert.Equal(scenes.Length, scenes.Select(scene => scene.Variant).Distinct().Count());
            Assert.All(scenes, scene => Assert.StartsWith(group.First().VariantDisplayName(language) + " · ", scene.Variant, StringComparison.Ordinal));
            Assert.Equal(scenes.Select(scene => scene.Variant), service.For(fixture.Pet(sharedPet), language).Scenes
                .Where(scene => group.Any(map => map.Id == scene.Key)).Select(scene => scene.Variant));
        }
    }

    [Fact]
    public void MissingImagesAndMissingSelectedPlacementsRemainHonestWithoutChangingWorldMapsOrProgress()
    {
        using var fixture = new Fixture();
        var mapsBefore = fixture.Progress.Maps.ToArray();
        var filesBefore = fixture.Files();
        var changes = 0;
        fixture.Progress.Changed += () => changes++;
        var unavailable = new PetDungeonMapService(fixture.Progress, PetDungeonMapCatalog.All, _ => false);
        var missing = unavailable.For(fixture.Pet(1093), "en");
        Assert.Empty(missing.Scenes);
        Assert.NotNull(missing.UnavailableReason);
        var unrelated = PetDungeonMapCatalog.All.Where(map => map.Spawns.All(spawn => spawn.PetNumber != 1093)).ToArray();
        var withoutPosition = new PetDungeonMapService(fixture.Progress, unrelated, _ => true).For(fixture.Pet(1093), "en");
        Assert.Empty(withoutPosition.Scenes);
        Assert.NotNull(withoutPosition.UnavailableReason);
        Assert.NotEqual(missing.UnavailableReason, withoutPosition.UnavailableReason);
        var unknown = new PetDefinition { Id = "unverified-same-name", En = fixture.Pet(1093).En, Genus = "unknown" };
        Assert.Empty(unavailable.For(unknown, "en").Scenes);
        Assert.Equal(0, unavailable.For(unknown, "en").PetNumber);
        Assert.Equal(new PetDungeonMapDiagnostics(46, 65, 3341, 15), unavailable.Diagnostics());
        var ready = new PetDungeonMapService(fixture.Progress, PetDungeonMapCatalog.All, _ => true);
        foreach (var number in PetAcquisitionCatalog.All.Keys) Assert.NotEmpty(ready.For(fixture.Pet(number), "de").Scenes);
        Assert.Equal(new PetDungeonMapDiagnostics(46, 65, 3341, 0), ready.Diagnostics());
        Assert.Equal(mapsBefore, fixture.Progress.Maps);
        Assert.Single(fixture.Progress.Maps);
        Assert.Equal(0, changes);
        var filesAfter = fixture.Files();
        Assert.Equal(filesBefore.Keys.Order(), filesAfter.Keys.Order());
        foreach (var (path, bytes) in filesBefore) Assert.Equal(bytes, filesAfter[path]);
    }

    [Fact]
    public void OneMissingSharedImageHidesOnlyItsScenesAndCountsTheImageOnce()
    {
        using var fixture = new Fixture();
        var maps = PetDungeonMapCatalog.ForPet(1199).ToArray();
        var missing = maps[0].Image;
        var service = new PetDungeonMapService(fixture.Progress, maps, image => image != missing);
        var scenes = service.For(fixture.Pet(1199), "en").Scenes;
        Assert.NotEmpty(scenes);
        Assert.Equal(maps.Count(map => map.Image != missing), scenes.Count);
        Assert.All(scenes,
            scene => Assert.NotEqual(missing, maps.Single(map => map.Id == scene.Key).Image));
        Assert.Equal(1, service.Diagnostics().MissingImages);
    }

    [Fact]
    public void ImageValidationChecksBytesAndRefreshesCachedResultsAfterTimestampOrLengthChanges()
    {
        using var images = new TempImages();
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };
        var map = ImageMap(bytes);
        var validate = PetDungeonMapService.ImageValidator(images.Directory, [map]);
        Assert.False(validate(map.Image));
        var path = images.Write(map.Image, bytes);
        var initialTime = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, initialTime);
        Assert.True(validate(map.Image));
        Assert.True(validate(map.Image));
        var corrupt = bytes.ToArray();
        corrupt[^1]++;
        File.WriteAllBytes(path, corrupt);
        File.SetLastWriteTimeUtc(path, initialTime.AddSeconds(2));
        Assert.False(validate(map.Image));
        Assert.False(validate(map.Image));
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, initialTime.AddSeconds(4));
        Assert.True(validate(map.Image));
        File.WriteAllBytes(path, [.. bytes, 5]);
        File.SetLastWriteTimeUtc(path, initialTime.AddSeconds(4));
        Assert.False(validate(map.Image));
        File.Delete(path);
        Assert.False(validate(map.Image));
        images.Write(map.Image, bytes);
        File.SetLastWriteTimeUtc(path, initialTime.AddSeconds(6));
        Assert.True(validate(map.Image));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("dungeon-maps/../../outside.png")]
    [InlineData("/outside.png")]
    [InlineData("https://mapdata.soulcrest/outside.png")]
    [InlineData("file:///C:/outside.png")]
    [InlineData("\\\\server\\share\\outside.png")]
    public void ImageValidationRejectsTraversalAndNonPackageSourcesEvenWhenDeclaredByAnInjectedMap(string image)
    {
        using var images = new TempImages();
        byte[] bytes = [1, 2, 3, 4];
        var map = ImageMap(bytes) with { Image = image };
        var validate = PetDungeonMapService.ImageValidator(images.Directory, [map]);
        Assert.False(validate(image));
    }

    [Fact]
    public void ImageValidationHasNoFallbackForUnknownPathsMissingRootsOrCorruptContent()
    {
        using var images = new TempImages();
        byte[] bytes = [1, 2, 3, 4];
        var map = ImageMap(bytes);
        images.Write(map.Image, [4, 3, 2, 1]);
        var validate = PetDungeonMapService.ImageValidator(images.Directory, [map]);
        Assert.False(validate(map.Image));
        images.Write("dungeon-maps/unknown.png", bytes);
        Assert.False(validate("dungeon-maps/unknown.png"));
        Assert.False(PetDungeonMapService.ImageValidator(null, [map])(map.Image));
        Assert.False(PetDungeonMapService.ImageValidator(Path.Combine(images.Directory, "missing"), [map])(map.Image));
    }

    private static PetDungeonMap ImageMap(byte[] bytes) => PetDungeonMapCatalog.All[0] with
    {
        Image = "dungeon-maps/validation.png", ImageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
    };

    private sealed class Fixture : IDisposable
    {
        private readonly TempImages _files = new();
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups;
        public ProgressService Progress { get; }

        public Fixture(string language = "en")
        {
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            _backups = new[] { AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile }
                .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
            JsonFile.Save(AppPaths.LearnedPetsFile, new List<PetDefinition>());
            JsonFile.Save(AppPaths.LearnedNamesFile, new Dictionary<string, string>());
            File.WriteAllText(Path.Combine(_files.Directory, "manifest.json"),
                "{\"maps\":[{\"id\":\"world-reference\",\"label\":\"Existing world map\",\"group\":\"Example\"}]}");
            var pets = PetAcquisitionCatalog.All.Keys.Select(number =>
            {
                var identity = PetIdTable.Find(number)!;
                Assert.NotNull(identity.CatalogId);
                return new PetDefinition { Id = identity.CatalogId, En = identity.En, De = "Belegtes Pet " + number,
                    Genus = identity.Genus.ToLowerInvariant(), NumericId = number.ToString(CultureInfo.InvariantCulture) };
            }).ToArray();
            var profile = new ProgressProfile();
            profile.Pets[pets[0].Id] = new() { Souls = 17, Focus = true };
            var profilePath = Path.Combine(_files.Directory, "progress.json");
            JsonFile.Save(profilePath, profile);
            File.WriteAllText(Path.Combine(_files.Directory, "targets.json"), "{\"preserved\":true}");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _files.Directory);
            UiText.Language = language;
            Progress = new ProgressService(profilePath, new PetCatalog(pets));
        }

        public PetDefinition Pet(int number) => Progress.Catalog.Find(PetIdTable.Find(number)!.CatalogId!)!;
        public Dictionary<string, byte[]> Files()
        {
            var files = System.IO.Directory.GetFiles(_files.Directory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(_files.Directory, path), File.ReadAllBytes);
            foreach (var path in _backups.Keys)
                if (File.Exists(path)) files["isolated-learned/" + Path.GetFileName(path)] = File.ReadAllBytes(path);
            return files;
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            UiText.Language = _previousLanguage;
            foreach (var (path, bytes) in _backups)
            {
                if (bytes is null) File.Delete(path);
                else File.WriteAllBytes(path, bytes);
            }
            _files.Dispose();
        }
    }

    private sealed class TempImages : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-dungeon-maps-" + Guid.NewGuid().ToString("N"));
        public TempImages() => System.IO.Directory.CreateDirectory(Directory);
        public string Write(string relative, byte[] bytes)
        {
            var path = Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        public void Dispose()
        {
            var root = Path.GetFullPath(Directory);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), root, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("soulcrest-dungeon-maps-", Path.GetFileName(root), StringComparison.Ordinal);
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
        }
    }
}
