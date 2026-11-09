using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pantheon;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PantheonStateServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly PantheonDecor Colossus = PantheonCatalog.Default.Items.First(item => item.Category == PantheonDecorCategory.Colossus);
    private static readonly PantheonDecor Artwork = PantheonCatalog.Default.Items.First(item => item.Category == PantheonDecorCategory.Artwork);
    private static ImmutableDictionary<int, byte> Decor(params (int Id, byte Slot)[] values) => values.ToImmutableDictionary(row => row.Id, row => row.Slot);

    [Fact]
    public void ACompleteOwnListSeparatesOwnedAndEquippedAndReloadsAsSavedOnly()
    {
        using var f = new Fixture(); f.Login(101, 1, Decor((Colossus.Id, 17), (Artwork.Id, 0)));
        var live = f.Service.Snapshot;
        Assert.Equal(PantheonSourceKind.Live, live.SourceKind); Assert.True(live.IsOwnershipKnown);
        Assert.Equal(2, live.OwnedIds.Count); Assert.Equal(17, live.Slots.Count);
        Assert.Equal(Colossus.Id, live.Slots.Single(slot => slot.SlotId == 17).DecorId);
        Assert.True(live.AreEffectsKnown); Assert.Equal(Colossus.Effects.OrderBy(effect => effect.StatId), live.EquippedEffects);
        Assert.Null(live.ActivePreset); Assert.Empty(live.KnownPresets);
        var reloaded = new ExplorationService(f.Path, []);
        using var offline = new PantheonStateService(new Source { Running = false }, new Source { Running = false }, reloaded);
        Assert.Equal(PantheonSourceKind.Saved, offline.Snapshot.SourceKind);
        Assert.True(offline.Snapshot.OwnedIds.SetEquals(live.OwnedIds));
        Assert.Equal(At, offline.Snapshot.ObservedAt);
        var json = File.ReadAllText(f.Path);
        Assert.DoesNotContain("EntityKey", json); Assert.DoesNotContain("Generation", json);
        Assert.DoesNotContain("PantheonSnapshot", json);
    }

    [Fact]
    public void MissingListsStayUnknownButAConfirmedEmptyListMeansZeroOwnedAndKnownEmptySlots()
    {
        using var f = new Fixture();
        Assert.False(f.Service.Snapshot.IsOwnershipKnown); Assert.False(f.Service.Snapshot.AreEffectsKnown);
        f.Login(101, 1, Decor());
        Assert.True(f.Service.Snapshot.IsOwnershipKnown); Assert.Empty(f.Service.Snapshot.OwnedIds);
        Assert.True(f.Service.Snapshot.AreEffectsKnown); Assert.Empty(f.Service.Snapshot.EquippedEffects);
        Assert.All(f.Service.Snapshot.Slots, slot => { Assert.True(slot.IsKnown); Assert.Null(slot.DecorId); });
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("stale")]
    [InlineData("source")]
    [InlineData("no-time")]
    [InlineData("generation")]
    [InlineData("own")]
    public void UnqualifiedSourcesCannotBecomeCurrentOrOverwriteTheSavedProfile(string variant)
    {
        using var f = new Fixture(); f.Login(101, 1, Decor((Colossus.Id, 17)));
        var previous = f.Profiles.TryGetSavedPantheon()!.State;
        var value = f.Source.PantheonSnapshot;
        var domain = value.Decorations with { Value = new(Decor((Artwork.Id, 1))), Sequence = 2, ObservedAt = At.AddSeconds(1) };
        f.Source.PantheonSnapshot = variant switch
        {
            "partial" => value with { Sequence = 2, Decorations = domain with { Status = NetworkCombatSourceStatus.Partial } },
            "stale" => value with { Sequence = 2, Decorations = domain with { IsFresh = false } },
            "source" => value with { Sequence = 2, Decorations = domain with { Source = "unqualified" } },
            "no-time" => value with { Sequence = 2, Decorations = domain with { ObservedAt = null } },
            "generation" => value with { Sequence = 2, Generation = 2, Decorations = domain },
            _ => value with { Sequence = 2, Character = new(value.Character!.Identity, 1), Decorations = domain }
        };
        f.Source.Raise();
        Assert.Equal(PantheonSourceKind.Saved, f.Service.Snapshot.SourceKind);
        Assert.Equal(previous, f.Profiles.TryGetSavedPantheon()!.State);
    }

    [Fact]
    public void CharacterChangesAndManualProfileSelectionNeverShareCurrentEquipment()
    {
        using var f = new Fixture(); f.Login(101, 1, Decor((Colossus.Id, 17)));
        var first = f.Profiles.ActiveId;
        f.Login(202, 2, Decor((Artwork.Id, 1)));
        var second = f.Profiles.ActiveId;
        Assert.NotEqual(first, second); Assert.DoesNotContain(Colossus.Id, f.Service.Snapshot.OwnedIds);
        f.Profiles.Select(first);
        Assert.Equal(PantheonSourceKind.Saved, f.Service.Snapshot.SourceKind);
        Assert.Contains(Colossus.Id, f.Service.Snapshot.OwnedIds); Assert.DoesNotContain(Artwork.Id, f.Service.Snapshot.OwnedIds);
        f.Profiles.Select(second); Assert.Equal(PantheonSourceKind.Live, f.Service.Snapshot.SourceKind);
        Assert.True(f.Profiles.Remove(second));
        Assert.NotEqual(PantheonSourceKind.Live, f.Service.Snapshot.SourceKind);
    }

    [Fact]
    public void SameTimestampSequenceOrderingAndDuplicateNotificationsPreserveTheLatestObservation()
    {
        using var f = new Fixture(); f.Login(101, 1, Decor((Colossus.Id, 17)));
        var own = f.Source.CombatSnapshot.Character!; var profile = f.Profiles.ActiveId;
        var next = f.Source.PantheonSnapshot.Decorations with { Value = new(Decor((Artwork.Id, 1))), Sequence = 3 };
        Assert.True(f.Profiles.ApplyNetworkPantheon(own, profile, next));
        Assert.False(f.Profiles.ApplyNetworkPantheon(own, profile, next with { Sequence = 2, Value = new(Decor()) }));
        Assert.Contains(Artwork.Id, f.Profiles.TryGetSavedPantheon()!.State.Decorations.Keys);
        f.Source.PantheonSnapshot = f.Source.PantheonSnapshot with { Sequence = 3, Decorations = next };
        f.Source.Raise(); var before = f.Service.Snapshot; var events = 0; f.Service.Changed += () => events++;
        f.Source.Raise(); Assert.Same(before, f.Service.Snapshot); Assert.Equal(0, events);
    }

    [Fact]
    public void UnknownIdsWrongCategoriesAndDuplicateSlotsDoNotInventAnEffectSum()
    {
        foreach (var values in new[] { Decor((int.MaxValue, 17)), Decor((Artwork.Id, 17)),
            Decor((Artwork.Id, 1), (Colossus.Id, 1)), Decor((Artwork.Id, 255)) })
        {
            var result = PantheonStateService.Project("synthetic-profile", values, At, PantheonSourceKind.Live);
            Assert.True(result.IsOwnershipKnown); Assert.False(result.AreEffectsKnown); Assert.Empty(result.EquippedEffects);
        }
        var unknownUnplaced = PantheonStateService.Project("synthetic-profile", Decor((int.MaxValue, 0), (Colossus.Id, 17)), At, PantheonSourceKind.Live);
        Assert.True(unknownUnplaced.AreEffectsKnown); Assert.Contains(int.MaxValue, unknownUnplaced.OwnedIds);
    }

    [Theory]
    [InlineData("profile", false)]
    [InlineData("profile", true)]
    [InlineData("stop", false)]
    [InlineData("gap", false)]
    [InlineData("source", false)]
    [InlineData("own", false)]
    public async Task AParallelGetterCannotReturnCachedAuthorityWhileRefreshIsBlockedAndItsScopeChanges(string change, bool saved)
    {
        using var f = new Fixture(); f.Login(101, 1, Decor((Colossus.Id, 17)));
        var first = f.Profiles.ActiveId;
        f.Profiles.Add("Synthetic other profile"); var second = f.Profiles.ActiveId;
        f.Profiles.Select(first);
        if (saved) { f.Source.Running = false; f.Source.Raise(); }
        Assert.Equal(saved ? PantheonSourceKind.Saved : PantheonSourceKind.Live, f.Service.Snapshot.SourceKind);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Source.OnPantheonRead = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var producer = Task.Run(f.Source.Raise);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            switch (change)
            {
                case "profile": f.Profiles.Select(second); break;
                case "stop": f.Source.Running = false; f.Source.Raise(); break;
                case "gap": f.Profiles.MarkNetworkStale(); break;
                case "source":
                    f.Source.PantheonSnapshot = f.Source.PantheonSnapshot with { Sequence = 2 };
                    f.Source.Raise(); break;
                case "own":
                    f.Source.CombatSnapshot = f.Source.CombatSnapshot with
                    { Character = new(f.Source.CombatSnapshot.Character!.Identity with { CharacterId = 202 }, 2), Generation = 2 };
                    break;
            }
            var blocked = f.Service.Snapshot;
            Assert.Equal(PantheonSourceKind.Unknown, blocked.SourceKind);
            Assert.False(blocked.IsOwnershipKnown); Assert.Empty(blocked.OwnedIds); Assert.Empty(blocked.Slots);
        }
        finally { release.Set(); await producer; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnknownOrWrongCategoryPlacementKeepsOwnershipButCannotBecomeAKnownSlotObject(bool unknownId)
    {
        var id = unknownId ? int.MaxValue : Artwork.Id;
        var raw = Decor((id, 17));
        var result = PantheonStateService.Project("synthetic-profile", raw, At, PantheonSourceKind.Live);
        Assert.True(result.IsOwnershipKnown); Assert.Contains(id, result.OwnedIds);
        var slot = Assert.Single(result.Slots, slot => slot.SlotId == 17);
        Assert.False(slot.IsKnown); Assert.Null(slot.DecorId);
        Assert.False(result.AreEffectsKnown); Assert.Empty(result.EquippedEffects);
        Assert.Equal((byte)17, raw[id]);
        Assert.All(result.Slots.Where(slot => slot.SlotId != 17), slot => { Assert.True(slot.IsKnown); Assert.Null(slot.DecorId); });
    }

    [Fact]
    public void GapStopsLiveAuthorityAndCallbacksCannotKeepAnUnboundOwnerCurrent()
    {
        using var f = new Fixture(); f.Login(101, 1, Decor((Colossus.Id, 17)));
        f.Profiles.MarkNetworkStale(); Assert.Equal(PantheonSourceKind.Saved, f.Service.Snapshot.SourceKind);
        Assert.False(f.Profiles.ApplyNetworkPantheon(f.Source.CombatSnapshot.Character!, f.Profiles.ActiveId, f.Source.PantheonSnapshot.Decorations));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-pantheon-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "exploration.json");
        internal Source Source { get; } = new();
        internal ExplorationService Profiles { get; }
        internal PantheonStateService Service { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_directory); Profiles = new(Path, []); Service = new(Source, Source, Profiles);
        }
        internal void Login(long id, long generation, ImmutableDictionary<int, byte> values)
        {
            var identity = new OwnCharacterIdentity(id, 501, 300, "Synthetic character " + id) { ClassId = "Cleric" };
            Profiles.BindNetworkContext(new(identity, generation, 1, At));
            var own = new OwnCharacterContext(identity, generation) { ObservedAt = At };
            Source.CombatSnapshot = new() { Character = own, Generation = generation, Sequence = 1, ClassId = "Cleric" };
            Source.PantheonSnapshot = new() { Character = own, Generation = generation, Sequence = 1,
                Decorations = new(new(values), NetworkCombatSourceStatus.Complete, true, At, 1, "own-pantheon-decoration-list") };
            Source.Raise();
        }
        public void Dispose() { Service.Dispose(); Directory.Delete(_directory, true); }
    }

    private sealed class Source : INetworkPantheonSource, INetworkCharacterCombatSource
    {
        public bool Running { get; set; } = true;
        private NetworkPantheonSnapshot _pantheon = NetworkPantheonSnapshot.Empty;
        private Action? _onPantheonRead;
        internal Action? OnPantheonRead { set => Interlocked.Exchange(ref _onPantheonRead, value); }
        public NetworkPantheonSnapshot PantheonSnapshot
        {
            get { var value = _pantheon; Interlocked.Exchange(ref _onPantheonRead, null)?.Invoke(); return value; }
            set => _pantheon = value;
        }
        public NetworkCharacterCombatSnapshot CombatSnapshot { get; set; } = NetworkCharacterCombatSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        internal void Raise() => Changed?.Invoke();
    }
}
