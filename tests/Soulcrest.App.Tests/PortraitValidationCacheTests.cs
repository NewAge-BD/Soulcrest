using Soulcrest.App.Services;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PortraitValidationCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-validation-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_directory, "cache.json");
    private static readonly PortraitMatch Match = new("other-pet", 42, "runner-up", 3);

    [Fact]
    public void UnchangedPortraitUsesPersistedMatchIncludingRepairTarget()
    {
        var first = new PortraitValidationCache(CachePath, "icons-v1");
        Assert.Equal(Match, first.GetOrMatch("pet", "hash", () => Match));
        first.Save();
        var next = new PortraitValidationCache(CachePath, "icons-v1");
        Assert.Equal(Match, next.GetOrMatch("pet", "hash", () => throw new InvalidOperationException("Must use cache.")));
        Assert.Equal(1, next.Hits);
        Assert.Equal(0, next.Misses);
    }

    [Theory]
    [InlineData("icons-v2", "pet", "hash")]
    [InlineData("icons-v1", "pet", "changed-image")]
    [InlineData("icons-v1", "new-pet", "hash")]
    public void ChangedContextImageOrBindingIsRevalidated(string context, string pet, string hash)
    {
        var first = new PortraitValidationCache(CachePath, "icons-v1");
        first.GetOrMatch("pet", "hash", () => Match);
        first.Save();
        var calls = 0;
        var next = new PortraitValidationCache(CachePath, context);
        next.GetOrMatch(pet, hash, () => { calls++; return Match; });
        Assert.Equal(1, calls);
        Assert.Equal(0, next.Hits);
    }

    [Fact]
    public void ContextTracksIconContentColourVariantFilenamePetAndMatcherRevision()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "art.png");
        File.WriteAllText(path, "first image");
        var before = PortraitValidationCache.ContextFor([("pet", path)], "matcher-v1");
        Assert.NotEqual(before, PortraitValidationCache.ContextFor([("other", path)], "matcher-v1"));
        Assert.NotEqual(before, PortraitValidationCache.ContextFor([("pet", path)], "matcher-v2"));
        var variant = Path.Combine(_directory, "art_cv01.png");
        File.Copy(path, variant);
        Assert.NotEqual(before, PortraitValidationCache.ContextFor([("pet", variant)], "matcher-v1"));
        File.WriteAllText(path, "second image");
        Assert.NotEqual(before, PortraitValidationCache.ContextFor([("pet", path)], "matcher-v1"));
        File.Delete(path);
        Assert.NotEqual(before, PortraitValidationCache.ContextFor([("pet", path)], "matcher-v1"));
    }

    [Fact]
    public void CorruptCacheFallsBackToMatching()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CachePath, "{broken");
        var cache = new PortraitValidationCache(CachePath, "icons");
        Assert.Equal(Match, cache.GetOrMatch("pet", "hash", () => Match));
        cache.Save();
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void UnusedPortraitsArePrunedAndNegativeMatchesRemainCacheable()
    {
        var first = new PortraitValidationCache(CachePath, "icons");
        var unknown = new PortraitMatch(null, 0, null, 0);
        first.GetOrMatch("old", "hash", () => Match);
        first.GetOrMatch("unknown", "hash", () => unknown);
        first.Save();
        var next = new PortraitValidationCache(CachePath, "icons");
        Assert.Equal(unknown, next.GetOrMatch("unknown", "hash", () => throw new InvalidOperationException()));
        next.Save();
        var final = new PortraitValidationCache(CachePath, "icons");
        final.GetOrMatch("old", "hash", () => Match);
        Assert.Equal(1, final.Misses);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
