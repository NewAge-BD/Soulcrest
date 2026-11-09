using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GenusInsightPersistenceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-genus-history-" + Guid.NewGuid().ToString("N"));
        internal string Path { get; }
        internal ExplorationService Service { get; }
        internal Fixture()
        { Directory.CreateDirectory(_root); Path = System.IO.Path.Combine(_root, "exploration.json"); Service = new(Path, []); }
        internal OwnCharacterContext Bind(long character = 101, int server = 501, long generation = 1, int second = 0)
        {
            var identity = new OwnCharacterIdentity(character, server, 300, "Synthetic " + character);
            Service.BindNetworkContext(new(identity, generation, 0, At.AddSeconds(second)));
            return new(identity, generation) { ObservedAt = At.AddSeconds(second) };
        }
        public void Dispose() { Directory.Delete(_root, true); }
    }

    [Fact]
    public void CompleteBoardsAreStoredOnceWithAllPresetsRawPrecisionAndSeparateProfileSelection()
    {
        using var f = new Fixture(); var own = f.Bind(); var profile = f.Service.ActiveId;
        Assert.Null(f.Service.TryGetSavedGenusInsight());
        var snapshot = Domain();
        Assert.True(f.Service.ApplyNetworkGenusInsight(own, profile, snapshot));
        var saved = Assert.IsType<ProfileGenusInsightSnapshot>(new ExplorationService(f.Path, []).TryGetSavedGenusInsight(profile));
        Assert.Equal(profile, saved.ProfileId); Assert.Equal((byte)1, saved.ActivePreset);
        Assert.Equal(At.AddSeconds(1), saved.ObservedAt); Assert.Equal(At.AddSeconds(1), saved.ActivePresetObservedAt);
        Assert.Equal(5, saved.Boards.Count);
        Assert.All(saved.Boards.Values, board => { Assert.Equal(3, board.Panels.Count); Assert.All(board.Panels.Values, slots => Assert.Equal(9, slots.Length)); });
        var raw = saved.Boards[2].Panels[3][0];
        Assert.Equal(ushort.MaxValue, raw.StatType); Assert.Equal(byte.MaxValue, raw.Grade);
        Assert.Equal(long.MinValue, raw.Value); Assert.True(raw.IsLocked); Assert.False(raw.IsActivated);
        Assert.Equal(long.MaxValue, saved.Boards[6].Panels[3][8].Value);
        using var document = JsonDocument.Parse(File.ReadAllText(f.Path));
        var global = document.RootElement.GetProperty("GenusInsight");
        Assert.Equal(new[] { "Boards", "ObservedAt", "Source" }, global.EnumerateObject().Select(property => property.Name).Order());
        var character = document.RootElement.GetProperty("Characters")[0];
        Assert.False(character.TryGetProperty("GenusInsight", out _));
        Assert.Equal(new[] { "ObservedAt", "Preset", "Source" }, character.GetProperty("GenusInsightPreset").EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public void IndependentStorageKeysAreRetainedAndOnlyUniqueValueCreatureTypesDefineTheFiveBoards()
    {
        using var f = new Fixture(); var own = f.Bind(); var profile = f.Service.ActiveId;
        var domain = Domain();
        var storageKeys = domain.Value!.GenusBoards.ToImmutableDictionary(pair => (byte)(pair.Key + 40), pair => pair.Value);
        domain = domain with { Value = domain.Value with { GenusBoards = storageKeys } };
        Assert.True(f.Service.ApplyNetworkGenusInsight(own, profile, domain));
        var saved = new ExplorationService(f.Path, []).TryGetSavedGenusInsight(profile)!;
        Assert.Equal(new byte[] { 42, 43, 44, 45, 46 }, saved.Boards.Keys.Order());
        Assert.Equal(new byte[] { 2, 3, 4, 5, 6 }, saved.Boards.Values.Select(board => board.CreatureType).Order());
        Assert.Equal(long.MinValue, saved.Boards[42].Panels[3][0].Value);
        var before = File.ReadAllText(f.Path);
        var duplicateTypes = storageKeys.SetItem(42, storageKeys[42] with { CreatureType = 3 });
        Assert.False(f.Service.ApplyNetworkGenusInsight(own, profile, domain with
            { Sequence = 2, Value = domain.Value with { GenusBoards = duplicateTypes } }));
        Assert.Equal(before, File.ReadAllText(f.Path));
    }

    [Fact]
    public void SharedRollsAreVisibleAcrossProfilesWhileTheirPresetsRemainIndependent()
    {
        using var f = new Fixture(); var firstOwn = f.Bind(); var first = f.Service.ActiveId;
        Assert.True(f.Service.ApplyNetworkGenusInsight(firstOwn, first, Domain()));
        var secondOwn = f.Bind(102, generation: 2, second: 10); var second = f.Service.ActiveId;
        var sharedBeforeOwnList = Assert.IsType<ProfileGenusInsightSnapshot>(f.Service.TryGetSavedGenusInsight(second));
        Assert.Null(sharedBeforeOwnList.ActivePreset);
        Assert.True(f.Service.ApplyNetworkGenusInsight(secondOwn, second, Domain(second: 11, selected: 2, value: 47)));
        var savedFirst = f.Service.TryGetSavedGenusInsight(first)!; var savedSecond = f.Service.TryGetSavedGenusInsight(second)!;
        Assert.Same(savedFirst.Boards, savedSecond.Boards);
        Assert.Equal(47L, savedFirst.Boards[2].Panels[1][0].Value);
        Assert.Equal((byte)1, savedFirst.ActivePreset); Assert.Equal((byte)2, savedSecond.ActivePreset);
        f.Service.Select(first);
        Assert.False(f.Service.ApplyObservedGenusPreset(firstOwn, first, 3, At.AddSeconds(12)));
        Assert.True(f.Service.ApplyObservedGenusPreset(secondOwn, second, 3, At.AddSeconds(12)));
        Assert.Equal((byte)1, f.Service.TryGetSavedGenusInsight(first)!.ActivePreset);
        Assert.Equal((byte)3, new ExplorationService(f.Path, []).TryGetSavedGenusInsight(second)!.ActivePreset);
        Assert.Null(f.Service.TryGetSavedGenusInsight("unknown-profile"));
    }

    [Fact]
    public void ConfirmedMenuChoiceChangesOnlyTheOwnProfileAndRepeatedFramesDoNotRewriteTheFile()
    {
        using var f = new Fixture(); var own = f.Bind(); var profile = f.Service.ActiveId;
        Assert.False(f.Service.ApplyObservedGenusPreset(own, profile, 3, At.AddSeconds(1)));
        f.Service.ApplyNetworkGenusInsight(own, profile, Domain());
        var boards = f.Service.TryGetSavedGenusInsight()!.Boards;
        Assert.True(f.Service.ApplyObservedGenusPreset(own, profile, 3, At.AddSeconds(2)));
        var saved = f.Service.TryGetSavedGenusInsight()!;
        Assert.Same(boards, saved.Boards); Assert.Equal(At.AddSeconds(1), saved.ObservedAt);
        Assert.Equal(At.AddSeconds(2), saved.ActivePresetObservedAt); Assert.Equal("game-menu-preset", saved.ActivePresetSource);
        var before = File.ReadAllText(f.Path);
        Assert.False(f.Service.ApplyObservedGenusPreset(own, profile, 3, At.AddSeconds(3)));
        Assert.False(f.Service.ApplyObservedGenusPreset(own, profile, 2, At.AddSeconds(1)));
        Assert.False(f.Service.ApplyObservedGenusPreset(own, "unrelated-profile", 2, At.AddSeconds(3)));
        Assert.False(f.Service.ApplyObservedGenusPreset(own with { Generation = 0 }, profile, 2, At.AddSeconds(3)));
        Assert.False(f.Service.ApplyObservedGenusPreset(own, profile, 0, At.AddSeconds(3)));
        Assert.Equal(before, File.ReadAllText(f.Path));
    }

    [Theory]
    [InlineData("partial")] [InlineData("stale")] [InlineData("conflict")] [InlineData("unknown")] [InlineData("source")]
    [InlineData("boards")] [InlineData("panels")] [InlineData("slots")] [InlineData("duplicate")] [InlineData("type")]
    public void IncompleteOrDamagedSourcesNeverEraseHistory(string variant)
    {
        using var f = new Fixture(); var own = f.Bind(); var profile = f.Service.ActiveId;
        f.Service.ApplyNetworkGenusInsight(own, profile, Domain()); var before = File.ReadAllText(f.Path);
        var next = Domain(second: 2, sequence: 2);
        next = variant switch
        {
            "partial" => next with { Status = NetworkCombatSourceStatus.Partial },
            "stale" => next with { IsFresh = false },
            "conflict" => next with { Status = NetworkCombatSourceStatus.Conflict },
            "unknown" => NetworkCombatDomain<PetProgressSnapshot>.Unknown,
            "source" => next with { Source = "catalog-preview" },
            _ => next
        };
        if (next.Value is { } value)
        {
            var boards = value.GenusBoards; var board = boards[2];
            boards = variant switch
            {
                "boards" => boards.Remove(6),
                "panels" => boards.SetItem(2, board with { Panels = board.Panels.Remove(3) }),
                "slots" => boards.SetItem(2, board with { Panels = board.Panels.SetItem(1, board.Panels[1].RemoveAt(8)) }),
                "duplicate" => boards.SetItem(2, board with { Panels = board.Panels.SetItem(1, board.Panels[1].SetItem(8, board.Panels[1][0])) }),
                "type" => boards.SetItem(2, board with { CreatureType = 3 }),
                _ => boards
            };
            next = next with { Value = value with { GenusBoards = boards } };
        }
        Assert.False(f.Service.ApplyNetworkGenusInsight(own, profile, next));
        Assert.Equal(before, File.ReadAllText(f.Path));
        Assert.Equal(5, f.Service.TryGetSavedGenusInsight()!.Boards.Count);
    }

    [Fact]
    public void SameTimestampCallbacksAreOrderedEvenWhenAnEquivalentObservationDoesNotWrite()
    {
        using var f = new Fixture(); var own = f.Bind(); var profile = f.Service.ActiveId;
        Assert.True(f.Service.ApplyNetworkGenusInsight(own, profile, Domain(second: 2, sequence: 3, value: 40)));
        var before = File.ReadAllText(f.Path);
        Assert.False(f.Service.ApplyNetworkGenusInsight(own, profile, Domain(second: 2, sequence: 4, value: 40)));
        Assert.False(f.Service.ApplyNetworkGenusInsight(own, profile, Domain(second: 2, sequence: 3, value: 41)));
        Assert.False(f.Service.ApplyNetworkGenusInsight(own, profile, Domain(second: 1, sequence: 5, value: 42)));
        Assert.Equal(before, File.ReadAllText(f.Path));
        Assert.True(f.Service.ApplyNetworkGenusInsight(own, profile, Domain(second: 2, sequence: 6, value: 43)));
        Assert.Equal(43L, f.Service.TryGetSavedGenusInsight()!.Boards[2].Panels[1][0].Value);
    }

    [Fact]
    public void RollsLocksAndUnknownSelectionsNeverReconfirmOrEraseAnExistingPreset()
    {
        using var f = new Fixture(); var own = f.Bind(); var profile = f.Service.ActiveId;
        f.Service.ApplyNetworkGenusInsight(own, profile, Domain());
        f.Service.ApplyObservedGenusPreset(own, profile, 3, At.AddSeconds(2));
        Assert.True(f.Service.ApplyNetworkGenusInsight(own, profile, Domain(second: 3, sequence: 2, selected: null, source: "own-pet-genus-roll-update")));
        Assert.True(f.Service.ApplyNetworkGenusInsight(own, profile, Domain(second: 4, sequence: 3, selected: 1, source: "own-pet-genus-lock-update")));
        Assert.True(f.Service.ApplyNetworkGenusInsight(own, profile, Domain(second: 5, sequence: 4, selected: null)));
        var saved = f.Service.TryGetSavedGenusInsight()!;
        Assert.Equal((byte)3, saved.ActivePreset); Assert.Equal(At.AddSeconds(2), saved.ActivePresetObservedAt);
        Assert.Equal(At.AddSeconds(5), saved.ObservedAt);
        f.Service.MarkNetworkStale(); var before = File.ReadAllText(f.Path);
        Assert.False(f.Service.ApplyObservedGenusPreset(own, profile, 2, At.AddSeconds(6)));
        Assert.False(f.Service.ApplyNetworkGenusInsight(own, profile, Domain(second: 6, sequence: 5)));
        Assert.Equal(before, File.ReadAllText(f.Path));
    }

    [Fact]
    public void SaveCallbacksRunOutsideTheProfileLockAndReadsExposeOnlyFrozenRawCollections()
    {
        using var f = new Fixture(); var own = f.Bind(); var unlocked = false;
        f.Service.Changed += () => unlocked = Task.Run(() => f.Service.TryGetSavedGenusInsight()).Wait(TimeSpan.FromSeconds(3));
        f.Service.ApplyNetworkGenusInsight(own, f.Service.ActiveId, Domain()); Assert.True(unlocked);
        var saved = f.Service.TryGetSavedGenusInsight()!;
        Assert.Throws<NotSupportedException>(() => ((IDictionary<byte, CharacterGenusBoard>)saved.Boards).Clear());
        Assert.Equal(5, f.Service.TryGetSavedGenusInsight()!.Boards.Count);
    }

    private static NetworkCombatDomain<PetProgressSnapshot> Domain(int second = 1, long sequence = 1, byte? selected = 1,
        long value = 19, string source = "own-pet-genus-list")
    {
        var boards = Enumerable.Range(2, 5).ToImmutableDictionary(type => (byte)type, type => new CharacterGenusBoard((byte)type, 10, 0,
            Enumerable.Range(1, 3).ToImmutableDictionary(panel => (byte)panel, panel => Enumerable.Range(0, 9)
                .Select(slot => type == 2 && panel == 3 && slot == 0 ? new CharacterGenusEffect(0, byte.MaxValue, ushort.MaxValue, long.MinValue, true, false)
                    : new CharacterGenusEffect((byte)slot, 5, 31, type == 6 && panel == 3 && slot == 8 ? long.MaxValue : value, false, true))
                .ToImmutableArray())));
        return new(new PetProgressSnapshot(ImmutableDictionary<int, int>.Empty, ImmutableDictionary<int, int>.Empty)
            { GenusBoards = boards, SelectedGenusPanel = selected }, NetworkCombatSourceStatus.Complete, true, At.AddSeconds(second), sequence, source);
    }
}
