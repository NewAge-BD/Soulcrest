using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkProgressSourceInvalidationTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly PetIdEntry Pet = PetIdTable.All.Values.First();
    private static string PetId => Pet.CatalogId ?? PetCatalog.Slug(Pet.En);
    private static NetworkProgressContext Context(long sequence, long generation = 1) =>
        new(new(101, 501, 1001, "Synthetic Alpha"), generation, sequence, At.AddSeconds(sequence));
    private static PetProgressSnapshot Pets(int level, int balance) => new(
        new Dictionary<int, int> { [Pet.Id] = level }, new Dictionary<int, int> { [Pet.Id] = balance });
    private static QuestProgressSnapshot Quests(byte status = 4) => new([new(2810001, status, 1, [1, 2], 7)]);
    private static TeleportProgressSnapshot Teleports(bool present = true) => new(present ? new HashSet<int> { 1052043 } : []);

    private sealed class Folder : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-source-stale-" + Guid.NewGuid().ToString("N"));
        internal string File(string name) => Path.Combine(Root, name);
        internal Folder() => Directory.CreateDirectory(Root);
        internal ProgressService Progress() => new(File("progress.json"), new PetCatalog([
            new PetDefinition { Id = PetId, En = Pet.En, Genus = Pet.Genus, NumericId = Pet.Id.ToString() }]));
        internal ExplorationService Exploration() => new(File("exploration.json"), [
            new("dungeon", "altgard", "dungeon", 0, "Synthetic Cave", null, QuestId: 2810001),
            new("stronghold", "altgard", "stronghold", 1, "Synthetic Fort", null, QuestId: 2820001),
            new("teleport", "altgard", "kibelisk", 2, "Synthetic Pillar", null, TeleportId: 1052043)]);
        internal T Read<T>(string name) => JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(File(name)))!;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public void PetSourceFailureKeepsTheLevelSeedAndOwnBindingForLaterDeduplicatedGains()
    {
        using var folder = new Folder();
        var service = folder.Progress();
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkSnapshot(Pets(1, 24), Context(2)));
        service.ToggleFocus(PetId);
        Assert.True(service.ApplyNetworkGain([new(Pet.Id, 1)], Context(3)));
        Assert.Equal(30, service.Souls(PetId));

        service.MarkNetworkPetSourceStale();
        Assert.Equal(ProgressFreshness.Stale, service.EntrySnapshot(PetId).Freshness);
        Assert.Equal(30, service.Souls(PetId));
        Assert.Equal(1, service.Level(PetId));
        Assert.True(service.IsFocus(PetId));
        Assert.Null(service.ObservedLevel(PetId));
        Assert.Null(service.AvailableSouls(PetId));
        Assert.Null(service.NetworkBaselineAt);
        var persisted = folder.Read<ProgressProfile>("progress.json");
        var raw = persisted.NetworkPets[Pet.Id];
        Assert.False(raw.HasBaseline);
        Assert.False(raw.LevelObserved);
        Assert.False(raw.BalanceObserved);
        Assert.Equal(1, raw.Level);
        Assert.Equal(25, raw.AvailableSouls);
        Assert.Equal(30, raw.EarnedTotal);
        Assert.Null(persisted.NetworkBaselineAt);

        Assert.False(service.ApplyNetworkGain([new(Pet.Id, 1)], Context(3)));
        Assert.True(service.ApplyNetworkGain([new(Pet.Id, 2)], Context(4)));
        Assert.Equal(32, service.Souls(PetId));
        Assert.Equal(1, service.Level(PetId));
        Assert.Null(service.AvailableSouls(PetId));
        Assert.Null(service.ObservedLevel(PetId));
        Assert.Equal(2, service.Recent().Count);
        Assert.Equal(2, System.IO.File.ReadAllLines(folder.File("events.jsonl")).Length);
        Assert.True(service.ApplyNetworkSnapshot(Pets(2, 2), Context(5)));
        Assert.Equal(2, service.ObservedLevel(PetId));
        Assert.Equal(2, service.AvailableSouls(PetId));
        Assert.Equal(Context(5).At, service.NetworkBaselineAt);
        Assert.Equal(ProgressFreshness.Fresh, service.EntrySnapshot(PetId).Freshness);
        Assert.Equal(2, service.Recent().Count); // a replacement snapshot is never another pickup
    }

    [Fact]
    public void PetSourceFailurePreservesManualOverridesAndIndependentExplorationSources()
    {
        using var folder = new Folder();
        var progress = folder.Progress();
        var exploration = folder.Exploration();
        progress.SetSouls(PetId, 40);
        Assert.True(progress.BindNetworkContext(Context(1)));
        exploration.BindNetworkContext(Context(1));
        Assert.True(progress.ApplyNetworkSnapshot(Pets(1, 3), Context(2)));
        Assert.True(exploration.ApplyNetworkQuests(Quests(), Context(2)));
        Assert.True(exploration.ApplyNetworkTeleports(Teleports(), Context(3)));
        Assert.True(progress.ApplyNetworkGain([new(Pet.Id, 2)], Context(3)));
        var manual = progress.EntrySnapshot(PetId);
        var character = exploration.ActiveId;

        progress.MarkNetworkPetSourceStale();
        Assert.Equal(42, progress.Souls(PetId));
        Assert.True(progress.EntrySnapshot(PetId).ManualOverride);
        Assert.Equal(manual.Source, progress.EntrySnapshot(PetId).Source);
        Assert.Equal(manual.Freshness, progress.EntrySnapshot(PetId).Freshness);
        Assert.Equal(manual.UpdatedAt, progress.EntrySnapshot(PetId).UpdatedAt);
        Assert.Equal(ProgressFreshness.Fresh, exploration.Snapshot().Progress["dungeon"].RawFreshness);
        Assert.Equal(ProgressFreshness.Fresh, exploration.Snapshot().Progress["teleport"].RawFreshness);
        Assert.Equal(character, exploration.ActiveId);

        Assert.False(progress.ApplyNetworkGain([new(Pet.Id, 2)], Context(3)));
        Assert.True(progress.ApplyNetworkGain([new(Pet.Id, 2)], Context(4)));
        Assert.Equal(44, progress.Souls(PetId));
        Assert.True(exploration.ApplyNetworkQuests(Quests(2), Context(4)));
        Assert.True(exploration.ApplyNetworkTeleports(Teleports(false), Context(5)));
        progress.ClearManualOverride(PetId);
        Assert.Equal(12, progress.Souls(PetId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplorationSourceFailureKeepsTheOtherSourceManualStatesRawValuesAndSequenceGuards(bool quest)
    {
        using var folder = new Folder();
        var service = folder.Exploration();
        service.BindNetworkContext(Context(1));
        var character = service.ActiveId;
        service.SetState(character, ["dungeon", "teleport"], ExplorationProgressState.DiscoveredCompleted);
        Assert.True(service.ApplyNetworkQuests(Quests(), Context(2)));
        Assert.True(service.ApplyNetworkTeleports(Teleports(), Context(3)));
        var before = service.Snapshot();

        if (quest) service.MarkNetworkQuestSourceStale();
        else service.MarkNetworkTeleportSourceStale();
        var after = service.Snapshot();
        Assert.Equal(character, after.CharacterId);
        Assert.Equal(before.NetworkCharacterId, after.NetworkCharacterId);
        Assert.Equal(before.ServerId, after.ServerId);
        Assert.Equal(before.Completed.Order(), after.Completed.Order());
        foreach (var (id, prior) in before.Progress)
        {
            var stale = quest ? id is "dungeon" or "stronghold" : id == "teleport";
            AssertPreserved(prior, after.Progress[id], stale ? ProgressFreshness.Stale : prior.RawFreshness);
        }
        var saved = folder.Read<ExplorationProfiles>("exploration.json").Characters.Single(c => c.Id == character);
        Assert.Equal((byte)4, Assert.Single(saved.RawQuests).Value.Status);
        Assert.Contains(1052043, saved.RawTeleportIds);
        Assert.Equal(after.Progress[quest ? "dungeon" : "teleport"].RawFreshness,
            saved.Progress[quest ? "dungeon" : "teleport"].RawFreshness);
        Assert.False(service.ApplyNetworkQuests(Quests(2), Context(2)));
        Assert.False(service.ApplyNetworkTeleports(Teleports(false), Context(3)));

        if (quest)
        {
            Assert.True(service.ApplyNetworkTeleports(Teleports(false), Context(4)));
            Assert.Equal(ProgressFreshness.Stale, service.Snapshot().Progress["dungeon"].RawFreshness);
            Assert.True(service.ApplyNetworkQuests(Quests(2), Context(5)));
        }
        else
        {
            Assert.True(service.ApplyNetworkQuests(Quests(2), Context(4)));
            Assert.Equal(ProgressFreshness.Stale, service.Snapshot().Progress["teleport"].RawFreshness);
            Assert.True(service.ApplyNetworkTeleports(Teleports(false), Context(5)));
        }
        var refreshed = service.Snapshot();
        Assert.Equal(ProgressFreshness.Fresh, refreshed.Progress["dungeon"].RawFreshness);
        Assert.Equal(ProgressFreshness.Fresh, refreshed.Progress["teleport"].RawFreshness);
        Assert.Equal((byte)2, refreshed.Progress["dungeon"].QuestStatus);
        Assert.False(refreshed.Progress["teleport"].TeleportPresent);
        Assert.True(refreshed.Progress["dungeon"].ManualOverride);
        Assert.True(refreshed.Progress["teleport"].ManualOverride);
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, refreshed.Progress["dungeon"].State);
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, refreshed.Progress["teleport"].State);
        Assert.False(quest ? service.ApplyNetworkQuests(Quests(), Context(5))
            : service.ApplyNetworkTeleports(Teleports(), Context(5)));
    }

    [Fact]
    public void ExplorationSourceFailureTargetsTheBoundCharacterWhileTheUiShowsAnotherProfile()
    {
        using var folder = new Folder();
        var service = folder.Exploration();
        service.BindNetworkContext(Context(1));
        var bound = service.ActiveId;
        Assert.True(service.ApplyNetworkQuests(Quests(), Context(2)));
        Assert.True(service.ApplyNetworkTeleports(Teleports(), Context(3)));
        service.Add("Synthetic Beta");
        var visible = service.ActiveId;
        service.SetState(visible, ["dungeon"], ExplorationProgressState.DiscoveredIncomplete);
        var before = service.Snapshot();

        service.MarkNetworkQuestSourceStale();
        Assert.Equal(visible, service.ActiveId);
        AssertPreserved(before.Progress["dungeon"], service.Snapshot().Progress["dungeon"], ProgressFreshness.Unknown);
        service.Select(bound);
        Assert.Equal(ProgressFreshness.Stale, service.Snapshot().Progress["dungeon"].RawFreshness);
        Assert.Equal(ProgressFreshness.Fresh, service.Snapshot().Progress["teleport"].RawFreshness);
        service.Select(visible);
        Assert.True(service.ApplyNetworkQuests(Quests(2), Context(4)));
        Assert.Equal(visible, service.ActiveId);
        service.Select(bound);
        Assert.Equal(ProgressFreshness.Fresh, service.Snapshot().Progress["dungeon"].RawFreshness);
        Assert.Equal((byte)2, service.Snapshot().Progress["dungeon"].QuestStatus);
    }

    [Theory]
    [InlineData("pet")]
    [InlineData("quest")]
    [InlineData("teleport")]
    public void SourceInvalidationStorageFailureCannotUnbindTheSessionOrBlockItsNextValidMessage(string source)
    {
        using var folder = new Folder();
        if (source == "pet")
        {
            var service = folder.Progress();
            Assert.True(service.BindNetworkContext(Context(1)));
            Assert.True(service.ApplyNetworkSnapshot(Pets(1, 3), Context(2)));
            System.IO.File.Delete(folder.File("progress.json"));
            Directory.CreateDirectory(folder.File("progress.json"));
            service.MarkNetworkPetSourceStale();
            Assert.True(service.ApplyNetworkGain([new(Pet.Id, 2)], Context(3)));
            Assert.Equal(10, service.Souls(PetId));
            Assert.Single(service.Recent());
            Assert.False(service.ApplyNetworkGain([new(Pet.Id, 2)], Context(3)));
        }
        else
        {
            var service = folder.Exploration();
            service.BindNetworkContext(Context(1));
            Assert.True(service.ApplyNetworkQuests(Quests(), Context(2)));
            Assert.True(service.ApplyNetworkTeleports(Teleports(), Context(3)));
            System.IO.File.Delete(folder.File("exploration.json"));
            Directory.CreateDirectory(folder.File("exploration.json"));
            if (source == "quest") service.MarkNetworkQuestSourceStale();
            else service.MarkNetworkTeleportSourceStale();
            Assert.True(service.ApplyNetworkQuests(Quests(2), Context(4)));
            Assert.True(service.ApplyNetworkTeleports(Teleports(false), Context(5)));
            Assert.False(service.ApplyNetworkQuests(Quests(), Context(4)));
            Assert.False(service.ApplyNetworkTeleports(Teleports(), Context(5)));
        }
    }

    [Fact]
    public void RepeatedSourceInvalidationDoesNotPublishAnotherChangeAndTrueSessionInvalidationStillBlocksOldCallbacks()
    {
        using var folder = new Folder();
        var progress = folder.Progress();
        var exploration = folder.Exploration();
        Assert.True(progress.BindNetworkContext(Context(1)));
        exploration.BindNetworkContext(Context(1));
        Assert.True(progress.ApplyNetworkSnapshot(Pets(1, 3), Context(2)));
        Assert.True(exploration.ApplyNetworkQuests(Quests(), Context(2)));
        Assert.True(exploration.ApplyNetworkTeleports(Teleports(), Context(3)));
        var petChanges = 0;
        var explorationChanges = 0;
        progress.Changed += () => petChanges++;
        exploration.Changed += () => explorationChanges++;
        for (var i = 0; i < 2; i++)
        {
            progress.MarkNetworkPetSourceStale();
            exploration.MarkNetworkQuestSourceStale();
            exploration.MarkNetworkTeleportSourceStale();
        }
        Assert.Equal(1, petChanges);
        Assert.Equal(2, explorationChanges);
        progress.MarkNetworkStale();
        exploration.MarkNetworkStale();
        Assert.False(progress.ApplyNetworkGain([new(Pet.Id, 2)], Context(4)));
        Assert.False(exploration.ApplyNetworkQuests(Quests(), Context(4)));
        Assert.False(exploration.ApplyNetworkTeleports(Teleports(), Context(5)));
        Assert.True(progress.BindNetworkContext(Context(1, generation: 2)));
        exploration.BindNetworkContext(Context(1, generation: 2));
        Assert.True(progress.ApplyNetworkGain([new(Pet.Id, 2)], Context(2, generation: 2)));
        Assert.True(exploration.ApplyNetworkQuests(Quests(), Context(2, generation: 2)));
        Assert.True(exploration.ApplyNetworkTeleports(Teleports(), Context(3, generation: 2)));
    }

    private static void AssertPreserved(ExplorationPlaceProgress before, ExplorationPlaceProgress after, ProgressFreshness rawFreshness)
    {
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Freshness, after.Freshness);
        Assert.Equal(before.Source, after.Source);
        Assert.Equal(before.ObservedAt, after.ObservedAt);
        Assert.Equal(before.ManualOverride, after.ManualOverride);
        Assert.Equal(before.QuestStatus, after.QuestStatus);
        Assert.Equal(before.QuestStage, after.QuestStage);
        Assert.Equal(before.QuestCounters, after.QuestCounters);
        Assert.Equal(before.QuestOrdinal, after.QuestOrdinal);
        Assert.Equal(before.TeleportPresent, after.TeleportPresent);
        Assert.Equal(before.RawSource, after.RawSource);
        Assert.Equal(before.RawObservedAt, after.RawObservedAt);
        Assert.Equal(rawFreshness, after.RawFreshness);
    }
}
