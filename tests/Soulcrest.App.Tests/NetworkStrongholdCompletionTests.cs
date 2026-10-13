using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkStrongholdCompletionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 2, 0, 0, TimeSpan.Zero);
    private static readonly ExplorationPlace[] Places =
    [
        new("latrima", "altgard", "stronghold", 0, "Synthetic Outpost", null, QuestId: 2820012, StrongholdInstanceId: 142017),
        new("latrima-side", "altgard", "stronghold", 1, "Synthetic Outpost Side", null, QuestId: 2820012, StrongholdInstanceId: 142017),
        new("other", "altgard", "stronghold", 2, "Synthetic Other Outpost", null, StrongholdInstanceId: 142018),
        new("wrong-stronghold", "altgard", "stronghold", 3, "Synthetic Wrong Field", null, DungeonId: 142017),
        new("wrong-dungeon", "altgard", "dungeon", 4, "Synthetic Wrong Category", null, StrongholdInstanceId: 142017),
        new("wrong-teleport", "altgard", "kibelisk", 5, "Synthetic Pillar", null, StrongholdInstanceId: 142017),
        new("dungeon", "altgard", "dungeon", 6, "Synthetic Chamber", null, DungeonId: 320027),
        new("dungeon-collision", "altgard", "stronghold", 7, "Synthetic Numeric Collision", null, DungeonId: 320027),
    ];
    private static NetworkProgressContext Context(long sequence, long generation = 1, int server = 501, long character = 101) =>
        new(new(character, server, 1001, "Synthetic Alpha"), generation, sequence, At.AddSeconds(sequence));
    private static DungeonCompletion Result(int instance = 142017, bool succeeded = true) => new(instance, succeeded, null,
        new Dictionary<int, long>(), null);
    private static QuestProgressSnapshot Quests(byte status = 2) => new([new(2820012, status, 1, [1, 0, 0, 0, 0, 0], 12)]);

    private sealed class Folder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-stronghold-completion-" + Guid.NewGuid().ToString("N"));
        internal string File { get; }
        internal Folder() { Directory.CreateDirectory(_root); File = Path.Combine(_root, "exploration.json"); }
        internal ExplorationService Service() => new(File, Places);
        internal ExplorationProfiles Read() => JsonSerializer.Deserialize<ExplorationProfiles>(System.IO.File.ReadAllText(File))!;
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void SuccessMapsEveryKnownStrongholdPlacementWithoutCrossingCategoryOrIdFields()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkQuests(Quests(), Context(2)));
        var rawBefore = service.Snapshot().Progress["latrima"];
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(3)));
        var snapshot = service.Snapshot();
        Assert.Equal(new[] { "latrima", "latrima-side" }, snapshot.Completed.Order());
        foreach (var id in new[] { "latrima", "latrima-side" })
        {
            var entry = snapshot.Progress[id];
            Assert.Equal(ExplorationProgressState.DiscoveredCompleted, entry.State);
            Assert.Equal("network-stronghold-completion", entry.Source);
            Assert.Equal(ProgressFreshness.Fresh, entry.Freshness);
            Assert.Equal(Context(3).At, entry.ObservedAt);
            Assert.False(entry.ManualOverride);
            Assert.Equal(rawBefore.QuestStatus, entry.QuestStatus);
            Assert.Equal(rawBefore.QuestStage, entry.QuestStage);
            Assert.Equal(rawBefore.QuestCounters!.ToArray(), entry.QuestCounters!.ToArray());
            Assert.Equal(rawBefore.QuestOrdinal, entry.QuestOrdinal);
            Assert.Equal(rawBefore.RawSource, entry.RawSource);
            Assert.Equal(rawBefore.RawObservedAt, entry.RawObservedAt);
            Assert.Equal(rawBefore.RawFreshness, entry.RawFreshness);
        }
        foreach (var id in new[] { "other", "wrong-stronghold", "wrong-dungeon", "wrong-teleport", "dungeon", "dungeon-collision" })
            Assert.Null(snapshot.Progress[id].State);
        var saved = Assert.Single(folder.Read().Characters);
        Assert.Equal(Context(3).At, saved.DungeonCompletedAt[142017]);
        Assert.Equal((byte)2, saved.RawQuests[2820012].Status);
    }

    [Fact]
    public void BoundCharacterKeepsItsGuidAndRenamedLabelWhenAnotherUiProfileIsSelected()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        var bound = service.ActiveId;
        const string label = "Synthetic Custom Label";
        service.Rename(label);
        service.Add("Synthetic Beta");
        var selected = service.ActiveId;
        service.SetState(selected, ["other"], ExplorationProgressState.DiscoveredIncomplete);

        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Assert.Equal(selected, service.ActiveId);
        Assert.Null(service.Snapshot().Progress["latrima"].State);
        var saved = folder.Read();
        Assert.Equal(label, saved.Characters.Single(c => c.Id == bound).Name);
        Assert.Equal(Context(2).At, saved.Characters.Single(c => c.Id == bound).DungeonCompletedAt[142017]);
        Assert.Empty(saved.Characters.Single(c => c.Id == selected).DungeonCompletedAt);
        service.BindNetworkContext(Context(1, generation: 2));
        Assert.Equal(bound, service.ActiveId);
        var reloaded = folder.Service();
        Assert.Equal(bound, reloaded.ActiveId);
        Assert.Equal(label, reloaded.Characters.Single(c => c.Id == bound).Name);
        Assert.Contains("latrima", reloaded.Completed);
        Assert.Equal(2, folder.Read().SchemaVersion);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("wrong-server")]
    [InlineData("wrong-character")]
    [InlineData("old-generation")]
    [InlineData("future-generation")]
    [InlineData("invalidated")]
    public void CompletionRequiresTheCurrentOwnCharacterContext(string variant)
    {
        using var folder = new Folder();
        var service = folder.Service();
        if (variant != "unbound") service.BindNetworkContext(Context(1, generation: 2));
        if (variant == "invalidated") service.MarkNetworkStale();
        var context = variant switch
        {
            "wrong-server" => Context(2, 2, server: 502),
            "wrong-character" => Context(2, 2, character: 102),
            "old-generation" => Context(2),
            "future-generation" => Context(2, 3),
            _ => Context(2, 2),
        };
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(), context));
        Assert.Empty(service.Completed);
        if (System.IO.File.Exists(folder.File)) Assert.All(folder.Read().Characters, c => Assert.Empty(c.DungeonCompletedAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExplorationProgressState.Undiscovered)]
    [InlineData(ExplorationProgressState.DiscoveredIncomplete)]
    [InlineData(ExplorationProgressState.DiscoveredCompleted)]
    public void ManualStateRemainsProtectedWhileTheIndependentSuccessProofPersists(ExplorationProgressState? manualState)
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        service.SetState(service.ActiveId, ["latrima"], manualState);
        var manual = service.Snapshot().Progress["latrima"];
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Assert.Equal(manual, service.Snapshot().Progress["latrima"]);
        Assert.Contains("latrima-side", service.Completed);
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        var reloaded = folder.Service();
        Assert.Equal(manual, reloaded.Snapshot().Progress["latrima"]);
        Assert.Equal(manualState == ExplorationProgressState.DiscoveredCompleted, reloaded.Completed.Contains("latrima"));
        Assert.Equal(Context(2).At, Assert.Single(folder.Read().Characters).DungeonCompletedAt[142017]);
    }

    [Fact]
    public void LaterQuestListsExitAndReloadNeverEraseTheStrongholdCompletion()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Assert.True(service.ApplyNetworkQuests(Quests(4), Context(3)));
        Assert.True(service.ApplyNetworkQuests(new([]), Context(4)));
        service.MarkNetworkQuestSourceStale();
        Assert.Contains("latrima", service.Completed);
        Assert.Equal("network-stronghold-completion", service.Snapshot().Progress["latrima"].Source);
        Assert.Equal(ProgressFreshness.Fresh, service.Snapshot().Progress["latrima"].Freshness);
        service.MarkNetworkStale();
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(instance: 142018), Context(5)));
        var reloaded = folder.Service();
        Assert.Contains("latrima", reloaded.Completed);
        Assert.DoesNotContain("other", reloaded.Completed);
        Assert.Equal(ProgressFreshness.Stale, reloaded.Snapshot().Progress["latrima"].Freshness);
        Assert.Equal(Context(2).At, Assert.Single(folder.Read().Characters).DungeonCompletedAt[142017]);
    }

    [Fact]
    public void FailedAndUnmappedResultsCannotCreateProofOrConsumeTheFollowingValidSequence()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(succeeded: false), Context(2)));
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(instance: 999999), Context(2)));
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(instance: 0), Context(2)));
        Assert.Empty(service.Completed);
        Assert.Empty(Assert.Single(folder.Read().Characters).DungeonCompletedAt);
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Assert.Contains("latrima", service.Completed);
    }

    [Fact]
    public void ASealedDungeonResultStillCannotCompleteAStrongholdWithOnlyACollidingDungeonId()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(instance: 320027), Context(2)));
        Assert.Equal(new[] { "dungeon" }, service.Completed);
        Assert.Equal("network-dungeon-completion", service.Snapshot().Progress["dungeon"].Source);
        Assert.Null(service.Snapshot().Progress["dungeon-collision"].State);
        Assert.DoesNotContain(142017, Assert.Single(folder.Read().Characters).DungeonCompletedAt.Keys);
    }
}
