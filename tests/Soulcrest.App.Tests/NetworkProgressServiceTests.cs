using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkProgressServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly PetIdEntry Pet = PetIdTable.All.Values.First();
    private static string PetId => Pet.CatalogId ?? PetCatalog.Slug(Pet.En);
    private static NetworkProgressContext Context(long sequence, long character = 101, int server = 501, long generation = 1,
        string name = "Synthetic Alpha") => new(new(character, server, 1001, name), generation, sequence, At.AddSeconds(sequence));
    private static PetProgressSnapshot PetSnapshot(int level, int balance) => new(
        new Dictionary<int, int> { [Pet.Id] = level }, new Dictionary<int, int> { [Pet.Id] = balance });
    private static readonly ExplorationPlace[] Places =
    [
        new("dungeon", "altgard", "dungeon", 0, "Synthetic Cave", null, QuestId: 2810001),
        new("stronghold", "altgard", "stronghold", 1, "Synthetic Fort", null, QuestId: 2820001),
        new("teleport-a", "altgard", "kibelisk", 2, "Synthetic Pillar A", null, TeleportId: 1052043),
        new("teleport-b", "altgard", "kibelisk", 3, "Synthetic Pillar B", null, TeleportId: 1052043),
    ];

    private sealed class Folder : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-network-model-" + Guid.NewGuid().ToString("N"));
        internal string File(string name) => Path.Combine(Root, name);
        internal Folder() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
        internal ProgressService Progress() => new(File("progress.json"), new PetCatalog([
            new PetDefinition { Id = PetId, En = Pet.En, Genus = Pet.Genus, NumericId = Pet.Id.ToString() }]));
        internal ExplorationService Exploration() => new(File("exploration.json"), Places);
    }

    [Fact]
    public void ABlockedProfileWriteDoesNotAbortNetworkBindingSnapshotOrTheNextGain()
    {
        using var folder = new Folder();
        var service = folder.Progress();
        var path = folder.File("progress.json");
        Directory.CreateDirectory(path); // Deliberate file-system fault, only inside this test's folder.
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkSnapshot(PetSnapshot(1, 25), Context(2)));
        Assert.Equal(30, service.Souls(PetId));
        Assert.False(service.ApplyNetworkSnapshot(PetSnapshot(1, 25), Context(2)));
        Assert.True(service.ApplyNetworkSpent([new(Pet.Id, 25)], Context(3)));
        Assert.True(service.ApplyNetworkAcquisition(new(1001, Pet.Id, 2), Context(4)));
        Assert.Equal(2, service.ObservedLevel(PetId));
        Assert.Empty(service.Recent());

        Directory.Delete(path);
        Assert.True(service.ApplyNetworkGain([new(Pet.Id, 2)], Context(5)));
        Assert.False(service.ApplyNetworkGain([new(Pet.Id, 2)], Context(5)));
        Assert.Equal(32, service.Souls(PetId));
        Assert.Equal(2, service.AvailableSouls(PetId));
        Assert.Equal(2, Assert.Single(service.Recent()).Quantity);
        Assert.Single(File.ReadAllLines(folder.File("events.jsonl")));
        Assert.Equal(32, folder.Progress().Souls(PetId));
    }

    [Fact]
    public void ABlockedExplorationWriteKeepsItsCharacterContextAndAcceptedRawObservations()
    {
        using var folder = new Folder();
        var service = folder.Exploration();
        var path = folder.File("exploration.json");
        Directory.CreateDirectory(path);
        Assert.Equal(CharacterChoice.Adopted, service.BindNetworkContext(Context(1)));
        var character = service.ActiveId;
        Assert.True(service.ApplyNetworkQuests(new([new(2810001, 4, 0, [], 1)]), Context(2)));
        Assert.True(service.ApplyNetworkTeleports(new(new HashSet<int> { 1052043 }), Context(3)));
        Assert.Equal(character, service.ActiveId);
        Assert.Equal((byte)4, service.Snapshot().Progress["dungeon"].QuestStatus);
        Assert.True(service.Snapshot().Progress["teleport-a"].TeleportPresent);
        Assert.False(service.ApplyNetworkTeleports(new(new HashSet<int>()), Context(3)));

        Directory.Delete(path);
        Assert.True(service.ApplyNetworkQuests(new([new(2810001, 2, 0, [], 2)]), Context(4)));
        Assert.Equal(character, folder.Exploration().ActiveId);
        Assert.Equal((byte)2, folder.Exploration().Snapshot().Progress["dungeon"].QuestStatus);
        Assert.True(folder.Exploration().Snapshot().Progress["teleport-a"].TeleportPresent);
    }

    [Fact]
    public void ManualWriteFailuresStillReachTheCaller()
    {
        using var folder = new Folder();
        var progress = folder.Progress();
        Directory.CreateDirectory(folder.File("progress.json"));
        var petError = Record.Exception(() => progress.SetSouls(PetId, 7));
        Assert.True(petError is IOException or UnauthorizedAccessException);
        var exploration = folder.Exploration();
        Directory.CreateDirectory(folder.File("exploration.json"));
        var explorationError = Record.Exception(() => exploration.SetState(exploration.ActiveId, ["dungeon"],
            ExplorationProgressState.DiscoveredIncomplete));
        Assert.True(explorationError is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public void CompletePetSnapshotReplacesLegacyTotalsWithoutLoggingPickupsAndPreservesFocus()
    {
        using var folder = new Folder();
        var original = new ProgressProfile();
        original.Pets[PetId] = new() { Souls = 90, Focus = true };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(original);
        File.WriteAllBytes(folder.File("progress.json"), bytes);
        var service = folder.Progress();
        Assert.Equal(90, service.Souls(PetId));
        Assert.Empty(Directory.GetFiles(folder.Root, "*.bak"));
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(folder.Root, "*.bak"))));
        var changes = 0;
        service.Changed += () => changes++;
        Assert.True(service.ApplyNetworkSnapshot(PetSnapshot(1, 3), Context(2)));
        Assert.Equal(8, service.Souls(PetId));
        Assert.True(service.IsFocus(PetId));
        Assert.Empty(service.Recent());
        Assert.False(File.Exists(folder.File("events.jsonl")));
        Assert.Equal(1, changes);
        var saved = File.ReadAllBytes(folder.File("progress.json"));
        Assert.False(service.ApplyNetworkSnapshot(PetSnapshot(1, 3), Context(2)));
        Assert.Equal(saved, File.ReadAllBytes(folder.File("progress.json")));
        Assert.Equal(1, changes);
        Assert.True(service.ApplyNetworkSnapshot(new(new Dictionary<int, int>(), new Dictionary<int, int>()), Context(3)));
        Assert.Equal(0, service.Souls(PetId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AcquisitionAndSpendingOrderNeverDoubleCountsAvailableSouls(bool acquireFirst)
    {
        using var folder = new Folder();
        var service = folder.Progress();
        service.BindNetworkContext(Context(1));
        service.ApplyNetworkSnapshot(PetSnapshot(1, 25), Context(2));
        Assert.Equal(30, service.Souls(PetId));
        if (acquireFirst)
        {
            Assert.True(service.ApplyNetworkAcquisition(new(1001, Pet.Id, 2), Context(3)));
            Assert.True(service.ApplyNetworkSpent([new(Pet.Id, 25)], Context(4)));
        }
        else
        {
            Assert.True(service.ApplyNetworkSpent([new(Pet.Id, 25)], Context(3)));
            Assert.True(service.ApplyNetworkAcquisition(new(1001, Pet.Id, 2), Context(4)));
        }
        Assert.Equal(30, service.Souls(PetId));
        Assert.Empty(service.Recent());
        Assert.True(service.ApplyNetworkGain([new(Pet.Id, 2)], Context(5)));
        Assert.False(service.ApplyNetworkGain([new(Pet.Id, 2)], Context(5)));
        Assert.Equal(32, service.Souls(PetId));
        Assert.Equal(2, Assert.Single(service.Recent()).Quantity);
        Assert.Single(File.ReadAllLines(folder.File("events.jsonl")));
        Assert.True(service.ApplyNetworkSnapshot(PetSnapshot(2, 2), Context(6)));
        Assert.Equal(32, service.Souls(PetId));
        Assert.Single(service.Recent());
        Assert.False(service.ApplyNetworkAcquisition(new(9999, Pet.Id, 3), Context(7)));
    }

    [Fact]
    public void AvailableSoulsBeyondTheNextCostDoNotClaimAnUnobservedAcquiredLevel()
    {
        using var folder = new Folder();
        var service = folder.Progress();
        service.BindNetworkContext(Context(1));
        service.ApplyNetworkSnapshot(PetSnapshot(1, 100), Context(2));
        Assert.Equal(105, service.Souls(PetId));
        Assert.Equal(1, service.ObservedLevel(PetId));
        Assert.Equal(100, service.AvailableSouls(PetId));
        Assert.Equal(1, service.Level(PetId));
        Assert.False(service.IsMax(PetId));
        Assert.Equal(25, service.NeededForNextLevel(PetId));
        Assert.Equal("100/25", service.SoulCount(PetId));
        service.ApplyNetworkGain([new(Pet.Id, 1)], Context(3));
        Assert.Equal(1, service.ObservedLevel(PetId));
        Assert.Equal(101, service.AvailableSouls(PetId));
        service.ApplyNetworkAcquisition(new(1001, Pet.Id, 2), Context(4));
        Assert.Equal(2, service.ObservedLevel(PetId));
        Assert.Equal(106, service.Souls(PetId));
        service.ApplyNetworkSpent([new(Pet.Id, 25)], Context(5));
        Assert.Equal(76, service.AvailableSouls(PetId));
        Assert.Equal(106, service.Souls(PetId));
        service.MarkNetworkStale();
        Assert.Equal(2, service.ObservedLevel(PetId));
        Assert.Equal(ProgressFreshness.Stale, service.EntrySnapshot(PetId).Freshness);
        var detached = service.EntrySnapshot(PetId);
        detached.Souls = 999;
        Assert.Equal(106, service.Souls(PetId));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 1)]
    public void LootBeforeThePetBaselineKeepsItsLegacyLevelSeedAcrossLaterDeltasAndReload(int legacySouls, int legacyLevel)
    {
        using var folder = new Folder();
        var original = new ProgressProfile();
        original.Pets[PetId] = new() { Souls = legacySouls };
        File.WriteAllText(folder.File("progress.json"), JsonSerializer.Serialize(original));
        var service = folder.Progress();
        Assert.Equal(legacyLevel, service.Level(PetId));
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkGain([new(Pet.Id, 30)], Context(2)));
        Assert.Equal(legacySouls + 30, service.Souls(PetId));
        Assert.Equal(2, service.Thresholds.Level(service.Souls(PetId)));
        Assert.Equal(legacyLevel, service.Level(PetId));
        Assert.Equal(legacySouls + 30 - service.Thresholds.TotalForLevel(legacyLevel), service.SoulsInLevel(PetId));
        Assert.Null(service.ObservedLevel(PetId));
        Assert.Null(service.AvailableSouls(PetId));
        Assert.Null(service.NetworkBaselineAt);
        Assert.True(service.ApplyNetworkGain([new(Pet.Id, 1)], Context(3)));
        Assert.Equal(legacyLevel, service.Level(PetId));

        var loaded = folder.Progress();
        Assert.Equal(legacyLevel, loaded.Level(PetId));
        Assert.Null(loaded.ObservedLevel(PetId));
        Assert.True(loaded.BindNetworkContext(Context(1, generation: 2)));
        Assert.True(loaded.ApplyNetworkGain([new(Pet.Id, 1)], Context(2, generation: 2)));
        Assert.Equal(legacyLevel, loaded.Level(PetId));
        Assert.Equal(legacySouls + 32 - loaded.Thresholds.TotalForLevel(legacyLevel), loaded.SoulsInLevel(PetId));
        Assert.True(loaded.ApplyNetworkAcquisition(new(1001, Pet.Id, 2), Context(3, generation: 2)));
        Assert.Equal(2, loaded.Level(PetId));
        Assert.Equal(2, loaded.ObservedLevel(PetId));
        Assert.Equal(legacySouls + 32, loaded.Souls(PetId));
    }

    [Fact]
    public void AStaleObservedLevelStaysFixedThroughPreBaselineLootAndTheNextFullSnapshotIsAuthoritative()
    {
        using var folder = new Folder();
        var service = folder.Progress();
        service.BindNetworkContext(Context(1));
        service.ApplyNetworkSnapshot(PetSnapshot(1, 24), Context(2));
        service.MarkNetworkStale();
        Assert.Equal(1, service.Level(PetId));
        Assert.True(service.BindNetworkContext(Context(1, generation: 2)));
        Assert.True(service.ApplyNetworkGain([new(Pet.Id, 100)], Context(2, generation: 2)));
        Assert.Equal(129, service.Souls(PetId));
        Assert.Equal(1, service.Level(PetId));
        Assert.Equal(1, service.ObservedLevel(PetId));
        Assert.False(service.IsMax(PetId));
        Assert.True(service.ApplyNetworkSnapshot(PetSnapshot(3, 0), Context(3, generation: 2)));
        Assert.Equal(3, service.Level(PetId));
        Assert.Equal(105, service.Souls(PetId));
        Assert.True(service.IsMax(PetId));
    }

    [Fact]
    public void AManualOverrideKeepsItsLevelFallbackWhileTheNetworkLevelSeedStaysSeparate()
    {
        using var folder = new Folder();
        var service = folder.Progress();
        service.BindNetworkContext(Context(1));
        service.ApplyNetworkGain([new(Pet.Id, 30)], Context(2));
        Assert.Equal(0, service.Level(PetId));
        service.SetSouls(PetId, 105);
        Assert.Equal(3, service.Level(PetId));
        Assert.Null(service.ObservedLevel(PetId));
        service.ClearManualOverride(PetId);
        Assert.Equal(30, service.Souls(PetId));
        Assert.Equal(0, service.Level(PetId));
        Assert.Null(service.ObservedLevel(PetId));
    }

    [Fact]
    public void PetsRemainServerWideAndExistingServerProfilesReturnWithTheirTotals()
    {
        using var folder = new Folder();
        var service = folder.Progress();
        service.BindNetworkContext(Context(1));
        service.ApplyNetworkSnapshot(PetSnapshot(1, 3), Context(2));
        service.ToggleFocus(PetId);
        service.BindNetworkContext(Context(3, character: 102, name: "Synthetic Beta"));
        Assert.Equal(8, service.Souls(PetId));
        Assert.True(service.IsFocus(PetId));
        service.BindNetworkContext(Context(4, character: 201, server: 502, name: "Synthetic Gamma"));
        Assert.Equal(0, service.Souls(PetId));
        Assert.False(service.IsFocus(PetId));
        service.ApplyNetworkSnapshot(PetSnapshot(2, 7), Context(5, character: 201, server: 502, name: "Synthetic Gamma"));
        service.BindNetworkContext(Context(6));
        Assert.Equal(8, service.Souls(PetId));
        Assert.True(service.IsFocus(PetId));
        Assert.Equal(501, service.ActiveServerId);
        var loaded = folder.Progress();
        Assert.Equal(8, loaded.Souls(PetId));
        loaded.BindNetworkContext(Context(7, character: 201, server: 502, name: "Synthetic Gamma"));
        Assert.Equal(37, loaded.Souls(PetId));
    }

    [Fact]
    public void ManualPetOverrideIsExplicitAndCanBeReleasedToTheSeparatelyStoredNetworkTotal()
    {
        using var folder = new Folder();
        var service = folder.Progress();
        service.SetSouls(PetId, 40);
        service.BindNetworkContext(Context(1));
        service.ApplyNetworkSnapshot(PetSnapshot(1, 3), Context(2));
        Assert.Equal(40, service.Souls(PetId));
        service.ApplyNetworkGain([new(Pet.Id, 2)], Context(3));
        Assert.Equal(42, service.Souls(PetId));
        service.ClearManualOverride(PetId);
        Assert.Equal(10, service.Souls(PetId));
    }

    [Fact]
    public void InvalidPetBatchesAndOldGenerationCannotPartiallyChangeProgress()
    {
        using var folder = new Folder();
        var service = folder.Progress();
        service.BindNetworkContext(Context(1));
        service.ApplyNetworkSnapshot(PetSnapshot(1, 3), Context(2));
        Assert.False(service.ApplyNetworkSnapshot(new(new Dictionary<int, int> { [Pet.Id] = 4 }, new Dictionary<int, int>()), Context(3)));
        Assert.False(service.ApplyNetworkGain([new(Pet.Id, 2), new(999, -1)], Context(3)));
        Assert.Equal(8, service.Souls(PetId));
        service.MarkNetworkStale();
        Assert.False(service.ApplyNetworkGain([new(Pet.Id, 2)], Context(4)));
        Assert.False(service.BindNetworkContext(Context(5)));
        Assert.True(service.BindNetworkContext(Context(1, generation: 2)));
        Assert.False(service.ApplyNetworkSnapshot(PetSnapshot(2, 9), Context(99)));
        Assert.True(service.ApplyNetworkSnapshot(PetSnapshot(2, 9), Context(2, generation: 2)));
        Assert.Equal(39, service.Souls(PetId));
    }

    [Fact]
    public void LegacyExplorationMigrationPreservesDoneAndDoesNotInventUndiscovered()
    {
        using var folder = new Folder();
        var bytes = """{"Active":"legacy","Characters":[{"Id":"legacy","Name":"Synthetic Alpha","Done":["dungeon"]}]}"""u8.ToArray();
        File.WriteAllBytes(folder.File("exploration.json"), bytes);
        var service = folder.Exploration();
        var snapshot = service.Snapshot();
        Assert.Equal("legacy", snapshot.CharacterId);
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, snapshot.Progress["dungeon"].State);
        Assert.Equal(ProgressFreshness.Unknown, snapshot.Progress["dungeon"].Freshness);
        Assert.Null(snapshot.Progress["stronghold"].State);
        Assert.Equal(bytes, File.ReadAllBytes(folder.File("exploration.json")));
        Assert.Equal(CharacterChoice.Adopted, service.BindNetworkContext(Context(1)));
        Assert.Equal("legacy", service.ActiveId);
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(folder.Root, "*.bak"))));
        Assert.Equal(101, service.Snapshot().NetworkCharacterId);
        Assert.Equal(501, service.Snapshot().ServerId);
        Assert.Contains("dungeon", folder.Exploration().Completed);
    }

    [Fact]
    public void RawQuestStatesAndTeleportAbsencePreserveMarksWhileKnownTeleportPresenceConfirmsBinding()
    {
        using var folder = new Folder();
        var service = folder.Exploration();
        service.SetDone(service.ActiveId, ["dungeon", "teleport-a"]);
        service.BindNetworkContext(Context(1));
        var events = 0;
        service.Changed += () => events++;
        Assert.True(service.ApplyNetworkQuests(new([new(2810001, 4, 1, [1, 2], 7), new(2820001, 2, 0, [], 8)]), Context(2)));
        Assert.True(service.ApplyNetworkTeleports(new(new HashSet<int> { 1052043 }), Context(2)));
        var snapshot = service.Snapshot();
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, snapshot.Progress["dungeon"].State);
        Assert.Equal((byte)4, snapshot.Progress["dungeon"].QuestStatus);
        Assert.Null(snapshot.Progress["stronghold"].State);
        Assert.Equal((byte)2, snapshot.Progress["stronghold"].QuestStatus);
        Assert.True(snapshot.Progress["teleport-a"].TeleportPresent);
        Assert.True(snapshot.Progress["teleport-b"].TeleportPresent);
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, snapshot.Progress["teleport-b"].State);
        Assert.False(service.ApplyNetworkQuests(new([]), Context(2)));
        Assert.Equal(2, events);
        Assert.True(service.ApplyNetworkTeleports(new(new HashSet<int>()), Context(3)));
        Assert.Contains("teleport-a", service.Completed);
        Assert.False(service.Snapshot().Progress["teleport-a"].TeleportPresent);
        var counters = Assert.IsType<int[]>(snapshot.Progress["dungeon"].QuestCounters);
        counters[0] = 999;
        Assert.Equal(1, service.Snapshot().Progress["dungeon"].QuestCounters![0]);
    }

    [Fact]
    public void ExplorationNetworkUpdatesStayBoundWhenTheUiSelectsAnotherProfile()
    {
        using var folder = new Folder();
        var service = folder.Exploration();
        service.BindNetworkContext(Context(1));
        var first = service.ActiveId;
        service.Add("Synthetic Beta");
        var second = service.ActiveId;
        Assert.True(service.ApplyNetworkQuests(new([new(2810001, 4, 0, [], 1)]), Context(2)));
        Assert.Equal(second, service.ActiveId);
        Assert.Null(service.Snapshot().Progress["dungeon"].QuestStatus);
        service.Select(first);
        Assert.Equal((byte)4, service.Snapshot().Progress["dungeon"].QuestStatus);
        service.BindNetworkContext(Context(3, character: 102, name: "Synthetic Beta"));
        Assert.Equal(second, service.ActiveId);
        Assert.False(service.ApplyNetworkQuests(new([]), Context(4)));
        Assert.True(service.ApplyNetworkQuests(new([]), Context(4, character: 102, name: "Synthetic Beta")));
    }

    [Fact]
    public void IdenticalNamesOnDifferentServersStaySeparateAndOldCaptureCallbacksAreRejected()
    {
        using var folder = new Folder();
        var service = folder.Exploration();
        service.BindNetworkContext(Context(1));
        var first = service.ActiveId;
        service.SetState(first, ["dungeon"], ExplorationProgressState.DiscoveredIncomplete);
        service.BindNetworkContext(Context(2, server: 502));
        var second = service.ActiveId;
        Assert.NotEqual(first, second);
        Assert.Null(service.Snapshot().Progress["dungeon"].State);
        service.BindNetworkContext(Context(3));
        Assert.Equal(first, service.ActiveId);
        Assert.Equal(ExplorationProgressState.DiscoveredIncomplete, service.Snapshot().Progress["dungeon"].State);
        service.ApplyNetworkQuests(new([new(2810001, 4, 0, [], 0)]), Context(4));
        service.MarkNetworkStale();
        Assert.Equal(ProgressFreshness.Stale, service.Snapshot().Progress["dungeon"].RawFreshness);
        Assert.False(service.ApplyNetworkQuests(new([]), Context(5)));
        service.BindNetworkContext(Context(6));
        Assert.False(service.ApplyNetworkQuests(new([]), Context(7)));
        service.BindNetworkContext(Context(1, generation: 2));
        Assert.True(service.ApplyNetworkQuests(new([]), Context(2, generation: 2)));
    }

    [Fact]
    public void ThreeGameStatesAndUnknownAreIndependentFromPrerequisiteLocksAndRawFreshness()
    {
        using var folder = new Folder();
        var service = folder.Exploration();
        var id = service.ActiveId;
        service.SetState(id, ["dungeon"], ExplorationProgressState.Undiscovered);
        service.SetState(id, ["stronghold"], ExplorationProgressState.DiscoveredIncomplete);
        var snapshot = service.Snapshot();
        Assert.Empty(snapshot.Completed);
        var overview = ExplorationOverview.Build(Places, snapshot, "altgard", "en");
        Assert.Equal(ExplorationProgressState.Undiscovered, overview.Rows.Single(r => r.Place.Id == "dungeon").State);
        Assert.Equal(ExplorationProgressState.DiscoveredIncomplete, overview.Rows.Single(r => r.Place.Id == "stronghold").State);
        Assert.Equal(2, ExplorationOverview.Build(Places, snapshot, "altgard", "en", status: "unknown").Rows.Count);
        var filtered = ExplorationOverview.Build(Places, snapshot, "altgard", "en", search: "Cave", kind: "dungeon");
        Assert.Equal(1, filtered.Categories.Single(category => category.Kind == "stronghold").Discovered);
        Assert.Equal(2, filtered.Categories.Single(category => category.Kind == "kibelisk").Unknown);
        service.SetDone(id, ["dungeon"]);
        Assert.Contains("dungeon", service.Completed);
        service.SetDone(id, ["dungeon"], false);
        Assert.Equal(ExplorationProgressState.DiscoveredIncomplete, service.Snapshot().Progress["dungeon"].State);
        service.SetState(id, ["dungeon"], null);
        Assert.Null(service.Snapshot().Progress["dungeon"].State);
    }
    [Fact]
    public void BoundCharacterKeepsItsManuallyRenamedProfileLabelOnTheNextWorldEntry()
    {
        using var folder = new Folder();
        var service = folder.Exploration();
        service.BindNetworkContext(Context(1));
        var id = service.ActiveId;
        service.Rename("Synthetic custom label");
        service.MarkNetworkStale();
        service.BindNetworkContext(Context(1, generation: 2));
        Assert.Equal(id, service.ActiveId);
        Assert.Equal("Synthetic custom label", Assert.Single(service.Characters).Name);
    }

}
