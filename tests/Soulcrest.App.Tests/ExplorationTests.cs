using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Soulcrest.Ocr;
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
    public void MatchingHandlesEntranceSuffixAndGermanAccentsButRejectsAmbiguity()
    {
        var p = Place("a", "Hidden Vein Cave Entrance", "Eingang zur Versteckten Erzaderhöhle");
        Assert.Equal(p, ExplorationService.Match("Hidden Vein Cave", [p]));
        Assert.Equal(p, ExplorationService.Match("Versteckten Erzaderhöhle", [p]));
        Assert.Null(ExplorationService.Match("Unknown Cave", [p]));
        Assert.Null(ExplorationService.Match("Hidden Vein Cave", [p, p with { Id = "other" }]));
    }
    [Fact]
    public void StrongholdsUseTheirOwnMapAndCategoryCatalog()
    {
        var service = new ExplorationService(new ProgressService());
        var candidates = service.Places.Where(p => p.Map == "altgard" && p.Kind == "stronghold").ToArray();
        Assert.Equal(15, candidates.Length);
        foreach (var p in candidates) Assert.Equal(p.Id, ExplorationService.Match(p.En, candidates)?.Id);
        Assert.Null(ExplorationService.Match("Hidden Vein Cave", candidates));
    }
    [Fact]
    public void UndiscoveredStopsBeforeUnknownRowsAndNeverMarksLaterNames()
    {
        var places = new[] { Place("a", "Hidden Vein Cave Entrance"), Place("b", "Old Graveyard Entrance") };
        var page = ExplorationScanService.ReadPage([
            new OcrLine("Hidden Vein Cave", 0, 10, 100, 20),
            new OcrLine("Undiscovered", 0, 40, 100, 20),
            new OcrLine("Old Graveyard", 0, 70, 100, 20)], places);
        Assert.True(page.End); Assert.Equal(new[] { "a" }, page.Ids);
    }
    [Fact]
    public void SingleFrameCannotMarkProgressOrStopTheScan()
    {
        var c = new ExplorationScanService.Confirmation();
        Assert.Empty(c.Observe(new(["a"], [], true)));
        Assert.False(c.Finished);
        Assert.Empty(c.Observe(new(["b"], [], false)));
        Assert.False(c.Finished);
        Assert.Equal(new[] { "b" }, c.Observe(new(["b"], [], true)));
        Assert.False(c.Finished);
        Assert.Equal(new[] { "b" }, c.Observe(new(["b"], [], true)));
        Assert.True(c.Finished);
    }
    [Fact]
    public async Task SuppliedScreenshotCanBeReadWithoutGameCapture()
    {
        var path = Environment.GetEnvironmentVariable("SOULCREST_EXPLORATION_SCREENSHOT");
        if (string.IsNullOrEmpty(path)) return;
        using var full = new Bitmap(path);
        using var list = full.Clone(new Rectangle((int)(full.Width * .015), (int)(full.Height * .215), (int)(full.Width * .145), (int)(full.Height * .76)), full.PixelFormat);
        var lines = await WindowsOcrLineReader.Create("auto").ReadAsync(list);
        var service = new ExplorationService(new ProgressService());
        var page = ExplorationScanService.ReadPage(lines, service.Places.Where(p => p.Map == "altgard" && p.Kind == "dungeon").ToArray());
        File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "exploration-screenshot.txt"), string.Join("\n", lines.Select(l => l.Text)) + "\nMatched: " + string.Join(";", page.Ids) + "\nUnknown: " + string.Join(";", page.Unmatched));
        Assert.True(page.Ids.Count == (path.Contains("57b7d0e9") ? 9 : 14), $"Matched {page.Ids.Count}: " + string.Join(";", page.Unmatched));
        if (path.Contains("57b7d0e9")) Assert.True(page.End);
    }
}
