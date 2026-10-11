using Soulcrest.App.Services;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PetPortraitRepairTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedMatchesDoNotBypassDuplicateOrWrongBindingRepair(bool duplicate)
    {
        Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(AppPaths.LearnedPortraitsDirectory);
        // Isolate portrait files within the suite's temporary data directory, and restore them afterwards.
        var kept = ProgressService.LearnedPortraits().ToDictionary(p => p.Path, p => File.ReadAllBytes(p.Path));
        var first = Path.Combine(AppPaths.LearnedPortraitsDirectory, "validation-first.png");
        var second = Path.Combine(AppPaths.LearnedPortraitsDirectory, "validation-second.png");
        var cachePath = Path.Combine(AppPaths.DataDirectory, "repair-test-cache.json");
        var mapData = Path.Combine(AppPaths.DataDirectory, "repair-test-mapdata-" + Guid.NewGuid().ToString("N"));
        var originalMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        var rejected = new[] { "validation-first-doppelt.png", "validation-second-doppelt.png", "validation-first-zeigt-swarm.png" }
            .Select(name => Path.Combine(AppPaths.RejectedPortraitsDirectory, name)).ToArray();
        var keptFiles = new[] { AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile, AppPaths.ProgressFile, cachePath }
            .Concat(rejected).ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        var bytes = Guid.NewGuid().ToByteArray();
        try
        {
            foreach (var path in kept.Keys) File.Delete(path);
            File.WriteAllText(AppPaths.LearnedPetsFile, "[]");
            File.WriteAllText(AppPaths.LearnedNamesFile, "{}");
            File.WriteAllText(AppPaths.ProgressFile, "{}");
            Directory.CreateDirectory(mapData);
            File.WriteAllText(Path.Combine(mapData, "manifest.json"), """{"maps":[]}""");
            // These are known map identities: a learned pet resembling a named pet deliberately
            // remains a user decision and would not exercise the wrong-binding repair rule.
            File.WriteAllText(Path.Combine(mapData, "pets.json"), """
                [
                  {"id":"validation-first","en":"Validation First","genus":"fera"},
                  {"id":"validation-second","en":"Validation Second","genus":"fera"},
                  {"id":"swarm","en":"Swarm","genus":"fera"}
                ]
                """);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", mapData);
            var progress = new ProgressService();
            Assert.NotNull(progress.Catalog.Find("validation-first"));
            Assert.False(progress.IsLearned("validation-first"));
            File.WriteAllBytes(first, bytes);
            if (duplicate) File.WriteAllBytes(second, bytes);
            Assert.Equal(duplicate ? 2 : 1, progress.RecognitionPortraits().Count);
            var cache = new PortraitValidationCache(cachePath, "test-icons");
            var hash = PortraitValidationCache.ImageHash(first);
            var match = duplicate ? new PortraitMatch(null, 0, null, 0) : new PortraitMatch("swarm", 42, null, 0);
            cache.GetOrMatch("validation-first", hash, () => match);
            if (duplicate) cache.GetOrMatch("validation-second", hash, () => match);
            cache.Save();
            cache = new PortraitValidationCache(cachePath, "test-icons");
            using var iconsOnly = new PortraitMatcher();
            using var scan = new PetScanService(new SettingsService(), progress);
            scan.RepairLearnedPortraits(iconsOnly, cache, CancellationToken.None);
            Assert.False(File.Exists(first));
            var suffix = duplicate ? "doppelt" : "zeigt-swarm";
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(AppPaths.RejectedPortraitsDirectory, $"validation-first-{suffix}.png")));
            if (duplicate)
            {
                Assert.False(File.Exists(second));
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(AppPaths.RejectedPortraitsDirectory, "validation-second-doppelt.png")));
                Assert.Equal(0, cache.Hits); // Duplicate hashing still happens before consulting matches.
            }
            else Assert.Equal(1, cache.Hits); // The cached target is re-evaluated against current catalog rules.
            Assert.Equal(0, cache.Misses);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", originalMapData);
            DeleteIfPresent(first);
            DeleteIfPresent(second);
            foreach (var (path, content) in keptFiles)
            {
                if (content is null) DeleteIfPresent(path);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, content);
                }
            }
            foreach (var (path, content) in kept) File.WriteAllBytes(path, content);
            DeleteIfPresent(Path.Combine(mapData, "pets.json"));
            DeleteIfPresent(Path.Combine(mapData, "manifest.json"));
            if (Directory.Exists(mapData)) Directory.Delete(mapData);
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
