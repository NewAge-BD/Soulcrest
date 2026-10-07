using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// User data 2026-10-03: the panel OCR had learned "Dracunj Herbalist" (catalog: Dracuni Herbalist) and
/// "Red Spark Jgnus" (Ignus) as new pets, with progress applied to them. Start-up repairs both.
/// </summary>
public sealed class LearnedNameRepairTests
{
    [Fact]
    public void MisreadLearnedPetsMoveToTheRightPet()
    {
        var progressBefore = new ProgressService();
        if (progressBefore.MapDataDirectory is null || progressBefore.Catalog.Find("dracuni-herbalist") is null)
            return;
        var learnedFile = AppPaths.LearnedPetsFile;
        var portraits = AppPaths.LearnedPortraitsDirectory;
        Directory.CreateDirectory(portraits);
        var keptLearned = File.Exists(learnedFile) ? File.ReadAllText(learnedFile) : null;
        var keptProgress = File.Exists(AppPaths.ProgressFile) ? File.ReadAllText(AppPaths.ProgressFile) : null;
        try
        {
            File.WriteAllText(learnedFile, """
                [ { "id": "dracunj-herbalist", "en": "Dracunj Herbalist", "genus": "unknown" },
                  { "id": "red-spark-jgnus", "en": "Red Spark Jgnus", "genus": "unknown" } ]
                """);
            File.WriteAllText(AppPaths.ProgressFile, """
                { "server": "Standard", "pets": { "dracunj-herbalist": { "souls": 4 }, "red-spark-jgnus": { "souls": 1 } } }
                """);
            File.WriteAllBytes(Path.Combine(portraits, "dracunj-herbalist.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(portraits, "red-spark-jgnus.png"), [4, 5, 6]);

            var progress = new ProgressService();

            Assert.Equal(4, progress.Souls("dracuni-herbalist"));
            Assert.Equal(1, progress.Souls("red-spark-ignus"));
            Assert.Equal(0, progress.Souls("dracunj-herbalist"));
            Assert.Null(progress.Catalog.Find("dracunj-herbalist"));
            Assert.Null(progress.Catalog.Find("red-spark-jgnus"));
            Assert.Equal("Red Spark Ignus", progress.Catalog.Find("red-spark-ignus")?.En);
            Assert.True(File.Exists(Path.Combine(portraits, "red-spark-ignus.png")));
            Assert.False(File.Exists(Path.Combine(portraits, "red-spark-jgnus.png")));
            var learned = JsonDocument.Parse(File.ReadAllText(learnedFile)).RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
            // Red Spark Ignus now has a sourced catalog entry; neither repair needs a learned duplicate.
            Assert.Empty(learned);
        }
        finally
        {
            foreach (var file in new[] { "dracunj-herbalist.png", "red-spark-jgnus.png", "red-spark-ignus.png", "dracuni-herbalist.png" })
                File.Delete(Path.Combine(portraits, file));
            if (keptLearned is null) File.Delete(learnedFile); else File.WriteAllText(learnedFile, keptLearned);
            if (keptProgress is null) File.Delete(AppPaths.ProgressFile); else File.WriteAllText(AppPaths.ProgressFile, keptProgress);
        }
    }
}
