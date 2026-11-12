using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Soulcrest.Core.GenusInsight;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Account-wide live rolls and an independent own-character stream; all frames and traffic are synthetic.</summary>
[Collection("Settings file")]
public sealed class GenusInsightSharedSourceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public Task ASharedBaselineReceivedBeforeOwnBindingShowsNineNativeRowsAfterFreshMenuConfirmation() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Shared.Publish(FullPets());
        Assert.False(GenusInsightOverlayContent.Available(fixture.State.Snapshot));
        using var panel = new GenusInsightOverlayForm(fixture.State, fixture.Settings);
        panel.Start(); panel.Show();
        Assert.False(panel.Visible);
        var profile = fixture.Login(101, 7);
        Assert.True(fixture.Character.Snapshot.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Unknown, fixture.Character.Snapshot.Pets.Status);
        Assert.Null(fixture.Character.Snapshot.Pets.Value);
        Assert.False(GenusInsightOverlayContent.Available(fixture.State.Snapshot));
        fixture.SelectGamePreset(2);
        var snapshot = fixture.State.Snapshot;
        Assert.True(GenusInsightOverlayContent.Available(snapshot));
        Assert.Equal(profile, snapshot.ProfileId);
        Assert.Equal((byte)2, snapshot.ViewedPreset);
        Assert.Equal(9, snapshot.ViewedBoard!.Slots.Count);
        Assert.All(snapshot.ViewedBoard.Slots, slot => Assert.True(slot.IsCurrent));
        panel.ApplySettings();
        Assert.True(panel.Visible);
        Assert.True(panel.RuntimeAvailable);
        var saved = fixture.Exploration.TryGetSavedGenusInsight(profile)!;
        Assert.Equal(5, saved.Boards.Count);
        Assert.Equal((byte)2, saved.ActivePreset);
        if (Environment.GetEnvironmentVariable("SOULCREST_SHARED_GENUS_EXPORT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            using var image = GenusInsightOverlayRenderer.Render(snapshot, language: "en");
            image.Save(Path.Combine(output, "genus-shared-source-en.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    });

    [Theory]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("partial")]
    public void ASharedSourceGapCannotReuseFreshCharacterPetsOrSavedRollsAsLiveAuthority(string kind)
    {
        using var fixture = new Fixture();
        fixture.Shared.Publish(FullPets());
        var profile = fixture.Login(101, 7);
        using var lease = fixture.State.ObserveMenu();
        fixture.SelectGamePreset(3);
        var before = fixture.Exploration.TryGetSavedGenusInsight(profile)!;
        Assert.True(GenusInsightOverlayContent.Available(fixture.State.Snapshot));
        var domain = kind switch
        {
            "stale" => fixture.Shared.GenusInsightSnapshot.Domain with { IsFresh = false },
            "partial" => fixture.Shared.GenusInsightSnapshot.Domain with { Status = NetworkCombatSourceStatus.Partial },
            _ => NetworkCombatDomain<PetProgressSnapshot>.Unknown
        };
        fixture.Shared.Revoke(domain);
        var snapshot = fixture.State.Snapshot;
        Assert.False(GenusInsightOverlayContent.Available(snapshot));
        Assert.False(snapshot.IsCurrent);
        var retained = fixture.Exploration.TryGetSavedGenusInsight(profile)!;
        Assert.Equal(before.ActivePreset, retained.ActivePreset);
        Assert.Equal(RawValues(before.Boards), RawValues(retained.Boards));
    }

    [Fact]
    public void ANewSharedSourceGenerationRequiresFreshMenuEvidenceAndUsesTheNewRolls()
    {
        using var fixture = new Fixture();
        fixture.Shared.Publish(FullPets());
        fixture.Login(101, 7);
        using var lease = fixture.State.ObserveMenu();
        fixture.SelectGamePreset(1);
        var previous = fixture.State.Snapshot;
        var updated = FullPets();
        var special = updated.GenusBoards[6];
        var first = special.Panels[1];
        var old = first[0];
        special = special with { Panels = special.Panels.SetItem(1, first.SetItem(0, old with { Value = old.Value + 1 })) };
        updated = updated with { GenusBoards = updated.GenusBoards.SetItem(6, special) };
        fixture.Shared.Publish(updated, newGeneration: true);
        Assert.False(GenusInsightOverlayContent.Available(fixture.State.Snapshot));
        fixture.SelectGamePreset(1);
        var current = fixture.State.Snapshot;
        Assert.True(GenusInsightOverlayContent.Available(current));
        Assert.NotEqual(previous.Sequence, current.Sequence);
        Assert.Equal(old.Value + 1m, current.Boards.Single(board => board.Genus == "special").Slots[0].Value);
        Assert.Null(fixture.Character.Snapshot.Pets.Value);
    }

    [Fact]
    public void SharedRollsPersistOnceWhileEachNewOwnProfileMustConfirmItsOwnPreset()
    {
        using var fixture = new Fixture();
        fixture.Shared.Publish(FullPets());
        var first = fixture.Login(101, 7);
        using var lease = fixture.State.ObserveMenu();
        fixture.SelectGamePreset(3);
        Assert.Equal((byte)3, fixture.Exploration.TryGetSavedGenusInsight(first)!.ActivePreset);
        var second = fixture.Login(102, 8);
        Assert.NotEqual(first, second);
        Assert.False(GenusInsightOverlayContent.Available(fixture.State.Snapshot));
        Assert.Null(fixture.Exploration.TryGetSavedGenusInsight(second)!.ActivePreset);
        fixture.SelectGamePreset(2);
        var a = fixture.Exploration.TryGetSavedGenusInsight(first)!;
        var b = fixture.Exploration.TryGetSavedGenusInsight(second)!;
        Assert.Equal((byte)3, a.ActivePreset);
        Assert.Equal((byte)2, b.ActivePreset);
        Assert.Equal(RawValues(a.Boards), RawValues(b.Boards));
        Assert.Equal(second, fixture.State.Snapshot.ProfileId);
        Assert.Equal((byte)2, fixture.State.Snapshot.Preset);
        fixture.StopOwnSource();
        Assert.False(GenusInsightOverlayContent.Available(fixture.State.Snapshot));
        Assert.True(fixture.State.Snapshot.IsHistorical);
        Assert.Equal((byte)3, fixture.Exploration.TryGetSavedGenusInsight(first)!.ActivePreset);
        Assert.Equal((byte)2, fixture.Exploration.TryGetSavedGenusInsight(second)!.ActivePreset);
    }

    [Fact]
    public void ClosingAndReopeningTheMenuDoesNotEraseSharedRollsOrCreateAuthorityWithoutFrames()
    {
        using var fixture = new Fixture();
        fixture.Shared.Publish(FullPets());
        var profile = fixture.Login(101, 7);
        var lease = fixture.State.ObserveMenu();
        fixture.SelectGamePreset(2);
        var saved = fixture.Exploration.TryGetSavedGenusInsight(profile)!;
        fixture.Frame(null);
        Assert.False(GenusInsightOverlayContent.Available(fixture.State.Snapshot));
        Assert.Equal((byte)2, fixture.Exploration.TryGetSavedGenusInsight(profile)!.ActivePreset);
        lease.Dispose();
        using var renewed = fixture.State.ObserveMenu();
        Assert.False(GenusInsightOverlayContent.Available(fixture.State.Snapshot));
        fixture.SelectGamePreset(2);
        Assert.True(GenusInsightOverlayContent.Available(fixture.State.Snapshot));
        Assert.Equal(RawValues(saved.Boards), RawValues(fixture.Exploration.TryGetSavedGenusInsight(profile)!.Boards));
    }

    [Fact]
    public void ASharedWitnessRevokedDuringTheSnapshotReadCannotCompleteAsCurrent()
    {
        using var fixture = new Fixture();
        fixture.Shared.Publish(FullPets());
        fixture.Login(101, 7);
        using var lease = fixture.State.ObserveMenu();
        fixture.SelectGamePreset(1);
        Assert.True(GenusInsightOverlayContent.Available(fixture.State.Snapshot));
        fixture.Shared.BeforeNextRead = () => fixture.Shared.Revoke(NetworkCombatDomain<PetProgressSnapshot>.Unknown);
        var raced = fixture.State.Snapshot;
        Assert.False(GenusInsightOverlayContent.Available(raced));
        Assert.False(raced.IsCurrent);
    }

    [Fact]
    public Task ARenderWhoseSourceLosesAuthorityCannotRestoreTheOldNativeFrame() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Shared.Publish(FullPets());
        fixture.Login(101, 7);
        using var lease = fixture.State.ObserveMenu();
        fixture.SelectGamePreset(1);
        var reads = 0;
        var revoke = false;
        using var panel = new GenusInsightOverlayForm(() =>
        {
            if (revoke && ++reads == 2) fixture.Shared.Revoke(NetworkCombatDomain<PetProgressSnapshot>.Unknown);
            return fixture.State.Snapshot;
        }, fixture.State.ObserveMenu, fixture.Settings);
        panel.Start(); panel.Show();
        Assert.True(panel.Visible);
        revoke = true;
        panel.RenderPanel();
        Assert.False(panel.Visible);
        Assert.False(panel.RuntimeAvailable);
        Assert.True(panel.Locked);
    });

    private static IEnumerable<(byte Board, byte Preset, byte Slot, ushort Stat, long Value)> RawValues(
        ImmutableDictionary<byte, CharacterGenusBoard> boards) => boards.OrderBy(row => row.Key).SelectMany(board =>
        board.Value.Panels.OrderBy(row => row.Key).SelectMany(panel => panel.Value.OrderBy(row => row.Slot)
            .Select(slot => (board.Key, panel.Key, slot.Slot, slot.StatType, slot.Value))));

    private static PetProgressSnapshot FullPets()
    {
        var catalog = GenusInsightCatalog.Default;
        var boards = ImmutableDictionary.CreateBuilder<byte, CharacterGenusBoard>();
        foreach (var (genus, type) in new (string Genus, byte Type)[]
            { ("cogni", 2), ("fera", 3), ("natura", 4), ("varian", 5), ("special", 6) })
        {
            var panels = ImmutableDictionary.CreateBuilder<byte, ImmutableArray<CharacterGenusEffect>>();
            for (byte preset = 1; preset <= 3; preset++)
            {
                var slots = ImmutableArray.CreateBuilder<CharacterGenusEffect>(9);
                for (byte number = 1; number <= 9; number++)
                {
                    var (stat, wire) = number switch
                    {
                        1 or 4 or 5 => ("FixingDamage", (ushort)19),
                        2 or 7 or 8 => ("Defense", (ushort)52),
                        _ => ("Critical", (ushort)128)
                    };
                    var option = catalog.Options(genus, number).Single(row => row.Rank == 5 && row.StatId == stat);
                    var value = preset switch
                    {
                        1 => option.Minimum,
                        2 => decimal.Floor((option.Minimum + option.Maximum) / 2),
                        _ => option.Maximum
                    };
                    slots.Add(new((byte)(number - 1), 5, wire,
                        checked((long)(value * catalog.FindStat(stat)!.RawDivisor)), false, true));
                }
                panels.Add(preset, slots.MoveToImmutable());
            }
            boards.Add(type, new(type, 10, 0, panels.ToImmutable()));
        }
        return new(ImmutableDictionary<int, int>.Empty, ImmutableDictionary<int, int>.Empty)
            { GenusBoards = boards.ToImmutable(), SelectedGenusPanel = null };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-shared-genus-" + Guid.NewGuid().ToString("N"));
        private readonly Rectangle _bounds = new(0, 0, 1280, 720);
        private readonly byte[]? _settingsBackup;
        private readonly string _language = UiText.Language;
        private long _clock = Stopwatch.GetTimestamp(), _frame;
        private TaskCompletionSource<bool> _projection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal OwnSource Network { get; } = new();
        internal SharedSource Shared { get; } = new();
        internal ExplorationService Exploration { get; }
        internal OwnSkillStateService Skills { get; }
        internal CharacterCombatStateService Character { get; }
        internal GenusMenuObservationService Menu { get; }
        internal GenusInsightFavoriteService Favorites { get; }
        internal GenusInsightStateService State { get; }
        internal SettingsService Settings { get; }

        internal Fixture()
        {
            Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOULCREST_DATA")));
            Directory.CreateDirectory(_directory);
            Directory.CreateDirectory(AppPaths.DataDirectory);
            _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
            Settings = new();
            Settings.Current.GenusInsightOverlayEnabled = true;
            Settings.Current.GenusInsightOverlayX = Settings.Current.GenusInsightOverlayY = -30000;
            Settings.Current.GenusInsightOverlayScale = 1;
            var path = Path.Combine(_directory, "exploration.json");
            File.WriteAllText(path, "{\"SchemaVersion\":2,\"Active\":\"11111111111111111111111111111111\",\"Characters\":[{\"Id\":\"11111111111111111111111111111111\",\"Name\":\"Character 1\"}]}");
            Exploration = new(path, []);
            Skills = new(Network, Exploration);
            Character = new(Network, Skills, Exploration);
            Favorites = new(Path.Combine(_directory, "favorites.json"));
            Menu = new(() => throw new InvalidOperationException("No capture in tests."), () => _bounds, () => _clock);
            State = new(Character, Favorites, Menu, Shared);
            Character.Changed += ProjectionObserved;
        }

        private void ProjectionObserved()
        {
            var current = Character.Snapshot;
            if (current.Graph is not null && current.Sequence == Network.CombatSnapshot.Sequence) _projection.TrySetResult(true);
        }

        internal string Login(long character, long generation)
        {
            var observed = At.AddMinutes(1).AddSeconds(character - 101);
            var identity = new OwnCharacterIdentity(character, 501, 300, "Synthetic shared character " + character) { ClassId = "Cleric" };
            Exploration.BindNetworkContext(new(identity, generation, 1, observed));
            Network.Running = true;
            Network.CombatSnapshot = new()
            {
                Character = new(identity, generation) { ObservedAt = observed }, Generation = generation, Sequence = 1,
                ClassId = "Cleric", Pets = NetworkCombatDomain<PetProgressSnapshot>.Unknown
            };
            _projection = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Network.Raise();
            Assert.True(_projection.Task.Wait(TimeSpan.FromSeconds(5)));
            return Character.Snapshot.ProfileId!;
        }

        internal void SelectGamePreset(byte preset)
        {
            _ = State.Snapshot;
            Frame(new("cogni", .99, preset));
            Frame(new("cogni", .99, preset));
            var snapshot = State.Snapshot;
            Assert.Equal(preset, snapshot.ViewedPreset);
            Assert.NotNull(snapshot.ViewedBoard);
            Assert.True(GenusInsightOverlayContent.Available(snapshot),
                "Two independent fresh synthetic frames must establish the shared genus preset.");
        }

        internal void Frame(GenusMenuDetection? detection)
        {
            _clock = Math.Max(_clock + 1, Stopwatch.GetTimestamp());
            Assert.True(Menu.Observe(detection, _bounds, _clock, 1, ++_frame, Menu.Revision));
        }

        internal void StopOwnSource() { Network.Running = false; Network.Raise(); }

        public void Dispose()
        {
            Character.Changed -= ProjectionObserved;
            State.Dispose(); Character.Dispose(); Skills.Dispose(); Menu.Dispose();
            Directory.Delete(_directory, true);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _language;
        }
    }

    private sealed class SharedSource : INetworkGenusInsightSource
    {
        private NetworkGenusInsightSnapshot _snapshot = NetworkGenusInsightSnapshot.Empty;
        internal Action? BeforeNextRead { get; set; }
        public NetworkGenusInsightSnapshot GenusInsightSnapshot
        {
            get
            {
                var current = _snapshot;
                var callback = BeforeNextRead; BeforeNextRead = null;
                callback?.Invoke();
                return current;
            }
        }
        public event Action? GenusInsightChanged;
        internal void Publish(PetProgressSnapshot pets, bool newGeneration = false)
        {
            var generation = _snapshot.Generation == 0 ? 1 : _snapshot.Generation + (newGeneration ? 1 : 0);
            var sequence = _snapshot.Sequence + 1;
            _snapshot = new(generation, sequence, new(pets, NetworkCombatSourceStatus.Complete, true,
                At.AddSeconds(sequence), sequence, "own-pet-genus-list"));
            GenusInsightChanged?.Invoke();
        }
        internal void Revoke(NetworkCombatDomain<PetProgressSnapshot> domain)
        {
            var sequence = _snapshot.Sequence + 1;
            if (domain.Value is not null) domain = domain with { Sequence = sequence };
            _snapshot = new(_snapshot.Generation + 1, sequence, domain);
            GenusInsightChanged?.Invoke();
        }
    }

    private sealed class OwnSource : INetworkCharacterCombatSource, INetworkSkillSource
    {
        public bool Running { get; set; }
        public NetworkCharacterCombatSnapshot CombatSnapshot { get; set; } = NetworkCharacterCombatSnapshot.Empty;
        public NetworkSkillSnapshot SkillSnapshot => NetworkSkillSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        internal void Raise() => Changed?.Invoke();
    }

    private static Task RunSta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception error) { completed.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }
}
