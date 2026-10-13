using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkDungeonCompletionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);
    private static readonly ExplorationPlace[] Places =
    [
        new("drana", "altgard", "dungeon", 0, "Synthetic Chamber Entrance", null, QuestId: 2810027, DungeonId: 320027),
        new("drana-side", "altgard", "dungeon", 1, "Synthetic Chamber Side Entrance", null, QuestId: 2810027, DungeonId: 320027),
        new("other", "altgard", "dungeon", 2, "Synthetic Other Entrance", null, QuestId: 2810028, DungeonId: 320028),
        // An accidental numeric collision in another category must never become a dungeon completion.
        new("stronghold", "altgard", "stronghold", 3, "Synthetic Fort", null, QuestId: 2820001, DungeonId: 320027),
        new("teleport", "altgard", "kibelisk", 4, "Synthetic Pillar", null, TeleportId: 1052043, DungeonId: 320027),
    ];
    private static NetworkProgressContext Context(long sequence, long generation = 1, int server = 501, long character = 101) =>
        new(new(character, server, 1001, "Synthetic Alpha"), generation, sequence, At.AddSeconds(sequence));
    private static DungeonCompletion Result(int id = 320027, bool succeeded = true) => new(id, succeeded, 42,
        new Dictionary<int, long> { [700001] = 2 }, 1);
    private static QuestProgressSnapshot Quests(byte status = 2) => new([new(2810027, status, 1, [1, 0, 0, 0, 0, 0], 160)]);

    private sealed class Folder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-dungeon-completion-" + Guid.NewGuid().ToString("N"));
        internal string File { get; }
        internal Folder() { Directory.CreateDirectory(_root); File = Path.Combine(_root, "exploration.json"); }
        internal ExplorationService Service() => new(File, Places);
        internal ExplorationProfiles Read() => JsonSerializer.Deserialize<ExplorationProfiles>(System.IO.File.ReadAllText(File))!;
        internal void Save(ExplorationProfiles state) => System.IO.File.WriteAllText(File, JsonSerializer.Serialize(state));
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void SuccessCompletesEveryExactDungeonPlacementAndPreservesQuestRawData()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkQuests(Quests(), Context(2)));
        var before = service.Snapshot().Progress["drana"];
        var changes = 0;
        service.Changed += () => changes++;

        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(3)));
        var snapshot = service.Snapshot();
        Assert.Equal(new[] { "drana", "drana-side" }, snapshot.Completed.Order());
        foreach (var id in new[] { "drana", "drana-side" })
        {
            var entry = snapshot.Progress[id];
            Assert.Equal(ExplorationProgressState.DiscoveredCompleted, entry.State);
            Assert.Equal(ProgressFreshness.Fresh, entry.Freshness);
            Assert.Equal("network-dungeon-completion", entry.Source);
            Assert.Equal(Context(3).At, entry.ObservedAt);
            Assert.False(entry.ManualOverride);
            Assert.Equal(before.QuestStatus, entry.QuestStatus);
            Assert.Equal(before.QuestStage, entry.QuestStage);
            Assert.Equal(before.QuestCounters, entry.QuestCounters);
            Assert.Equal(before.QuestOrdinal, entry.QuestOrdinal);
            Assert.Equal(before.RawSource, entry.RawSource);
            Assert.Equal(before.RawObservedAt, entry.RawObservedAt);
            Assert.Equal(before.RawFreshness, entry.RawFreshness);
        }
        Assert.Null(snapshot.Progress["other"].State);
        Assert.Null(snapshot.Progress["stronghold"].State);
        Assert.Null(snapshot.Progress["teleport"].State);
        Assert.Equal(1, changes);
        var saved = folder.Read().Characters.Single(c => c.Id == snapshot.CharacterId);
        Assert.Equal(Context(3).At, saved.DungeonCompletedAt[320027]);
        Assert.Equal((byte)2, saved.RawQuests[2810027].Status);
        Assert.DoesNotContain("Rewards", System.IO.File.ReadAllText(folder.File));
        Assert.DoesNotContain("ClearLevel", System.IO.File.ReadAllText(folder.File));
    }

    [Fact]
    public void LegacyProfileGuidAndExactLabelSurviveBindingCompletionAndReload()
    {
        using var folder = new Folder();
        const string id = "0123456789abcdef0123456789abcdef";
        const string label = "sYnThEtIc AlPhA";
        folder.Save(new() { Active = id, Characters = [new() { Id = id, Name = label, Done = ["other"] }] });
        var service = folder.Service();
        Assert.Equal(CharacterChoice.Adopted, service.BindNetworkContext(Context(1)));
        Assert.Equal(id, service.ActiveId);
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));

        var reloaded = folder.Service();
        Assert.Equal(id, reloaded.ActiveId);
        Assert.Equal(label, Assert.Single(reloaded.Characters).Name);
        Assert.Equal(new[] { "drana", "drana-side", "other" }, reloaded.Completed.Order());
        Assert.Equal(ProgressFreshness.Stale, reloaded.Snapshot().Progress["drana"].Freshness);
        var saved = Assert.Single(folder.Read().Characters);
        Assert.Equal(Context(2).At, saved.DungeonCompletedAt[320027]);
        Assert.Equal(101L, saved.NetworkCharacterId);
        Assert.Equal(501, saved.ServerId);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(folder.File)!, "exploration.json.pre-network-*.bak"));
    }

    [Fact]
    public void CompletionWritesTheBoundProfileWhileAnotherProfileIsSelected()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        var bound = service.ActiveId;
        service.Add("Synthetic Beta");
        var visible = service.ActiveId;
        service.SetState(visible, ["other"], ExplorationProgressState.DiscoveredIncomplete);
        var visibleBefore = service.Snapshot();

        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Assert.Equal(visible, service.ActiveId);
        Assert.Equal(visibleBefore.Progress["other"], service.Snapshot().Progress["other"]);
        Assert.Null(service.Snapshot().Progress["drana"].State);
        service.Select(bound);
        Assert.Contains("drana", service.Completed);
        var saved = folder.Read();
        Assert.Contains(320027, saved.Characters.Single(c => c.Id == bound).DungeonCompletedAt.Keys);
        Assert.Empty(saved.Characters.Single(c => c.Id == visible).DungeonCompletedAt);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("wrong-server")]
    [InlineData("wrong-character")]
    [InlineData("old-generation")]
    [InlineData("future-generation")]
    [InlineData("before-binding")]
    [InlineData("invalidated")]
    public void UntrustedContextsCannotWriteCompletion(string variant)
    {
        using var folder = new Folder();
        var service = folder.Service();
        if (variant != "unbound") service.BindNetworkContext(Context(10, generation: 2));
        if (variant == "invalidated") service.MarkNetworkStale();
        var candidate = variant switch
        {
            "wrong-server" => Context(11, 2, server: 502),
            "wrong-character" => Context(11, 2, character: 102),
            "old-generation" => Context(11),
            "future-generation" => Context(11, 3),
            "before-binding" => Context(9, 2),
            _ => Context(11, 2),
        };
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(), candidate));
        Assert.Empty(service.Completed);
        Assert.Null(service.Snapshot().Progress["drana"].State);
        if (System.IO.File.Exists(folder.File)) Assert.All(folder.Read().Characters, c => Assert.Empty(c.DungeonCompletedAt));
    }

    [Fact]
    public void FailedAndUnmappedResultsDoNotConsumeTheValidEventAndSequenceDeduplicatesPerSource()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(succeeded: false), Context(2)));
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(id: 999999), Context(2)));
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(id: 0), Context(2)));
        Assert.Empty(Assert.Single(folder.Read().Characters).DungeonCompletedAt);
        Assert.True(service.ApplyNetworkQuests(Quests(), Context(20)));
        var changes = 0;
        service.Changed += () => changes++;
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(), Context(1)));
        Assert.Equal(1, changes);
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(3)));
        Assert.Equal(Context(2).At, Assert.Single(folder.Read().Characters).DungeonCompletedAt[320027]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExplorationProgressState.Undiscovered)]
    [InlineData(ExplorationProgressState.DiscoveredIncomplete)]
    [InlineData(ExplorationProgressState.DiscoveredCompleted)]
    public void ManualStatesRemainProtectedButThePositiveCompletionEvidenceSurvivesReload(ExplorationProgressState? manualState)
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        service.SetState(service.ActiveId, ["drana", "drana-side"], manualState);
        Assert.True(service.ApplyNetworkQuests(Quests(), Context(2)));
        var before = service.Snapshot().Progress["drana"];
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(3)));
        AssertManualStatePreserved(before, service.Snapshot().Progress["drana"]);
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(), Context(3)));
        var reloaded = folder.Service();
        AssertManualStatePreserved(before, reloaded.Snapshot().Progress["drana"]);
        Assert.Equal(ProgressFreshness.Stale, reloaded.Snapshot().Progress["drana"].RawFreshness);
        Assert.Equal(Context(3).At, Assert.Single(folder.Read().Characters).DungeonCompletedAt[320027]);
        Assert.Equal(manualState == ExplorationProgressState.DiscoveredCompleted, reloaded.Completed.Contains("drana"));
    }

    [Fact]
    public void LaterQuestAbsenceFailureOrSessionChangesNeverEraseAConfirmedOneTimeCompletion()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Assert.True(service.ApplyNetworkQuests(Quests(4), Context(3)));
        Assert.True(service.ApplyNetworkQuests(new([]), Context(4)));
        service.MarkNetworkQuestSourceStale();
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(succeeded: false), Context(5)));
        Assert.Contains("drana", service.Completed);
        Assert.Equal("network-dungeon-completion", service.Snapshot().Progress["drana"].Source);
        Assert.Equal(ProgressFreshness.Fresh, service.Snapshot().Progress["drana"].Freshness);
        service.MarkNetworkStale();
        Assert.Contains("drana", service.Completed);
        Assert.Equal(ProgressFreshness.Stale, service.Snapshot().Progress["drana"].Freshness);
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(), Context(6)));
        service.BindNetworkContext(Context(1, generation: 2));
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(), Context(7)));
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(2, generation: 2)));
        Assert.Equal(ProgressFreshness.Fresh, service.Snapshot().Progress["drana"].Freshness);
        Assert.Equal(Context(2).At, Assert.Single(folder.Read().Characters).DungeonCompletedAt[320027]);
    }

    private static void AssertManualStatePreserved(ExplorationPlaceProgress before, ExplorationPlaceProgress after)
    {
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Freshness, after.Freshness);
        Assert.Equal(before.Source, after.Source);
        Assert.Equal(before.ObservedAt, after.ObservedAt);
        Assert.True(after.ManualOverride);
        Assert.Equal(before.QuestStatus, after.QuestStatus);
        Assert.Equal(before.QuestStage, after.QuestStage);
        Assert.Equal(before.QuestCounters!.ToArray(), after.QuestCounters!.ToArray());
        Assert.Equal(before.QuestOrdinal, after.QuestOrdinal);
        Assert.Equal(before.RawSource, after.RawSource);
        Assert.Equal(before.RawObservedAt, after.RawObservedAt);
    }

    [Fact]
    public void StorageFailureKeepsInMemoryCompletionDedupeAndTheNextValidCompletionUsable()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        System.IO.File.Delete(folder.File);
        Directory.CreateDirectory(folder.File);
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Assert.Contains("drana", service.Completed);
        Assert.False(service.ApplyNetworkDungeonCompletion(Result(), Context(2)));
        Directory.Delete(folder.File);
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(id: 320028), Context(3)));
        Assert.Contains("other", service.Completed);
        var saved = Assert.Single(folder.Read().Characters);
        Assert.Equal(Context(2).At, saved.DungeonCompletedAt[320027]);
        Assert.Equal(Context(3).At, saved.DungeonCompletedAt[320028]);
    }
}
