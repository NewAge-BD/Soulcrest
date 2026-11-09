using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using Soulcrest.App.Network;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.GenusInsight;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GenusInsightStateServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private const string SourceName = "own-pet-genus-list";

    [Fact]
    public void FeatureDiagnosisCannotEvaluateOrRevokeTheExistingPresetAuthority()
    {
        using var f = new Fixture(); f.Login(101, 7, 1);
        using var lease = f.State.ObserveMenu(); f.SelectGamePreset(3); f.WaitForProjection();
        var preset = typeof(GenusInsightStateService).GetField("_preset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.State)!;
        var fields = preset.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var before = fields.Select(field => field.GetValue(preset)).ToArray();
        var beforeFile = File.ReadAllBytes(f.Path);
        // No producer callback: a diagnostic read must not perform the normal presentation-side invalidation.
        f.Network.Running = false;
        var contributors = DiagnosticContributors.Create(new DiagnosticServiceRegistry(f.Character, f.State, f.Menu));
        var contributor = contributors.Single(item => item.FeatureIds.Contains("combat-genus-insight"));
        _ = contributor.Collect("combat-genus-insight");
        Assert.Equal(before, fields.Select(field => field.GetValue(preset)).ToArray());
        Assert.Equal(beforeFile, File.ReadAllBytes(f.Path));
    }

    [Fact]
    public void ClosingTheMenuAndReleasingItsLeaseKeepTheLastConfirmedPresetAndAllDisplayedRolls()
    {
        using var f = new Fixture(); f.Login(101, 7, 1);
        var lease = f.State.ObserveMenu(); f.SelectGamePreset(3);
        var open = f.State.Snapshot;
        Assert.Equal((byte)3, open.Preset); Assert.Equal(16m, FirstAttack(open));
        Assert.NotNull(open.ViewedBoard);
        f.Frame(null);
        var closed = f.State.Snapshot;
        Assert.False(closed.MenuVisible); Assert.Null(closed.ViewedBoard);
        Assert.Equal(open.Preset, closed.Preset); Assert.Equal(Values(open), Values(closed));
        lease.Dispose();
        var stopped = f.State.Snapshot;
        Assert.Null(stopped.MenuVisible); Assert.Null(stopped.ViewedBoard);
        Assert.Equal(open.Preset, stopped.Preset); Assert.Equal(Values(open), Values(stopped));
        Assert.Equal((byte)3, f.Exploration.TryGetSavedGenusInsight()!.ActivePreset);
        Assert.False(GenusInsightOverlayContent.Available(stopped));
    }

    [Fact]
    public void RestartRestoresSharedRollsAndTheSelectedProfilesPresetAsHistoryWithoutNetworkAuthority()
    {
        using var f = new Fixture(); var profile = f.Login(101, 7, 1);
        using (f.State.ObserveMenu()) f.SelectGamePreset(3);
        f.Restart();
        var snapshot = f.State.Snapshot;
        Assert.Equal(profile, snapshot.ProfileId); Assert.Equal((byte)3, snapshot.Preset);
        Assert.True(snapshot.IsHistorical); Assert.False(snapshot.IsCurrent);
        Assert.Equal(0, snapshot.Generation); Assert.Equal(0, snapshot.Sequence); Assert.Equal(At, snapshot.ObservedAt);
        Assert.Equal(16m, FirstAttack(snapshot)); Assert.Equal(45, Values(snapshot).Length);
        Assert.All(snapshot.Boards.SelectMany(board => board.Slots), slot => Assert.NotNull(slot.Value));
        Assert.Null(snapshot.ViewedBoard); Assert.False(GenusInsightOverlayContent.Available(snapshot));
        Assert.False(f.Character.Snapshot.IsFresh); Assert.Null(f.Character.Snapshot.Pets.Value);
        var bytes = File.ReadAllBytes(f.Path); _ = f.State.Snapshot; _ = f.State.Snapshot;
        Assert.Equal(bytes, File.ReadAllBytes(f.Path));
    }

    [Fact]
    public void SharedBoardsFollowBothProfilesWhileTheirActivePresetsStayIndependent()
    {
        using var f = new Fixture(); var first = f.Login(101, 7, 1);
        using (f.State.ObserveMenu()) f.SelectGamePreset(3);
        var second = f.Login(102, 8, 2);
        Assert.NotEqual(first, second);
        f.Exploration.Select(first);
        Assert.Equal(second, f.State.Snapshot.ProfileId); Assert.Equal((byte)2, f.State.Snapshot.Preset);
        Assert.Equal(12m, FirstAttack(f.State.Snapshot));
        // A roll changed while using B must also be visible through A's retained preset.
        var updated = FullPets(2); var special = updated.GenusBoards[6]; var third = special.Panels[3];
        special = special with { Panels = special.Panels.SetItem(3, third.SetItem(0, third[0] with { Value = 14 })) };
        updated = updated with { GenusBoards = updated.GenusBoards.SetItem(6, special) };
        f.Publish(new(updated, NetworkCombatSourceStatus.Complete, true, At.AddSeconds(20), 2, SourceName));
        var a = f.Exploration.TryGetSavedGenusInsight(first)!; var b = f.Exploration.TryGetSavedGenusInsight(second)!;
        Assert.Equal((byte)3, a.ActivePreset); Assert.Equal((byte)2, b.ActivePreset);
        Assert.Equal(RawValues(a.Boards), RawValues(b.Boards));
        f.StopNetwork();
        Assert.Equal(first, f.State.Snapshot.ProfileId); Assert.Equal(14m, FirstAttack(f.State.Snapshot));
        f.Exploration.Select(second);
        Assert.Equal(second, f.State.Snapshot.ProfileId); Assert.Equal(12m, FirstAttack(f.State.Snapshot));
        f.Restart();
        Assert.Equal(second, f.State.Snapshot.ProfileId); Assert.Equal((byte)2, f.State.Snapshot.Preset);
        f.Exploration.Select(first);
        Assert.Equal((byte)3, f.State.Snapshot.Preset); Assert.Equal(14m, FirstAttack(f.State.Snapshot));
    }

    [Fact]
    public void AProfileWithoutItsOwnPresetCannotInheritAnotherProfilesActiveBonuses()
    {
        using var f = new Fixture(); var first = f.Login(101, 7, 1);
        using (f.State.ObserveMenu()) f.SelectGamePreset(3);
        var second = f.Login(102, 8, null, includeBoards: false);
        var saved = f.Exploration.TryGetSavedGenusInsight(second)!;
        Assert.Equal(5, saved.Boards.Count); Assert.Null(saved.ActivePreset);
        Assert.Equal((byte)3, f.Exploration.TryGetSavedGenusInsight(first)!.ActivePreset);
        var snapshot = f.State.Snapshot;
        Assert.Equal(second, snapshot.ProfileId); Assert.True(snapshot.IsHistorical); Assert.Null(snapshot.Preset);
        Assert.Equal(CombatKnowledgeStatus.Unknown, snapshot.AppliedEffectsStatus); Assert.Empty(snapshot.AppliedEffects);
        Assert.All(snapshot.Boards.SelectMany(board => board.Slots), slot => Assert.Null(slot.Value));
    }

    [Fact]
    public void AnOldCharacterSnapshotCannotWriteItsPresetIntoEitherCurrentOrPreviouslySelectedProfile()
    {
        using var f = new Fixture(); var first = f.Login(101, 7, 1); var old = f.Character.Snapshot;
        var second = f.Login(102, 8, 2); f.Exploration.Select(first);
        var bytes = File.ReadAllBytes(f.Path);
        f.Character.PersistObservedGenusPreset(old, 3);
        Assert.Equal(bytes, File.ReadAllBytes(f.Path));
        Assert.Equal((byte)1, f.Exploration.TryGetSavedGenusInsight(first)!.ActivePreset);
        Assert.Equal((byte)2, f.Exploration.TryGetSavedGenusInsight(second)!.ActivePreset);
        Assert.Equal(second, f.State.Snapshot.ProfileId); Assert.Equal((byte)2, f.State.Snapshot.Preset);
    }

    [Fact]
    public void AConfirmedOwnMenuCanSelectHistoricalSharedRollsWithoutInventingAFreshGenusBaseline()
    {
        using var f = new Fixture(); var first = f.Login(101, 7, 1);
        var second = f.Login(102, 8, null, includeBoards: false);
        Assert.True(f.Character.Snapshot.IsFresh); Assert.Null(f.Character.Snapshot.Pets.Value);
        var before = f.Exploration.TryGetSavedGenusInsight(second)!; Assert.Null(before.ActivePreset);
        using var lease = f.State.ObserveMenu(); _ = f.State.Snapshot;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            f.Frame(new("cogni", .99, 3));
            if (f.Menu.Snapshot.ViewedPreset == 3) break;
        }
        Assert.Equal((byte)3, f.Menu.Snapshot.ViewedPreset);
        var saved = f.Exploration.TryGetSavedGenusInsight(second)!;
        Assert.Equal((byte)3, saved.ActivePreset); Assert.Equal("game-menu-preset", saved.ActivePresetSource);
        Assert.Equal(before.ObservedAt, saved.ObservedAt); Assert.Equal(RawValues(before.Boards), RawValues(saved.Boards));
        Assert.Equal((byte)1, f.Exploration.TryGetSavedGenusInsight(first)!.ActivePreset);
        var snapshot = f.State.Snapshot;
        Assert.Equal(second, snapshot.ProfileId); Assert.Equal((byte)3, snapshot.Preset); Assert.Equal(16m, FirstAttack(snapshot));
        Assert.True(snapshot.IsHistorical); Assert.False(snapshot.IsCurrent);
        Assert.Equal(0, snapshot.Generation); Assert.Equal(0, snapshot.Sequence);
        Assert.Null(snapshot.ViewedBoard); Assert.False(GenusInsightOverlayContent.Available(snapshot));
        Assert.Null(f.Character.Snapshot.Pets.Value);
    }

    [Theory]
    [InlineData(NetworkCombatSourceStatus.Unknown, false)]
    [InlineData(NetworkCombatSourceStatus.Stale, false)]
    [InlineData(NetworkCombatSourceStatus.Conflict, false)]
    [InlineData(NetworkCombatSourceStatus.Partial, true)]
    public void MissingOrUnqualifiedCurrentGenusDataFallsBackToSavedRowsWithoutErasingTheStore(NetworkCombatSourceStatus status, bool fresh)
    {
        using var f = new Fixture(); f.Login(101, 7, 2);
        var before = f.State.Snapshot; var bytes = File.ReadAllBytes(f.Path);
        f.Publish(new(EmptyPets(), status, fresh, At.AddSeconds(20), 2, SourceName));
        var after = f.State.Snapshot;
        Assert.True(after.IsHistorical); Assert.False(after.IsCurrent);
        Assert.Equal(before.ProfileId, after.ProfileId); Assert.Equal(before.Preset, after.Preset);
        Assert.Equal(Values(before), Values(after)); Assert.Equal(bytes, File.ReadAllBytes(f.Path));
    }

    [Fact]
    public void ANewCompleteListWithUnmappedStatsRemainsVisibleInsteadOfHidingBehindOldSavedValues()
    {
        using var f = new Fixture(); f.Login(101, 7, 1);
        var pets = FullPets(2); var board = pets.GenusBoards[6]; var panel = board.Panels[2];
        board = board with { Panels = board.Panels.SetItem(2, panel.SetItem(0, panel[0] with { StatType = ushort.MaxValue })) };
        pets = pets with { GenusBoards = pets.GenusBoards.SetItem(6, board) };
        f.Publish(new(pets, NetworkCombatSourceStatus.Complete, true, At.AddSeconds(20), 2, SourceName));
        var snapshot = f.State.Snapshot;
        Assert.True(snapshot.IsCurrent); Assert.False(snapshot.IsHistorical); Assert.Equal((byte)2, snapshot.Preset);
        Assert.Null(snapshot.Boards.Single(row => row.Genus == "special").Slots[0].Value);
        Assert.Equal(ushort.MaxValue, f.Exploration.TryGetSavedGenusInsight()!.Boards[6].Panels[2][0].StatType);
    }

    [Fact]
    public void RepeatedReadsAndIdenticalGameFramesDoNotRewriteProfileDataOrChangeGlobalFavorites()
    {
        using var f = new Fixture(); f.Login(101, 7, 1);
        var favorite = new GenusInsightFavoriteKey("special", 1, "FixingDamage"); f.Favorites.Toggle(favorite);
        using var lease = f.State.ObserveMenu(); f.SelectGamePreset(3);
        var bytes = File.ReadAllBytes(f.Path); var preferences = File.ReadAllBytes(f.FavoritesPath);
        var changes = 0; f.Exploration.Changed += () => Interlocked.Increment(ref changes);
        _ = f.State.Snapshot; _ = f.State.Snapshot;
        f.Frame(new("cogni", .99, 3)); f.Frame(new("cogni", .99, 3));
        Assert.Equal(0, Volatile.Read(ref changes)); Assert.Equal(bytes, File.ReadAllBytes(f.Path));
        Assert.Equal(preferences, File.ReadAllBytes(f.FavoritesPath));
        Assert.True(f.State.Snapshot.Boards.Single(row => row.Genus == "special").Slots[0].IsFavorite);
    }

    [Theory]
    [InlineData("profile")] [InlineData("full-list")] [InlineData("roll")]
    public void AReadBeforeTheStateFrameCallbackCannotConsumeThePresetChoiceWithoutPersistingIt(string barrier)
    {
        using var f = new Fixture(); f.Login(101, 7, 1);
        using var lease = f.State.ObserveMenu(); f.SelectGamePreset(3);
        if (barrier == "profile") f.Login(102, 8, 2);
        else
        {
            f.Publish(new(FullPets(2), NetworkCombatSourceStatus.Complete, true, DateTimeOffset.UtcNow, 2, SourceName));
            if (barrier == "roll") f.Publish(new(FullPets(null), NetworkCombatSourceStatus.Complete, true,
                DateTimeOffset.UtcNow, 3, "own-pet-genus-roll-update"));
        }
        // Drain the actual character projection callback, so it cannot accidentally save
        // the choice while this test is exercising only the FrameObserved ordering.
        f.WaitForProjection();
        Assert.Equal((byte)2, f.Exploration.TryGetSavedGenusInsight()!.ActivePreset);
        var reads = 0; var menuChanges = 0;
        f.BeforeFrameObserved = () => { Interlocked.Increment(ref reads); _ = f.State.Snapshot; };
        f.Menu.Changed += () => Interlocked.Increment(ref menuChanges);
        // Same visible category/preset: only FrameObserved fires, after our earlier read callback.
        f.Frame(new("cogni", .99, 3));
        Assert.Equal(0, Volatile.Read(ref menuChanges)); Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Equal((byte)3, f.Exploration.TryGetSavedGenusInsight()!.ActivePreset);
        Assert.Equal("game-menu-preset", f.Exploration.TryGetSavedGenusInsight()!.ActivePresetSource);
    }

    [Theory]
    [InlineData("legacy")] [InlineData("invalid-global")] [InlineData("missing-file")]
    public void MissingOrCorruptSavedGenusDataStaysUnknownAndReadingNeverReplacesTheExistingFile(string variant)
    {
        var json = variant == "invalid-global"
            ? "{\"SchemaVersion\":2,\"Active\":\"11111111111111111111111111111111\",\"Characters\":[{\"Id\":\"11111111111111111111111111111111\",\"Name\":\"Synthetic character\"}],\"GenusInsight\":{\"Boards\":{},\"ObservedAt\":\"2026-11-08T12:00:00+00:00\",\"Source\":\"own-pet-genus-list\"}}"
            : Fixture.Legacy;
        using var f = new Fixture(json, variant != "missing-file");
        var bytes = File.Exists(f.Path) ? File.ReadAllBytes(f.Path) : null;
        var snapshot = f.State.Snapshot;
        Assert.False(snapshot.IsCurrent); Assert.False(snapshot.IsHistorical); Assert.Null(snapshot.Preset);
        Assert.Empty(snapshot.AppliedEffects); Assert.All(snapshot.Boards.SelectMany(board => board.Slots), slot => Assert.Null(slot.Value));
        if (bytes is null) Assert.False(File.Exists(f.Path)); else Assert.Equal(bytes, File.ReadAllBytes(f.Path));
    }

    private static decimal? FirstAttack(GenusInsightSnapshot snapshot) => snapshot.Boards.Single(board => board.Genus == "special").Slots[0].Value;
    private static decimal?[] Values(GenusInsightSnapshot snapshot) => snapshot.Boards.OrderBy(board => board.Genus, StringComparer.Ordinal)
        .SelectMany(board => board.Slots.OrderBy(slot => slot.Slot)).Select(slot => slot.Value).ToArray();
    private static IEnumerable<(byte Board, byte Preset, byte Slot, ushort Stat, long Value)> RawValues(ImmutableDictionary<byte, CharacterGenusBoard> boards) =>
        boards.OrderBy(row => row.Key).SelectMany(board => board.Value.Panels.OrderBy(row => row.Key)
            .SelectMany(panel => panel.Value.OrderBy(row => row.Slot).Select(slot => (board.Key, panel.Key, slot.Slot, slot.StatType, slot.Value))));
    private static PetProgressSnapshot EmptyPets() => new(ImmutableDictionary<int, int>.Empty, ImmutableDictionary<int, int>.Empty);
    private static PetProgressSnapshot FullPets(byte? selected)
    {
        var catalog = GenusInsightCatalog.Default; var boards = ImmutableDictionary.CreateBuilder<byte, CharacterGenusBoard>();
        foreach (var (genus, type) in new (string Genus, byte Type)[] { ("cogni", 2), ("fera", 3), ("natura", 4), ("varian", 5), ("special", 6) })
        {
            var panels = ImmutableDictionary.CreateBuilder<byte, ImmutableArray<CharacterGenusEffect>>();
            for (byte preset = 1; preset <= 3; preset++)
            {
                var slots = ImmutableArray.CreateBuilder<CharacterGenusEffect>(9);
                for (byte number = 1; number <= 9; number++)
                {
                    var (stat, wire) = number switch { 1 or 4 or 5 => ("FixingDamage", (ushort)19), 2 or 7 or 8 => ("Defense", (ushort)52), _ => ("Critical", (ushort)128) };
                    var option = catalog.Options(genus, number).Single(row => row.Rank == 5 && row.StatId == stat);
                    var value = preset switch { 1 => option.Minimum, 2 => decimal.Floor((option.Minimum + option.Maximum) / 2), _ => option.Maximum };
                    slots.Add(new((byte)(number - 1), 5, wire, checked((long)(value * catalog.FindStat(stat)!.RawDivisor)), false, true));
                }
                panels.Add(preset, slots.MoveToImmutable());
            }
            boards.Add(type, new(type, 10, 0, panels.ToImmutable()));
        }
        return EmptyPets() with { SelectedGenusPanel = selected, GenusBoards = boards.ToImmutable() };
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Legacy = "{\"SchemaVersion\":2,\"Active\":\"11111111111111111111111111111111\",\"Characters\":[{\"Id\":\"11111111111111111111111111111111\",\"Name\":\"Character 1\"}]}";
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-genus-state-" + Guid.NewGuid().ToString("N"));
        private readonly Rectangle _bounds = new(0, 0, 1280, 720);
        private long _clock = Stopwatch.GetTimestamp(), _frame;
        private TaskCompletionSource<bool> _projection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Path => System.IO.Path.Combine(_directory, "exploration.json");
        internal string FavoritesPath => System.IO.Path.Combine(_directory, "favorites.json");
        internal Source Network { get; private set; } = null!;
        internal ExplorationService Exploration { get; private set; } = null!;
        internal OwnSkillStateService Skills { get; private set; } = null!;
        internal CharacterCombatStateService Character { get; private set; } = null!;
        internal GenusMenuObservationService Menu { get; private set; } = null!;
        internal GenusInsightFavoriteService Favorites { get; private set; } = null!;
        internal GenusInsightStateService State { get; private set; } = null!;
        internal Action? BeforeFrameObserved { get; set; }
        internal Fixture(string json = Legacy, bool writeProfileFile = true)
        {
            Directory.CreateDirectory(_directory); if (writeProfileFile) File.WriteAllText(Path, json); StartServices();
        }
        private void StartServices()
        {
            Network = new(); Exploration = new(Path, []); Skills = new(Network, Exploration); Character = new(Network, Skills, Exploration);
            Favorites = new(FavoritesPath); Menu = new(() => throw new InvalidOperationException("No capture in tests."), () => _bounds, () => _clock);
            Menu.FrameObserved += () => BeforeFrameObserved?.Invoke();
            State = new(Character, Favorites, Menu);
            Character.Changed += ProjectionObserved;
        }
        private void ProjectionObserved()
        {
            var current = Character.Snapshot;
            if (current.Graph is not null && current.Sequence == Network.CombatSnapshot.Sequence) _projection.TrySetResult(true);
        }
        internal void WaitForProjection() => Assert.True(_projection.Task.Wait(TimeSpan.FromSeconds(5)));
        internal string Login(long character, long generation, byte? preset, bool includeBoards = true)
        {
            var observed = At.AddSeconds(character - 101);
            var identity = new OwnCharacterIdentity(character, 501, 300, "Synthetic character " + character) { ClassId = "Cleric" };
            Exploration.BindNetworkContext(new(identity, generation, 1, observed)); Network.Running = true;
            Network.CombatSnapshot = new() { Character = new(identity, generation) { ObservedAt = observed }, Generation = generation, Sequence = 1,
                ClassId = "Cleric", Pets = includeBoards ? new(FullPets(preset), NetworkCombatSourceStatus.Complete, true, observed, 1, SourceName) : NetworkCombatDomain<PetProgressSnapshot>.Unknown };
            _projection = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Network.Raise(); return Character.Snapshot.ProfileId!;
        }
        internal void Publish(NetworkCombatDomain<PetProgressSnapshot> domain)
        {
            Network.CombatSnapshot = Network.CombatSnapshot with { Sequence = Network.CombatSnapshot.Sequence + 1, Pets = domain };
            _projection = new(TaskCreationOptions.RunContinuationsAsynchronously); Network.Raise();
        }
        internal void SelectGamePreset(byte preset)
        {
            _ = State.Snapshot;
            for (var attempt = 0; attempt < 4; attempt++)
            { Frame(new("cogni", .99, preset)); if (State.Snapshot.ViewedBoard is not null && State.Snapshot.ViewedPreset == preset) return; }
            Assert.Fail("The fresh synthetic menu frames did not establish their selected preset.");
        }
        internal void Frame(GenusMenuDetection? detection)
        {
            _clock = Math.Max(_clock + 1, Stopwatch.GetTimestamp());
            Assert.True(Menu.Observe(detection, _bounds, _clock, 1, ++_frame, Menu.Revision));
        }
        internal void StopNetwork() { Network.Running = false; Network.Raise(); }
        internal void Restart() { StopServices(); StartServices(); }
        private void StopServices() { Character.Changed -= ProjectionObserved; State.Dispose(); Character.Dispose(); Skills.Dispose(); Menu.Dispose(); }
        public void Dispose() { StopServices(); Directory.Delete(_directory, true); }
    }

    private sealed class Source : INetworkCharacterCombatSource, INetworkSkillSource
    {
        public bool Running { get; set; }
        public NetworkCharacterCombatSnapshot CombatSnapshot { get; set; } = NetworkCharacterCombatSnapshot.Empty;
        public NetworkSkillSnapshot SkillSnapshot => NetworkSkillSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        internal void Raise() => Changed?.Invoke();
    }
}
