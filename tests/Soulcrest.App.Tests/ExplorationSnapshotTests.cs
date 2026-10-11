using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationSnapshotTests
{
    private static readonly ExplorationPlace[] Places =
        [new("library", "altgard", "dungeon", 0, "Library of Foresight", null),
            new("vein", "altgard", "dungeon", 1, "Hidden Vein Cave", null)];
    private static string PathForTest() => Path.Combine(Path.GetTempPath(), "soulcrest-exploration-snapshot-" + Guid.NewGuid().ToString("N"), "exploration.json");
    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }

    [Fact]
    public void ReadingAFreshSnapshotDoesNotCreateADataFileOrRaiseChangeEvents()
    {
        var path = PathForTest();
        try
        {
            var service = new ExplorationService(path, Places);
            var events = 0;
            service.Changed += () => events++;
            var snapshot = service.Snapshot();
            Assert.Equal(service.ActiveId, snapshot.CharacterId);
            Assert.Empty(snapshot.Completed);
            Assert.False(File.Exists(path));
            Assert.Equal(0, events);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SnapshotIsDetachedFromLaterEditsAndCannotMutateTheService()
    {
        var path = PathForTest();
        try
        {
            var service = new ExplorationService(path, Places);
            var character = service.ActiveId;
            service.SetDone(character, ["library"]);
            var snapshot = service.Snapshot();
            service.SetDone(character, ["vein"]);
            Assert.Equal(["library"], snapshot.Completed);
            Assert.Equal(new[] { "library", "vein" }, service.Snapshot().Completed.Order());
            if (snapshot.Completed is ISet<string> mutableCopy) mutableCopy.Clear();
            Assert.Equal(new[] { "library", "vein" }, service.Completed.Order());
            var loaded = new ExplorationService(path, Places);
            Assert.Equal(new[] { "library", "vein" }, loaded.Snapshot().Completed.Order());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CharacterSwitchKeepsOldSnapshotsBoundToTheirOwnProgress()
    {
        var path = PathForTest();
        try
        {
            var service = new ExplorationService(path, Places);
            var first = service.ActiveId;
            service.SetDone(first, ["library"]);
            var oldSnapshot = service.Snapshot();
            service.Add("Synthetic second test profile");
            var second = service.ActiveId;
            service.SetDone(second, ["vein"]);
            var current = service.Snapshot();
            Assert.Equal(first, oldSnapshot.CharacterId);
            Assert.Equal(["library"], oldSnapshot.Completed);
            Assert.Equal(second, current.CharacterId);
            Assert.Equal(["vein"], current.Completed);
            service.Select(first);
            Assert.Equal(first, service.Snapshot().CharacterId);
            Assert.Equal(["library"], service.Snapshot().Completed);
            Assert.Equal(["vein"], current.Completed);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RepeatedSnapshotsDoNotRewritePersistedProgress()
    {
        var path = PathForTest();
        try
        {
            var service = new ExplorationService(path, Places);
            service.SetDone(service.ActiveId, ["library"]);
            var before = File.ReadAllBytes(path);
            var writtenAt = File.GetLastWriteTimeUtc(path);
            var events = 0;
            service.Changed += () => events++;
            for (var index = 0; index < 20; index++) Assert.Equal(["library"], service.Snapshot().Completed);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(path));
            Assert.Equal(0, events);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ConcurrentProfileSwitchesNeverMixSnapshotCharacterAndProgress()
    {
        var path = PathForTest();
        try
        {
            var service = new ExplorationService(path, Places);
            var first = service.ActiveId;
            service.SetDone(first, ["library"]);
            service.Add("Synthetic second test profile");
            var second = service.ActiveId;
            service.SetDone(second, ["vein"]);
            using var readerStarted = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var reads = 0;
            var reader = Task.Run(() =>
            {
                readerStarted.Set();
                do
                {
                    var snapshot = service.Snapshot();
                    Assert.True(snapshot.CharacterId == first || snapshot.CharacterId == second);
                    Assert.Equal([snapshot.CharacterId == first ? "library" : "vein"], snapshot.Completed);
                    reads++;
                    Thread.Yield();
                } while (!finished.IsSet);
            });
            var writer = Task.Run(() =>
            {
                readerStarted.Wait();
                try
                {
                    for (var index = 0; index < 64; index++) service.Select(index % 2 == 0 ? first : second);
                }
                finally { finished.Set(); }
            });
            await Task.WhenAll(reader, writer);
            Assert.True(reads > 0);
        }
        finally { Cleanup(path); }
    }
}
