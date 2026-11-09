using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CharacterCombatReadRaceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CombatRefreshWhileTheGetterWaitsForTheProducerReturnsTheNewRevisionWithoutAnEmptyFrame()
    {
        using var fixture = new Fixture(); await fixture.WaitForGraph();
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        fixture.Source.NextCombatRead = () => { entered.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        var read = Task.Run(() => fixture.State.Snapshot);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            fixture.Source.Combat = fixture.Source.Combat with
            {
                Sequence = 2, CurrentCombatScore = 54321,
                Stats = new(ImmutableDictionary<ushort, int>.Empty.Add(1, 2000),
                    NetworkCombatSourceStatus.Complete, true, At.AddSeconds(1), 2, "own-stat-update")
            };
            fixture.Source.Raise();
        }
        finally { resume.Set(); }
        var result = await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsFresh); Assert.Equal(2, result.Sequence);
        Assert.Equal(54321, result.CurrentCombatScore); Assert.Equal(2000, result.NativeStats.Value![1]);
        Assert.Equal(Fixture.ProfileId, result.ProfileId);
    }

    [Fact]
    public async Task SkillRefreshAfterTheOldAdapterCopyDoesNotClearUnrelatedCharacterStats()
    {
        using var fixture = new Fixture(); await fixture.WaitForGraph();
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        fixture.Source.NextSkillRead = () => { entered.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        var read = Task.Run(() => fixture.State.Snapshot);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            fixture.Source.Skill = fixture.Source.Skill with
            {
                Sequence = 2, ObservedAt = At.AddSeconds(1),
                Skills = fixture.Source.Skill.Skills.SetItem(17060000, new(17060000, 15, 11, new(4, 0, 0, 0, 0)))
            };
            fixture.Source.Raise();
        }
        finally { resume.Set(); }
        var result = await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsFresh); Assert.Equal(15, result.Skills[17060000].TotalLevel);
        Assert.Equal(1250, result.NativeStats.Value![1]); Assert.Equal(12345, result.CurrentCombatScore);
        Assert.Equal(Fixture.ProfileId, result.ProfileId);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("gap")]
    [InlineData("profile")]
    [InlineData("generation")]
    [InlineData("identity")]
    public async Task ARealLossOfOwnAuthorityDuringTheReadStillReturnsEmpty(string boundary)
    {
        using var fixture = new Fixture(); await fixture.WaitForGraph();
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        fixture.Source.NextCombatRead = () => { entered.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        var read = Task.Run(() => fixture.State.Snapshot);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            switch (boundary)
            {
                case "stop": fixture.Source.Running = false; break;
                case "gap": fixture.Source.Combat = NetworkCharacterCombatSnapshot.Empty;
                    fixture.Source.Skill = NetworkSkillSnapshot.Empty; break;
                case "profile": fixture.Bound = false; break;
                case "generation": fixture.Source.Combat = fixture.Source.Combat with
                    { Character = fixture.Own with { Generation = 8 }, Generation = 8 }; break;
                case "identity": fixture.Source.Combat = fixture.Source.Combat with
                    { Character = fixture.Own with { Identity = fixture.Own.Identity with { CharacterId = 102 } } }; break;
                default: throw new ArgumentOutOfRangeException(nameof(boundary));
            }
            fixture.Source.Raise();
        }
        finally { resume.Set(); }
        var result = await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(CharacterCombatSnapshot.Empty, result);
        Assert.False(result.IsFresh); Assert.Null(result.ProfileId); Assert.Null(result.NativeStats.Value);
    }

    [Fact]
    public async Task ContinuousAdapterChangesAreBoundedAndNeverReturnAnUnvalidatedOldRevision()
    {
        using var fixture = new Fixture(); await fixture.WaitForGraph();
        var updates = 0;
        Action? update = null;
        update = () =>
        {
            updates++;
            fixture.Source.Combat = fixture.Source.Combat with { Sequence = fixture.Source.Combat.Sequence + 1 };
            fixture.Source.Raise();
            fixture.Source.NextCombatRead = update;
        };
        fixture.Source.NextCombatRead = update;
        CharacterCombatSnapshot result;
        try { result = fixture.State.Snapshot; }
        finally { fixture.Source.NextCombatRead = null; }
        Assert.Same(CharacterCombatSnapshot.Empty, result);
        Assert.InRange(updates, 3, 6);
        Assert.True(fixture.State.Snapshot.IsFresh);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string ProfileId = "11111111111111111111111111111111";
        internal Source Source { get; } = new();
        internal bool Bound { get; set; } = true;
        internal OwnCharacterContext Own { get; } = new(new(101, 501, 300, "Synthetic combat character") { ClassId = "Cleric" }, 7)
            { CombatScore = 12345, ObservedAt = At };
        internal OwnSkillStateService Skills { get; }
        internal CharacterCombatStateService State { get; }
        internal Fixture()
        {
            Source.Combat = new() { Character = Own, ClassId = "Cleric", Generation = 7, Sequence = 1, CurrentCombatScore = 12345,
                Stats = new(ImmutableDictionary<ushort, int>.Empty.Add(1, 1250), NetworkCombatSourceStatus.Complete, true, At, 1, "own-full-stat-list") };
            Source.Skill = new(Own, ImmutableDictionary<int, OwnSkillLevel>.Empty.Add(17060000,
                new(17060000, 14, 10, new(4, 0, 0, 0, 0))), true, true, At, 1, "own-skill-list");
            LootTrackerCharacterSnapshot? Resolve(OwnCharacterContext own) => Bound && own.Generation == Own.Generation
                && own.Identity == Own.Identity ? new(ProfileId, "Synthetic local profile") : null;
            Skills = new(Source, Resolve); State = new(Source, Skills, Resolve);
        }
        internal async Task WaitForGraph()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (State.Snapshot.Graph is null) await Task.Delay(10, timeout.Token);
        }
        public void Dispose() { State.Dispose(); Skills.Dispose(); }
    }

    private sealed class Source : INetworkCharacterCombatSource, INetworkSkillSource
    {
        private Action? _nextCombatRead, _nextSkillRead;
        internal Action? NextCombatRead { set => Interlocked.Exchange(ref _nextCombatRead, value); }
        internal Action? NextSkillRead { set => Interlocked.Exchange(ref _nextSkillRead, value); }
        internal NetworkCharacterCombatSnapshot Combat { get; set; } = NetworkCharacterCombatSnapshot.Empty;
        internal NetworkSkillSnapshot Skill { get; set; } = NetworkSkillSnapshot.Empty;
        public bool Running { get; set; } = true;
        public NetworkCharacterCombatSnapshot CombatSnapshot { get { Interlocked.Exchange(ref _nextCombatRead, null)?.Invoke(); return Combat; } }
        public NetworkSkillSnapshot SkillSnapshot { get { Interlocked.Exchange(ref _nextSkillRead, null)?.Invoke(); return Skill; } }
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        internal void Raise() => Changed?.Invoke();
    }
}
