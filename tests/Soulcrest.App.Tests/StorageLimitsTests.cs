using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Logs and map reference caches must not grow without end (user question 2026-10-07).</summary>
public sealed class StorageLimitsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-storage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AFullLogBecomesTheOldLogAndStartsAgain()
    {
        LogFile.Append(_directory, "map-tracking.log", new string('a', 60), 100);
        LogFile.Append(_directory, "map-tracking.log", new string('b', 60), 100); // 120 bytes: full now
        LogFile.Append(_directory, "map-tracking.log", "c", 100);
        Assert.Equal("c", File.ReadAllText(Path.Combine(_directory, "map-tracking.log")));
        Assert.Equal(120, new FileInfo(Path.Combine(_directory, "map-tracking.old.log")).Length);
        LogFile.Append(_directory, "map-tracking.log", new string('d', 120), 100);
        LogFile.Append(_directory, "map-tracking.log", "e", 100); // the old log is replaced, never a third file
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void OnlyTheCurrentAndTheNewestOtherMapDataVersionStay()
    {
        Directory.CreateDirectory(_directory);
        var age = 0;
        foreach (var name in new[] { "altgard-z5-20261003003919", "altgard-z4-20261003003919", "abyss-reshanta-a-z4-20261004070916",
                     "altgard-z5-20261005223736", "abyss-reshanta-a-z4-20261005223736", "altgard-z5-20261006142014" })
        {
            var path = Path.Combine(_directory, name + ".bin");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10 + age++));
        }
        MapTrackingService.PruneReferences(_directory, "20261006142014");
        Assert.Equal(
            ["abyss-reshanta-a-z4-20261005223736.bin", "altgard-z5-20261005223736.bin", "altgard-z5-20261006142014.bin"],
            Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
