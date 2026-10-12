using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationTests
{
    private static ExplorationPlace Place(string id, string en, string? de = null) => new(id, "altgard", "dungeon", 0, en, de);
    [Fact]
    public void CharacterProgressPersistsSeparatelyAndCanBeCorrected()
    {
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-exploration-" + Guid.NewGuid(), "exploration.json");
        var places = new[] { Place("a", "Hidden Vein Cave Entrance") };
        var service = new ExplorationService(path, places);
        var first = service.ActiveId;
        service.SetDone(first, ["a", "invalid"]);
        service.Add("Second / Server");
        Assert.Empty(service.Completed);
        var second = service.ActiveId;
        service.Select(first); Assert.Equal(new[] { "a" }, service.Completed);
        var loaded = new ExplorationService(path, places);
        Assert.Equal(first, loaded.ActiveId); Assert.Contains("a", loaded.Completed);
        loaded.Select(second); Assert.Empty(loaded.Completed);
        loaded.SetDone(first, ["a"], false);
        loaded.Select(first); Assert.Empty(loaded.Completed);
    }
    [Fact]
    public void StrongholdsUseTheirOwnMapAndCategoryCatalog()
    {
        var service = new ExplorationService(new ProgressService());
        var candidates = service.Places.Where(p => p.Map == "altgard" && p.Kind == "stronghold").ToArray();
        Assert.Equal(15, candidates.Length);
    }
}
