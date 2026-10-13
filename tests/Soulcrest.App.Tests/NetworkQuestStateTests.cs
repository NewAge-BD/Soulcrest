using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkQuestStateTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 3, 0, 0, TimeSpan.Zero);
    private static readonly ExplorationPlace[] Places =
    [
        new("fort", "altgard", "stronghold", 0, "Synthetic Fort", null, QuestId: 2820006, StrongholdInstanceId: 142005),
        new("fort-side", "altgard", "stronghold", 1, "Synthetic Fort Side", null, QuestId: 2820006, StrongholdInstanceId: 142005),
        new("cave", "altgard", "dungeon", 2, "Synthetic Cave", null, QuestId: 2810034, DungeonId: 320034),
        new("other", "altgard", "stronghold", 3, "Synthetic Other", null, QuestId: 2820004, StrongholdInstanceId: 142003),
        new("pillar", "altgard", "kibelisk", 4, "Synthetic Pillar", null, QuestId: 2820006, TeleportId: 1052020),
        new("wrong-kind", "altgard", "quest", 5, "Synthetic Other Kind", null, QuestId: 2820006),
        new("wrong-field", "altgard", "dungeon", 6, "Synthetic Wrong Field", null, DungeonId: 2820006),
    ];

    private static NetworkProgressContext Context(long sequence, long generation = 1, int server = 501, long character = 101) =>
        new(new(character, server, 1001, "Synthetic Alpha"), generation, sequence, At.AddSeconds(sequence));
    private static QuestProgressRecord Complete(int id = 2820006, long ordinal = 80) => new(id, 4, 1, [1, 0, 0, 0, 0, 0], ordinal);
    private static QuestProgressRecord Open(int id = 2820006) => new(id, 2, 1, [0, 0, 0, 0, 0, 0], 80);
    private static QuestProgressSnapshot List(params QuestProgressRecord[] quests) => new(quests);
    private static DungeonCompletion Result(int id) => new(id, true, null, new Dictionary<int, long>(), null);

    private sealed class Folder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-quest-states-" + Guid.NewGuid().ToString("N"));
        internal string File { get; }
        internal Folder() { Directory.CreateDirectory(_root); File = Path.Combine(_root, "exploration.json"); }
        internal ExplorationService Service() => new(File, Places);
        internal ExplorationProfiles Read() => JsonSerializer.Deserialize<ExplorationProfiles>(System.IO.File.ReadAllText(File))!;
        internal void Write(ExplorationProfiles state) => System.IO.File.WriteAllText(File, JsonSerializer.Serialize(state));
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void FreshPositiveBaselineCompletesEveryExactDungeonAndStrongholdPlacementAtomically()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        var events = 0;
        service.Changed += () =>
        {
            events++;
            Assert.Equal(new[] { "cave", "fort", "fort-side" }, service.Snapshot().Completed.Order());
        };

        Assert.True(service.ApplyNetworkQuests(List(Complete(), Complete(2810034), Complete(999999)), Context(2)));
        Assert.Equal(1, events);
        foreach (var id in new[] { "fort", "fort-side", "cave" })
        {
            var entry = service.Snapshot().Progress[id];
            Assert.Equal(ExplorationProgressState.DiscoveredCompleted, entry.State);
            Assert.Equal(ProgressFreshness.Fresh, entry.Freshness);
            Assert.Equal("network-quest-baseline", entry.Source);
            Assert.Equal(Context(2).At, entry.ObservedAt);
            Assert.Equal(ProgressFreshness.Fresh, entry.RawFreshness);
            Assert.Equal(Context(2).At, entry.RawObservedAt);
        }
        foreach (var id in new[] { "other", "pillar", "wrong-kind", "wrong-field" })
            Assert.Null(service.Snapshot().Progress[id].State);
        var saved = Assert.Single(folder.Read().Characters);
        Assert.Equal(3, saved.RawQuests.Count);
        Assert.Empty(saved.DungeonCompletedAt); // A quest baseline is not a direct 0x6101 result.
    }

    [Fact]
    public void RejectedQuestSourceStalesOnlyListBasedCompletionAndRetainsAllHistory()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        service.SetDone(service.ActiveId, ["other"]);
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(320034), Context(2)));
        Assert.True(service.ApplyNetworkQuests(List(Complete(), Complete(2810034), Complete(2820004)), Context(3)));
        var before = service.Snapshot();

        service.MarkNetworkQuestSourceStale();

        var after = service.Snapshot();
        Assert.Equal(before.Completed.Order(), after.Completed.Order());
        Assert.Equal(ProgressFreshness.Stale, after.Progress["fort"].Freshness);
        Assert.Equal(ProgressFreshness.Stale, after.Progress["fort"].RawFreshness);
        Assert.Equal(ProgressFreshness.Fresh, after.Progress["cave"].Freshness);
        Assert.Equal(ProgressFreshness.Stale, after.Progress["cave"].RawFreshness);
        Assert.Equal(before.Progress["other"].Freshness, after.Progress["other"].Freshness);
        foreach (var id in new[] { "fort", "cave", "other" })
        {
            Assert.Equal(before.Progress[id].State, after.Progress[id].State);
            Assert.Equal(before.Progress[id].Source, after.Progress[id].Source);
            Assert.Equal(before.Progress[id].ObservedAt, after.Progress[id].ObservedAt);
            Assert.Equal(before.Progress[id].ManualOverride, after.Progress[id].ManualOverride);
        }
        Assert.Equal(ProgressFreshness.Stale, Assert.Single(folder.Read().Characters).Progress["fort"].Freshness);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("status")]
    [InlineData("stage-zero")]
    [InlineData("stage-two")]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("first-zero")]
    [InlineData("first-two")]
    [InlineData("negative-counter")]
    [InlineData("max-counter")]
    [InlineData("later-counter")]
    [InlineData("negative-ordinal")]
    public void OtherRecordShapesRemainRawWithoutInferringDiscoveryOrCompletion(string variant)
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        var record = variant switch
        {
            "open" => Open(),
            "status" => Complete() with { Status = 3 },
            "stage-zero" => Complete() with { Stage = 0 },
            "stage-two" => Complete() with { Stage = 2 },
            "empty" => Complete() with { Counters = [] },
            "short" => Complete() with { Counters = [1, 0, 0, 0, 0] },
            "long" => Complete() with { Counters = [1, 0, 0, 0, 0, 0, 0] },
            "first-zero" => Complete() with { Counters = [0, 0, 0, 0, 0, 0] },
            "first-two" => Complete() with { Counters = [2, 0, 0, 0, 0, 0] },
            "negative-counter" => Complete() with { Counters = [1, 0, -1, 0, 0, 0] },
            "max-counter" => Complete() with { Counters = [int.MaxValue, 0, 0, 0, 0, 0] },
            "later-counter" => Complete() with { Counters = [1, 0, 0, 0, 0, 1] },
            "negative-ordinal" => Complete(ordinal: -1),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        Assert.True(service.ApplyNetworkQuests(List(record), Context(2)));
        var entry = service.Snapshot().Progress["fort"];
        Assert.Null(entry.State);
        Assert.Equal(ProgressFreshness.Unknown, entry.Freshness);
        Assert.Equal(record.Status, entry.QuestStatus);
        Assert.Equal(record.Stage, entry.QuestStage);
        Assert.Equal(record.Counters.ToArray(), entry.QuestCounters!.ToArray());
        Assert.Equal(record.Ordinal, entry.QuestOrdinal);
        Assert.Equal(ProgressFreshness.Fresh, entry.RawFreshness);
        Assert.Empty(service.Completed);
        Assert.Empty(Assert.Single(folder.Read().Characters).DungeonCompletedAt);
    }

    [Fact]
    public void RawStatusThreeNeverCompletesSyntheticDungeonOrStrongholdEvenWithOtherwisePositiveCounters()
    {
        using var folder = new Folder();
        const int dungeonQuest = 91001, strongholdQuest = 91002, controlQuest = 91003;
        ExplorationPlace[] places = [
            new("dungeon", "synthetic-map", "dungeon", 0, "Synthetic Dungeon", null, QuestId: dungeonQuest),
            new("stronghold", "synthetic-map", "stronghold", 1, "Synthetic Stronghold", null, QuestId: strongholdQuest),
            new("control", "synthetic-map", "dungeon", 2, "Synthetic Complete Control", null, QuestId: controlQuest)
        ];
        var service = new ExplorationService(folder.File, places);
        service.BindNetworkContext(Context(1));
        QuestProgressRecord[] rawOnly = [
            new(dungeonQuest, 3, 1, [1, 0, 0, 0, 0, 0], 1),
            new(strongholdQuest, 3, 1, [1, 0, 0, 0, 0, 0], 2)
        ];
        Assert.True(service.ApplyNetworkQuests(new(rawOnly), Context(2)));
        Assert.Empty(service.Completed);
        foreach (var id in new[] { "dungeon", "stronghold" })
        {
            var entry = service.Snapshot().Progress[id];
            Assert.Null(entry.State);
            Assert.Equal(ProgressFreshness.Unknown, entry.Freshness);
            Assert.Equal((byte)3, entry.QuestStatus);
            Assert.Equal((byte)1, entry.QuestStage);
            Assert.Equal(new[] { 1, 0, 0, 0, 0, 0 }, entry.QuestCounters!.ToArray());
            Assert.Equal(ProgressFreshness.Fresh, entry.RawFreshness);
        }
        // Identical stage/counters only become a completion when the independently proven status is four.
        Assert.True(service.ApplyNetworkQuests(new([.. rawOnly,
            new(controlQuest, 4, 1, [1, 0, 0, 0, 0, 0], 3)]), Context(3)));
        Assert.Equal(new[] { "control" }, service.Completed);
        Assert.Empty(Assert.Single(folder.Read().Characters).DungeonCompletedAt);
        var reloaded = new ExplorationService(folder.File, places);
        Assert.Equal(new[] { "control" }, reloaded.Completed);
        foreach (var id in new[] { "dungeon", "stronghold" })
        {
            Assert.Null(reloaded.Snapshot().Progress[id].State);
            Assert.Equal((byte)3, reloaded.Snapshot().Progress[id].QuestStatus);
        }
    }

    [Fact]
    public void AbsenceAndLaterOpenOrUnrecognizedRecordsNeverUndoAnExistingCompletion()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkQuests(List(Complete()), Context(2)));
        var first = service.Snapshot().Progress["fort"];
        Assert.True(service.ApplyNetworkQuests(List(Open()), Context(3)));
        AssertSemanticEqual(first, service.Snapshot().Progress["fort"]);
        Assert.True(service.ApplyNetworkQuests(List(Complete() with { Counters = [1, 1, 0, 0, 0, 0] }), Context(4)));
        AssertSemanticEqual(first, service.Snapshot().Progress["fort"]);
        Assert.True(service.ApplyNetworkQuests(List(), Context(5)));
        var absent = service.Snapshot().Progress["fort"];
        AssertSemanticEqual(first, absent);
        Assert.Null(absent.QuestStatus);
        Assert.Empty(Assert.Single(folder.Read().Characters).RawQuests);
        Assert.Contains("fort", folder.Service().Completed);
        Assert.Null(service.Snapshot().Progress["other"].State);
    }

    [Fact]
    public void StoredRawCompletionIsNeverAppliedOnLoadBindOrAnAbsentFreshList()
    {
        using var folder = new Folder();
        folder.Write(new ExplorationProfiles
        {
            SchemaVersion = 2, Active = "legacy-guid",
            Characters = [new CharacterExploration
            {
                Id = "legacy-guid", Name = "synthetic alpha",
                RawQuests = new() { [2820006] = Complete() },
                Progress = new() { ["fort"] = ExplorationPlaceProgress.Unknown with
                {
                    QuestStatus = 4, QuestStage = 1, QuestCounters = [1, 0, 0, 0, 0, 0], QuestOrdinal = 80,
                    RawSource = "network-quest-unconfirmed", RawFreshness = ProgressFreshness.Fresh, RawObservedAt = At,
                } },
            }],
        });
        var service = folder.Service();
        Assert.Null(service.Snapshot().Progress["fort"].State);
        Assert.Equal(ProgressFreshness.Stale, service.Snapshot().Progress["fort"].RawFreshness);
        Assert.Equal(CharacterChoice.Adopted, service.BindNetworkContext(Context(1)));
        Assert.Null(service.Snapshot().Progress["fort"].State);
        Assert.True(service.ApplyNetworkQuests(List(), Context(2)));
        Assert.Null(service.Snapshot().Progress["fort"].State);
        Assert.True(service.ApplyNetworkQuests(List(Complete(ordinal: 0)), Context(3)));
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, service.Snapshot().Progress["fort"].State);
        Assert.Equal("legacy-guid", service.ActiveId);
        Assert.Equal("synthetic alpha", Assert.Single(service.Characters).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExplorationProgressState.Undiscovered)]
    [InlineData(ExplorationProgressState.DiscoveredIncomplete)]
    [InlineData(ExplorationProgressState.DiscoveredCompleted)]
    public void ManualSemanticStateRemainsProtectedWhileFreshRawCompletionIsStored(ExplorationProgressState? state)
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        service.SetState(service.ActiveId, ["fort"], state);
        var manual = service.Snapshot().Progress["fort"];
        Assert.True(service.ApplyNetworkQuests(List(Complete()), Context(2)));
        var observed = service.Snapshot().Progress["fort"];
        AssertSemanticEqual(manual, observed);
        Assert.True(observed.ManualOverride);
        Assert.Equal((byte)4, observed.QuestStatus);
        Assert.Equal(ProgressFreshness.Fresh, observed.RawFreshness);
        Assert.Contains("fort-side", service.Completed);
        Assert.True(service.ApplyNetworkQuests(List(Open()), Context(3)));
        AssertSemanticEqual(manual, folder.Service().Snapshot().Progress["fort"]);
        Assert.Equal(state == ExplorationProgressState.DiscoveredCompleted, service.Completed.Contains("fort"));
        Assert.Empty(Assert.Single(folder.Read().Characters).DungeonCompletedAt);
    }

    [Theory]
    [InlineData(2820006, 142005, "fort", "network-stronghold-completion")]
    [InlineData(2810034, 320034, "cave", "network-dungeon-completion")]
    public void FreshQuestCompletionConfirmsButPreservesTheFirstDirectResultSourceAndTime(int quest, int instance, string place, string source)
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(instance), Context(2)));
        service.MarkNetworkStale();
        service.BindNetworkContext(Context(1, generation: 2));
        Assert.Equal(ProgressFreshness.Stale, service.Snapshot().Progress[place].Freshness);
        Assert.True(service.ApplyNetworkQuests(List(Complete(quest)), Context(3, generation: 2)));
        var entry = service.Snapshot().Progress[place];
        Assert.Equal(ProgressFreshness.Fresh, entry.Freshness);
        Assert.Equal(source, entry.Source);
        Assert.Equal(Context(2).At, entry.ObservedAt);
        Assert.Equal(Context(3, generation: 2).At, entry.RawObservedAt);
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(instance), Context(4, generation: 2)));
        Assert.Equal(Context(2).At, service.Snapshot().Progress[place].ObservedAt);
        Assert.True(service.ApplyNetworkQuests(List(Open(quest)), Context(5, generation: 2)));
        Assert.Equal(source, service.Snapshot().Progress[place].Source);
        var reloaded = folder.Service();
        Assert.Contains(place, reloaded.Completed);
        Assert.Equal(source, reloaded.Snapshot().Progress[place].Source);
        Assert.Equal(Context(2).At, reloaded.Snapshot().Progress[place].ObservedAt);
        Assert.Equal(Context(2).At, Assert.Single(folder.Read().Characters).DungeonCompletedAt[instance]);
    }

    [Fact]
    public void DirectResultAfterQuestConfirmationBecomesTheDirectProofWithoutBackdatingIt()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkQuests(List(Complete()), Context(2)));
        Assert.True(service.ApplyNetworkDungeonCompletion(Result(142005), Context(3)));
        Assert.True(service.ApplyNetworkQuests(List(Complete()), Context(4)));
        var entry = service.Snapshot().Progress["fort"];
        Assert.Equal("network-stronghold-completion", entry.Source);
        Assert.Equal(Context(3).At, entry.ObservedAt);
        Assert.Equal(Context(3).At, Assert.Single(folder.Read().Characters).DungeonCompletedAt[142005]);
    }

    [Fact]
    public void LegacyCompletionCanBeFreshlyConfirmedWithoutLosingItsOrigin()
    {
        using var folder = new Folder();
        folder.Write(new ExplorationProfiles
        {
            Active = "old-guid", Characters = [new CharacterExploration
            { Id = "old-guid", Name = "Synthetic Alpha", Done = ["fort"] }],
        });
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkQuests(List(Complete()), Context(2)));
        var entry = service.Snapshot().Progress["fort"];
        Assert.Equal(ProgressFreshness.Fresh, entry.Freshness);
        Assert.Equal("legacy", entry.Source);
        Assert.Null(entry.ObservedAt);
        Assert.Equal("old-guid", service.ActiveId);
        Assert.Empty(Assert.Single(folder.Read().Characters).DungeonCompletedAt);
    }

    [Fact]
    public void NetworkCompletionUsesTheBoundProfileEvenWhenTheUiSelectsAnotherCharacter()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        var bound = service.ActiveId;
        service.Rename("Synthetic Custom Label");
        service.Add("Synthetic Beta");
        var selected = service.ActiveId;
        Assert.True(service.ApplyNetworkQuests(List(Complete()), Context(2)));
        Assert.Equal(selected, service.ActiveId);
        Assert.Null(service.Snapshot().Progress["fort"].State);
        var saved = folder.Read();
        var owner = saved.Characters.Single(c => c.Id == bound);
        Assert.Contains("fort", owner.Done);
        Assert.Equal("Synthetic Custom Label", owner.Name);
        Assert.Empty(saved.Characters.Single(c => c.Id == selected).RawQuests);
        service.BindNetworkContext(Context(1, generation: 2));
        Assert.Equal(bound, service.ActiveId);
        Assert.Contains("fort", service.Completed);
        Assert.Equal("Synthetic Custom Label", service.Characters.Single(c => c.Id == bound).Name);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("wrong-server")]
    [InlineData("wrong-character")]
    [InlineData("old-generation")]
    [InlineData("future-generation")]
    [InlineData("before-bind-sequence")]
    [InlineData("invalidated")]
    public void CompletionRequiresCurrentOwnIdentityGenerationAndSequence(string variant)
    {
        using var folder = new Folder();
        var service = folder.Service();
        if (variant != "unbound") service.BindNetworkContext(Context(3, generation: 2));
        if (variant == "invalidated") service.MarkNetworkStale();
        var context = variant switch
        {
            "wrong-server" => Context(4, 2, server: 502),
            "wrong-character" => Context(4, 2, character: 102),
            "old-generation" => Context(4),
            "future-generation" => Context(4, 3),
            "before-bind-sequence" => Context(2, 2),
            _ => Context(4, 2),
        };
        Assert.False(service.ApplyNetworkQuests(List(Complete()), context));
        Assert.Empty(service.Completed);
        if (System.IO.File.Exists(folder.File))
            Assert.All(folder.Read().Characters, c => Assert.Empty(c.RawQuests));
    }

    [Fact]
    public void InvalidBatchesAreAtomicAndDoNotConsumeTheFollowingValidSequence()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.False(service.ApplyNetworkQuests(List(Complete(), Complete()), Context(2)));
        Assert.False(service.ApplyNetworkQuests(List(Complete(), Complete(0)), Context(2)));
        Assert.False(service.ApplyNetworkQuests(List(Complete(), Complete(-1)), Context(2)));
        Assert.Empty(service.Completed);
        Assert.True(service.ApplyNetworkQuests(List(Complete()), Context(2)));
        Assert.False(service.ApplyNetworkQuests(List(), Context(2)));
        Assert.False(service.ApplyNetworkQuests(List(), Context(1)));
        Assert.Contains("fort", service.Completed);
        Assert.Equal((byte)4, service.Snapshot().Progress["fort"].QuestStatus);
    }

    [Fact]
    public void FailedStorageKeepsAcceptedCompletionDedupeAndTheNextSnapshotPersistsIt()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        System.IO.File.Delete(folder.File);
        Directory.CreateDirectory(folder.File);
        Assert.True(service.ApplyNetworkQuests(List(Complete()), Context(2)));
        Assert.Contains("fort", service.Completed);
        Assert.False(service.ApplyNetworkQuests(List(), Context(2)));
        Directory.Delete(folder.File);
        Assert.True(service.ApplyNetworkQuests(List(Complete(2810034)), Context(3)));
        var reloaded = folder.Service();
        Assert.Equal(new[] { "cave", "fort", "fort-side" }, reloaded.Completed.Order());
        Assert.Equal(Context(2).At, reloaded.Snapshot().Progress["fort"].ObservedAt);
        Assert.Empty(Assert.Single(folder.Read().Characters).DungeonCompletedAt);
    }

    [Fact]
    public void InputAndSnapshotCounterMutationsCannotChangeStoredCompletionEvidence()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        var counters = new[] { 1, 0, 0, 0, 0, 0 };
        Assert.True(service.ApplyNetworkQuests(List(Complete() with { Counters = counters }), Context(2)));
        counters[0] = 999;
        var snapshot = service.Snapshot();
        Assert.IsType<int[]>(snapshot.Progress["fort"].QuestCounters)[0] = 999;
        Assert.Equal(1, service.Snapshot().Progress["fort"].QuestCounters![0]);
        Assert.Equal(1, service.Snapshot().Progress["fort-side"].QuestCounters![0]);
        Assert.Equal(1, Assert.Single(folder.Read().Characters).RawQuests[2820006].Counters[0]);
        Assert.Contains("fort", service.Completed);
    }

    private static void AssertSemanticEqual(ExplorationPlaceProgress expected, ExplorationPlaceProgress actual)
    {
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Freshness, actual.Freshness);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.ObservedAt, actual.ObservedAt);
        Assert.Equal(expected.ManualOverride, actual.ManualOverride);
    }
}
