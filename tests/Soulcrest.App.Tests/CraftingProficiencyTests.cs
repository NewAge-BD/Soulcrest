using System.Collections.Immutable;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CraftingProficiencyTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private const string Profile = "11111111-1111-1111-1111-111111111111";
    private const string OtherProfile = "22222222-2222-2222-2222-222222222222";
    private static readonly ImmutableDictionary<byte, CharacterCraftingProficiency> Values =
        ImmutableDictionary<byte, CharacterCraftingProficiency>.Empty.Add(1, new(1, 12, 1200, 2));

    [Fact]
    public void QualifiedOwnEntryIsProfileBoundPersistedAndSavedAfterRestartWithoutBeingFresh()
    {
        using var f = new Fixture();
        f.Source.Publish(Evidence());
        Assert.Equal(Profile, f.State.Snapshot.ProfileId);
        Assert.True(f.State.Snapshot.IsFresh); Assert.Equal(12, f.State.Snapshot.Level(1));
        Assert.Null(f.State.Snapshot.Level(2)); Assert.False(f.State.Snapshot.UsingSaved);
        var store = new CraftingProficiencyStore(f.Path);
        Assert.Equal(12, store.Read(Profile)!.Proficiencies[1].Level);
        using var offline = new CraftingProficiencyStateService(new Source(), _ => null, () => Profile, store);
        Assert.False(offline.Snapshot.IsFresh); Assert.True(offline.Snapshot.UsingSaved);
        Assert.Equal(At, offline.Snapshot.ObservedAt); Assert.Equal(12, offline.Snapshot.Level(1));
        var text = File.ReadAllText(f.Path);
        Assert.DoesNotContain("Synthetic", text); Assert.DoesNotContain("CharacterId", text);
        Assert.DoesNotContain("EntityKey", text); Assert.DoesNotContain("ServerId", text);
    }

    [Theory]
    [InlineData("stopped")] [InlineData("gap")] [InlineData("stale")] [InlineData("wrong-source")]
    [InlineData("wrong-generation")] [InlineData("no-own")]
    public void UnqualifiedEvidenceCannotCreateHistoryOrFreshValues(string mode)
    {
        using var f = new Fixture(); var evidence = Evidence();
        if (mode == "stopped") f.Source.Running = false;
        if (mode is "gap" or "no-own") evidence = evidence with { Character = null };
        if (mode == "wrong-generation") evidence = evidence with { Generation = 8 };
        if (mode == "stale") evidence = evidence with { CraftingProficiencies = evidence.CraftingProficiencies with { IsFresh = false } };
        if (mode == "wrong-source") evidence = evidence with { CraftingProficiencies = evidence.CraftingProficiencies with { Source = "unqualified" } };
        f.Source.Publish(evidence);
        Assert.False(f.State.Snapshot.IsKnown); Assert.Null(f.State.Saved); Assert.False(File.Exists(f.Path));
    }

    [Fact]
    public void CharacterSelectionAndResolverRacesDoNotLeakOrSaveAnotherProfilesLiveState()
    {
        using var f = new Fixture(); f.Source.Publish(Evidence()); var first = f.State.Snapshot;
        f.Selected = OtherProfile;
        Assert.True(f.State.Snapshot.IsFresh); Assert.Equal(Profile, f.State.Snapshot.ProfileId); Assert.Null(f.State.Saved);
        f.Selected = Profile;
        f.Resolve = own => { f.Source.CombatSnapshot = Evidence() with { Generation = 8 }; return new(Profile, "Unused"); };
        Assert.False(f.State.Snapshot.IsFresh);
        Assert.Equal(12, first.Level(1));
        Assert.Null(new CraftingProficiencyStore(f.Path).Read(OtherProfile));
        f.State.Dispose(); Assert.Equal(CraftingProficiencySnapshot.Empty, f.State.Snapshot); Assert.Null(f.State.Saved);
    }

    [Fact]
    public void FailedAtomicSaveRetainsThePreviousHistoryAndCurrentLiveFactRemainsAvailable()
    {
        using var f = new Fixture();
        var store = new CraftingProficiencyStore(f.Path, (_, _) => throw new IOException("synthetic failure"));
        using var state = new CraftingProficiencyStateService(f.Source, _ => new(Profile, "Unused"), () => Profile, store);
        f.Source.Publish(Evidence());
        Assert.True(state.Snapshot.IsFresh); Assert.NotNull(state.StorageError); Assert.Null(state.Saved);
    }

    [Fact]
    public void CorruptOrUnknownHistoryIsPreservedAndNeverSilentlyOverwritten()
    {
        using var f = new Fixture(); File.WriteAllText(f.Path, "{\"SchemaVersion\":999,\"Profiles\":[]}");
        var before = File.ReadAllText(f.Path); var store = new CraftingProficiencyStore(f.Path);
        Assert.False(store.IsWritable); Assert.Null(store.Read(Profile));
        Assert.Throws<InvalidDataException>(() => store.Save(new(Profile, At, Values)));
        Assert.Equal(before, File.ReadAllText(f.Path));
    }

    [Fact]
    public void ConcurrentHistoryWritesRetainLatestFactsAndDoNotOverwriteOtherProfiles()
    {
        using var f = new Fixture(); var store = new CraftingProficiencyStore(f.Path);
        store.Save(new(OtherProfile, At, Values));
        Parallel.For(1, 20, second => store.Save(new(Profile, At.AddSeconds(second),
            Values.SetItem(1, new(1, (ushort)second, second * 100L, 0)))));
        var reload = new CraftingProficiencyStore(f.Path);
        Assert.True(reload.IsWritable); Assert.Equal(19, reload.Read(Profile)!.Proficiencies[1].Level);
        Assert.Equal(12, reload.Read(OtherProfile)!.Proficiencies[1].Level);
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(f.Path)!, "*.tmp"));
    }

    [Fact]
    public void UnrelatedCombatUpdatesDoNotWriteTheSameCraftingDomainAgain()
    {
        using var f = new Fixture(); var saves = 0;
        var store = new CraftingProficiencyStore(f.Path, (_, _) => Interlocked.Increment(ref saves));
        using var state = new CraftingProficiencyStateService(f.Source, _ => new(Profile, "Unused"), () => Profile, store);
        var evidence = Evidence(); f.Source.Publish(evidence);
        f.Source.Publish(evidence with { Sequence = 2, CurrentCombatScore = 4000 });
        f.Source.Publish(evidence with { Sequence = 3, CurrentCombatScore = 5000 });
        Assert.Equal(1, saves);
    }

    [Fact]
    public void EffectiveProficiencyChangesPublishOnceWhileUnrelatedAndReentrantNetworkUpdatesStaySilent()
    {
        using var f = new Fixture(); var changes = 0;
        var evidence = Evidence();
        f.State.Changed += () =>
        {
            changes++;
            if (changes == 1) f.Source.Publish(evidence with { CurrentCombatScore = 4000 });
        };
        f.Source.Publish(evidence);
        Assert.Equal(1, changes);
        f.Source.Publish(evidence with { Sequence = 99, CurrentCombatScore = 5000 });
        Assert.Equal(1, changes);
        f.Source.Publish(evidence with { CraftingProficiencies = evidence.CraftingProficiencies with
            { Value = Values.ToDictionary(pair => pair.Key, pair => pair.Value with { }).ToImmutableDictionary() } });
        Assert.Equal(1, changes);
        var nextAt = At.AddSeconds(1);
        var next = evidence with
        {
            Generation = 8, Character = evidence.Character! with { Generation = 8, ObservedAt = nextAt },
            CraftingProficiencies = evidence.CraftingProficiencies with { ObservedAt = nextAt, Sequence = 2 }
        };
        f.Source.Publish(next); Assert.Equal(2, changes);
        f.Source.Running = false; f.Source.Publish(next);
        Assert.Equal(3, changes); Assert.True(f.State.Snapshot.UsingSaved);
        f.Source.Publish(next with { CurrentCombatScore = 6000 }); Assert.Equal(3, changes);
        f.Selected = OtherProfile; f.Source.Publish(next);
        Assert.Equal(4, changes); Assert.False(f.State.Snapshot.IsKnown);
        f.Selected = Profile; f.Source.Publish(next);
        Assert.Equal(5, changes); Assert.True(f.State.Snapshot.UsingSaved);
    }

    [Fact]
    public void PersistenceFailureDoesNotRetryOrRepublishOnEveryCombatEventAndNextOwnFactCanRecover()
    {
        using var f = new Fixture(); var writes = 0; var fail = true; var changes = 0;
        var store = new CraftingProficiencyStore(f.Path, (_, _) =>
        {
            writes++;
            if (fail) throw new IOException("synthetic failure");
        });
        using var state = new CraftingProficiencyStateService(f.Source, _ => new(Profile, "Unused"), () => Profile, store);
        state.Changed += () => changes++;
        var evidence = Evidence(); f.Source.Publish(evidence);
        Assert.Equal(1, writes); Assert.Equal(1, changes); Assert.NotNull(state.StorageError);
        f.Source.Publish(evidence with { CurrentCombatScore = 7000 });
        Assert.Equal(1, writes); Assert.Equal(1, changes);
        fail = false;
        f.Source.Publish(evidence with { CraftingProficiencies = evidence.CraftingProficiencies with { Sequence = 2 } });
        Assert.Equal(2, writes); Assert.Equal(2, changes); Assert.Null(state.StorageError);
    }

    [Theory]
    [InlineData("gap")] [InlineData("stop")] [InlineData("prepare")]
    public void ProductiveNetworkBoundariesInvalidateLiveCraftingWhileKeepingOnlyHistoricalValues(string boundary)
    {
        using var f = new Fixture();
        var root = System.IO.Path.GetDirectoryName(f.Path)!;
        var exploration = new ExplorationService(System.IO.Path.Combine(root, "exploration.json"), []);
        using var network = new NetworkLootService(new ProgressService(System.IO.Path.Combine(root, "progress.json"), new PetCatalog([])), exploration);
        typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(network, true);
        using var state = new CraftingProficiencyStateService(network, exploration, new CraftingProficiencyStore(f.Path));
        network.ProcessProgressMessage("synthetic-own", Own(), At);
        Assert.True(state.Snapshot.IsFresh); Assert.Equal(12, state.Snapshot.Level(1));
        if (boundary == "gap") network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(1));
        else if (boundary == "stop") network.Stop();
        else network.ProcessProgressMessage("synthetic-own", Packet(0x3621, w => w.Write(new byte[46])), At.AddSeconds(1));
        Assert.False(state.Snapshot.IsFresh); Assert.True(state.Snapshot.UsingSaved);
        Assert.Equal(12, state.Snapshot.Level(1));
        Assert.False(network.CombatSnapshot.CraftingProficiencies.IsFresh);
    }

    private static NetworkCharacterCombatSnapshot Evidence() => new()
    {
        Character = new(new(101, 501, 300, "SyntheticAlpha"), 7) { ObservedAt = At }, Generation = 7,
        CraftingProficiencies = new(Values, NetworkCombatSourceStatus.Complete, true, At, 1, CraftingProficiencyStateService.LiveSource)
    };
    private sealed class Source : INetworkCharacterCombatSource
    {
        public bool Running { get; set; } = true;
        public NetworkCharacterCombatSnapshot CombatSnapshot { get; set; } = NetworkCharacterCombatSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        public void Publish(NetworkCharacterCombatSnapshot snapshot) { CombatSnapshot = snapshot; Changed?.Invoke(); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-crafting-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "crafting.json");
        public Source Source { get; } = new();
        public string Selected { get; set; } = Profile;
        public Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> Resolve { get; set; } = _ => new(Profile, "Unused");
        public CraftingProficiencyStateService State { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            State = new(Source, own => Resolve(own), () => Selected, new CraftingProficiencyStore(Path));
        }
        public void Dispose() { State.Dispose(); Directory.Delete(_root, true); }
    }
    private static byte[] Own() => Packet(0x3633, w =>
    {
        V(w, 300); w.Write(0); w.Write((byte)1);
        var name = Encoding.UTF8.GetBytes("SyntheticAlpha"); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)501); w.Write(31); w.Write(new byte[36]); V(w, 0);
        V(w, 1); w.Write((byte)1); w.Write((ushort)12); w.Write(1200L); w.Write((ushort)2);
        w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0); w.Write(new byte[52]);
        V(w, 0); V(w, 0); V(w, 0); w.Write(new byte[12]); w.Write(101L); V(w, 0); w.Write((byte)1);
        w.Write(new byte[16]); w.Write(12345L); w.Write(67890L); V(w, 0); V(w, 0);
    });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer); return bytes.ToArray();
    }
    private static void V(BinaryWriter w, uint value)
    {
        while (value >= 128) { w.Write((byte)(value | 128)); value >>= 7; }
        w.Write((byte)value);
    }
}
