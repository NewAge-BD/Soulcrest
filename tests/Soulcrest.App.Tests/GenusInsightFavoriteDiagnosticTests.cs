using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GenusInsightFavoriteDiagnosticTests
{
    [Fact]
    public void PreferenceDiagnosisIsSelectableWithoutNetworkCaptureOrCharacterDependencies()
    {
        var features = DiagnosticFeatureCatalog.Resolve(["combat-genus-favorites"]);
        Assert.Equal(new[] { "system", "combat-genus-favorites" }, features.Select(feature => feature.Id));
        Assert.Contains(DiagnosticFeatureCatalog.Resolve(["overlay-genus-insight"]), feature => feature.Id == "combat-genus-favorites");
    }

    [Fact]
    public void DiagnosisReadsAcceptedAnonymousCountsWithoutSavingOrPublishingPreferences()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-favorite-diagnosis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = System.IO.Path.Combine(folder, "favorites.json");
            var service = new GenusInsightFavoriteService(path);
            service.ToggleGlobal("FixingDamage"); service.Toggle(new("special", 1, "FixingDamage"));
            service.Toggle(new("special", 2, "Defense"));
            var bytes = File.ReadAllBytes(path);
            var snapshot = service.Snapshot;
            var changed = 0; service.Changed += () => changed++;
            var contributor = DiagnosticContributors.Create(new DiagnosticServiceRegistry(service))
                .Single(item => item.FeatureIds.Contains("combat-genus-favorites"));
            var result = contributor.Collect("combat-genus-favorites");
            Assert.Equal(DiagnosticStatus.Healthy, result.Status);
            Assert.Equal(1, result.Facts["globalFavoriteStats"]);
            Assert.Equal(2, result.Facts["slotOverrides"]);
            Assert.Equal(1, result.Facts["includedSlotOverrides"]);
            Assert.Equal(1, result.Facts["excludedSlotOverrides"]);
            Assert.Equal(snapshot.Favorites.Count, result.Facts["effectiveSlotFavorites"]);
            Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Same(snapshot, service.Snapshot); Assert.Equal(0, changed);
            var serialized = JsonSerializer.Serialize(result);
            Assert.DoesNotContain(folder, serialized); Assert.DoesNotContain("FixingDamage", serialized);
            Assert.All(result.Facts.Values, value => Assert.True(value is null or bool or int));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AbsentCorruptAndFailedPersistenceStatesAreExplicitInsteadOfHealthyEmptyPreferences()
    {
        Assert.Equal(DiagnosticStatus.Unavailable, GenusInsightFavoriteDiagnostics.Capture(null, DateTimeOffset.UtcNow).Status);
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-favorite-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = System.IO.Path.Combine(folder, "favorites.json");
            File.WriteAllText(path, "{\"schemaVersion\":999}");
            var before = File.ReadAllBytes(path);
            var unreadable = new GenusInsightFavoriteService(path);
            Assert.Equal(DiagnosticStatus.Error, GenusInsightFavoriteDiagnostics.Capture(unreadable, DateTimeOffset.UtcNow).Status);
            Assert.Equal(before, File.ReadAllBytes(path));
            var failed = new GenusInsightFavoriteService(System.IO.Path.Combine(folder, "other.json"),
                static (_, _) => throw new IOException("Synthetic failure"));
            Assert.Throws<IOException>(() => failed.ToggleGlobal("FixingDamage"));
            var result = GenusInsightFavoriteDiagnostics.Capture(failed, DateTimeOffset.UtcNow);
            Assert.Equal(DiagnosticStatus.Error, result.Status);
            Assert.Equal(true, result.Facts["saveFailed"]);
        }
        finally { Directory.Delete(folder, true); }
    }
}
