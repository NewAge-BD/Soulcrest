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
        var bytes = Guid.NewGuid().ToByteArray();
        try
        {
            foreach (var path in kept.Keys) File.Delete(path);
            File.WriteAllBytes(first, bytes);
            if (duplicate) File.WriteAllBytes(second, bytes);
            var cache = new PortraitValidationCache(cachePath, "test-icons");
            var hash = PortraitValidationCache.ImageHash(first);
            var match = duplicate ? new PortraitMatch(null, 0, null, 0) : new PortraitMatch("swarm", 42, null, 0);
            cache.GetOrMatch("validation-first", hash, () => match);
            if (duplicate) cache.GetOrMatch("validation-second", hash, () => match);
            cache.Save();
            cache = new PortraitValidationCache(cachePath, "test-icons");
            using var iconsOnly = new PortraitMatcher();
            using var scan = new PetScanService(new SettingsService(), new ProgressService());
            scan.RepairLearnedPortraits(iconsOnly, cache, CancellationToken.None);
            Assert.False(File.Exists(first));
            var suffix = duplicate ? "doppelt" : "zeigt-swarm";
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(AppPaths.RejectedPortraitsDirectory, $"validation-first-{suffix}.png")));
            if (duplicate)
            {
                Assert.False(File.Exists(second));
                Assert.Equal(0, cache.Hits); // Duplicate hashing still happens before consulting matches.
            }
            else Assert.Equal(1, cache.Hits); // The cached target is re-evaluated against current catalog rules.
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
            File.Delete(cachePath);
            foreach (var name in new[] { "validation-first-doppelt.png", "validation-second-doppelt.png", "validation-first-zeigt-swarm.png" })
                File.Delete(Path.Combine(AppPaths.RejectedPortraitsDirectory, name));
            foreach (var (path, content) in kept) File.WriteAllBytes(path, content);
        }
    }
}
