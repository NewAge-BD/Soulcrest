using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ReviewRegressionTests
{
    [Fact]
    public void ParallelSettingsUpdatesPersistEveryChange()
    {
        var settings = new SettingsService();
        settings.Update(s => s.OverlayX = 0);
        Parallel.For(0, 100, _ => settings.Update(s => s.OverlayX++));
        Assert.Equal(100, settings.Current.OverlayX);
        Assert.Equal(100, new SettingsService().Current.OverlayX);
        Assert.Empty(Directory.GetFiles(AppPaths.DataDirectory, "settings.json.*.tmp"));
    }


    [Fact]
    public void NetworkPetWithoutMapEntryIsVisibleAndPersistent()
    {
        Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        var folder = Path.Combine(Path.GetTempPath(), "soulcrest-review-network-pet-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "progress.json");
        var savedFiles = new[] { AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile, AppPaths.EventsFile }
            .ToDictionary(file => file, file => File.Exists(file) ? File.ReadAllBytes(file) : null);
        Directory.CreateDirectory(folder);
        try
        {
            // A local progress path still shares the learned catalogue. Exercise a true first
            // observation without leaving a sourced pet visible to later UI fixtures.
            JsonFile.Save(AppPaths.LearnedPetsFile, Array.Empty<PetDefinition>());
            JsonFile.Save(AppPaths.LearnedNamesFile, new Dictionary<string, string>());
            var progress = new ProgressService(path, new PetCatalog([]));
            Assert.Empty(progress.Catalog.Pets);
            var source = PetIdTable.Find(1062)!;
            var id = progress.EnsureNetworkPet(source);
            var at = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
            Assert.True(progress.BindPetSession(new(1, 1, at)));
            Assert.True(progress.ApplyPetGain([new(source.Id, 2)], new(1, 2, at.AddSeconds(1))));
            Assert.Equal(id, progress.EnsureNetworkPet(source));
            var reloaded = new ProgressService(path, new PetCatalog([]));
            Assert.Single(reloaded.Catalog.Pets, p => p.Id == id);
            Assert.Equal(source.En, reloaded.Catalog.Find(id)!.En);
            Assert.Equal(2, reloaded.Souls(id));
            Assert.Equal(2, Assert.Single(progress.Recent()).Quantity);
        }
        finally
        {
            foreach (var (file, bytes) in savedFiles)
            {
                if (bytes is null) File.Delete(file);
                else File.WriteAllBytes(file, bytes);
            }
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ReconnectWithNewLocalPortRequiresNewCaptureTarget()
    {
        var original = new CaptureTarget("device", "filter", new HashSet<int> { 80, 81 }, new HashSet<int> { 123 }, "old");
        Assert.False(NetworkLootService.SameConnection(original, original with { GamePorts = new HashSet<int> { 124 } }));
        Assert.False(NetworkLootService.SameConnection(original, original with { ServerPorts = new HashSet<int> { 82 } }));
        Assert.True(NetworkLootService.SameConnection(original, original with { ServerPorts = new HashSet<int> { 81, 80 }, Description = "new" }));
    }
}
