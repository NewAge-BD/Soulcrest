using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkCharacterCombatSourceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-combat-source-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])),
                new ExplorationService(Path.Combine(_root, "exploration.json"), []));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        }
        internal void Send(byte[] message, int second, string stream = "synthetic-own") =>
            Network.ProcessProgressMessage(stream, message, At.AddSeconds(second));
        internal void Login()
        {
            Send(Candidate(), 0); Send(Prepare(), 1); Send(Stats(), 2); Send(Titles(), 2); Send(Boards(), 2); Send(Own(), 3);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, true); }
    }

    [Fact]
    public void PreparedSourcesAreInvisibleUntilMatchingFullOwnAndRemainIndependentDomains()
    {
        using var f = new Fixture();
        f.Send(Candidate(), 0); f.Send(Prepare(), 1); f.Send(Stats(), 2); f.Send(Titles(), 2); f.Send(Boards(), 2);
        Assert.Null(f.Network.CombatSnapshot.Character);
        f.Send(Own(), 3);
        var state = f.Network.CombatSnapshot;
        Assert.NotNull(state.Character); Assert.Equal("Cleric", state.ClassId); Assert.Equal(45, state.CharacterLevel);
        Assert.Equal(1, state.CombatLevel); Assert.Equal(0, state.ConquerorLevel);
        Assert.Equal(12345, state.CurrentCombatScore); Assert.Equal(NetworkCombatSourceStatus.Complete, state.Stats.Status);
        Assert.Equal(9999, state.Stats.Value![193]); Assert.Equal(200, state.Stats.Value[19]);
        Assert.Equal(NetworkCombatSourceStatus.Partial, state.DaevanionBoards.Status);
        Assert.Single(state.DaevanionBoards.Value!.Boards); Assert.False(state.DaevanionBoards.Value.Boards.ContainsKey(75));
        Assert.Equal(5, state.DaevanionPoints.Value!.Used[1]); Assert.Equal(4, state.DaevanionPoints.Value.Bonus[1]);
        Assert.Null(state.Equipment.Value); Assert.DoesNotContain("SyntheticAlpha", state.ToString());
    }

    [Fact]
    public void ObservedLoginOrderRetainsBoundedPrePreparationSourcesUntilFullOwn()
    {
        using var f = new Fixture(); f.Send(Candidate(), 0); f.Send(Titles(), 1); f.Send(Boards(), 1);
        Assert.Null(f.Network.CombatSnapshot.Titles.Value);
        f.Send(Prepare(), 2); f.Send(Own(), 3);
        Assert.True(f.Network.CombatSnapshot.Titles.IsFresh);
        Assert.True(f.Network.CombatSnapshot.DaevanionBoards.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Partial, f.Network.CombatSnapshot.Stats.Status);
        f.Send(Stats(), 4);
        Assert.Equal(NetworkCombatSourceStatus.Complete, f.Network.CombatSnapshot.Stats.Status);
    }

    [Fact]
    public void AbsoluteAchievementUpdatesRefineOnlyAnObservedProvisionalList()
    {
        using var f = new Fixture(); f.Send(Candidate(), 0); f.Send(Achievements(false, 99), 1);
        f.Send(Achievements(true, 2), 1); f.Send(Achievements(false, 4), 1);
        f.Send(Prepare(), 2); f.Send(Achievements(false, 5), 2); f.Send(Own(), 3);
        var source = f.Network.CombatSnapshot.Achievements;
        Assert.Equal(NetworkCombatSourceStatus.Complete, source.Status); Assert.True(source.IsFresh);
        Assert.Equal(5, source.Value!.Achievements[123].Count);
    }

    [Theory]
    [InlineData("character")] [InlineData("entity")] [InlineData("stream")] [InlineData("unprepared")] [InlineData("expired")]
    public void UnmatchedLoginDoesNotPromoteProvisionalFullStats(string variant)
    {
        using var f = new Fixture(); f.Send(Candidate(), 0);
        if (variant != "unprepared") f.Send(Prepare(), 1);
        f.Send(Stats(), 2);
        f.Send(Own(variant == "character" ? 102 : 101, variant == "entity" ? 301u : 300u),
            variant == "expired" ? 124 : 3, variant == "stream" ? "synthetic-other" : "synthetic-own");
        Assert.NotEqual(NetworkCombatSourceStatus.Complete, f.Network.CombatSnapshot.Stats.Status);
        Assert.False(f.Network.CombatSnapshot.Stats.Value?.ContainsKey(19) == true);
    }

    [Fact]
    public void EntityFilteredUpdatesPreserveImmutableSnapshotAndCannotHealRejectedFullSource()
    {
        using var f = new Fixture(); f.Login(); var before = f.Network.CombatSnapshot;
        f.Send(Update(300, 250), 4);
        Assert.Equal(250, f.Network.CombatSnapshot.Stats.Value![19]); Assert.Equal(200, before.Stats.Value![19]);
        Assert.Equal(NetworkCombatSourceStatus.Stale, f.Network.CombatSnapshot.Abnormals.Status);
        var current = f.Network.CombatSnapshot;
        f.Send(Update(301, 400), 5); f.Send(Update(300, 500), 6, "synthetic-other"); f.Send(Update(300, 600), 2);
        Assert.Equal(250, f.Network.CombatSnapshot.Stats.Value![19]);
        Assert.Same(current, f.Network.CombatSnapshot);
        f.Send([0x4a, 0x36], 7);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.Stats.Status);
        Assert.True(f.Network.CombatSnapshot.Titles.IsFresh);
        f.Send(Update(300, 700), 8); Assert.False(f.Network.CombatSnapshot.Stats.IsFresh);
        f.Send(Stats(800), 9); Assert.True(f.Network.CombatSnapshot.Stats.IsFresh);
        Assert.Equal(800, f.Network.CombatSnapshot.Stats.Value![19]);
    }

    [Fact]
    public void DuplicatePreparedListConflictsButOwnSubsetNeverClaimsToBeFullStats()
    {
        using var f = new Fixture(); f.Send(Candidate(), 0); f.Send(Prepare(), 1); f.Send(Stats(), 2); f.Send(Stats(), 2); f.Send(Own(), 3);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.Stats.Status);
        f.Send(Own(), 4);
        Assert.Equal(NetworkCombatSourceStatus.Partial, f.Network.CombatSnapshot.Stats.Status);
        Assert.Single(f.Network.CombatSnapshot.Stats.Value!);
    }

    [Fact]
    public void GapAndNewCharacterRevokeFreshnessWithoutMakingMissingWalletsZero()
    {
        using var f = new Fixture(); f.Login();
        f.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(4));
        Assert.Null(f.Network.CombatSnapshot.Character); Assert.False(f.Network.CombatSnapshot.Titles.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Stale, f.Network.CombatSnapshot.Titles.Status);
        f.Send(Own(102), 5);
        Assert.Null(f.Network.CombatSnapshot.Titles.Value);
        Assert.NotNull(f.Network.CombatSnapshot.DaevanionPoints.Value); // only explicitly observed used/bonus, no free wallet API
    }

    [Fact]
    public void IdentityOnlyHookCannotQualifyCombatSourcesAndReentrantOwnUpdateWins()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("synthetic-own", new(101, 501, 300, "SyntheticAlpha"), At);
        f.Send(Stats(), 1); Assert.Null(f.Network.CombatSnapshot.Character);
        var updated = false;
        f.Network.OwnCharacterChanged += () =>
        {
            if (updated || f.Network.CombatSnapshot.Character is null) return;
            updated = true; f.Send(Stats(777), 5);
        };
        f.Send(Own(), 3);
        Assert.Equal(777, f.Network.CombatSnapshot.Stats.Value![19]);
    }

    [Fact]
    public void SpentPointUpdatesUseOnlyCurrentOwnStreamAndPreserveItsImmutableBonusMap()
    {
        using var f = new Fixture(); f.Login(); var before = f.Network.CombatSnapshot;
        f.Send(UsedPoints(4), 4);
        var refunded = f.Network.CombatSnapshot;
        Assert.Equal((ushort)4, refunded.DaevanionPoints.Value!.Used[1]);
        Assert.Equal((ushort)5, before.DaevanionPoints.Value!.Used[1]);
        Assert.Same(before.DaevanionPoints.Value.Bonus, refunded.DaevanionPoints.Value.Bonus);
        Assert.Equal(before.Generation, refunded.Generation); Assert.Same(before.Character, refunded.Character);
        Assert.Contains("used", refunded.DaevanionPoints.Source, StringComparison.Ordinal);
        f.Send(UsedPoints(5), 5);
        var rebought = f.Network.CombatSnapshot;
        Assert.Equal((ushort)5, rebought.DaevanionPoints.Value!.Used[1]);
        f.Send(UsedPoints(1), 6, "synthetic-other"); f.Send(UsedPoints(1), 2);
        Assert.Same(rebought, f.Network.CombatSnapshot);
        f.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(7));
        var stale = f.Network.CombatSnapshot;
        f.Send(UsedPoints(1), 8);
        Assert.Same(stale, f.Network.CombatSnapshot); Assert.Null(stale.Character);
        Assert.False(stale.DaevanionPoints.IsFresh); Assert.Equal((ushort)5, stale.DaevanionPoints.Value!.Used[1]);
    }

    [Fact]
    public void SpentPointUpdatesCannotCreateAuthorityFromIdentityOnlyOrPreparedLoginPrefixes()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("synthetic-own", new(101, 501, 300, "SyntheticAlpha"), At);
        f.Send(UsedPoints(4), 1); Assert.Null(f.Network.CombatSnapshot.Character);
        f.Send(Candidate(), 2); f.Send(Prepare(), 3); f.Send(UsedPoints(4), 4); f.Send(Own(), 5);
        Assert.Equal((ushort)5, f.Network.CombatSnapshot.DaevanionPoints.Value!.Used[1]);
        f.Send(UsedPoints(4), 6); Assert.Equal((ushort)4, f.Network.CombatSnapshot.DaevanionPoints.Value!.Used[1]);
    }

    [Fact]
    public void MalformedSpentPointUpdateInvalidatesOnlyPointsAndRequiresANewOwnBonusBaselineToHeal()
    {
        using var f = new Fixture(); f.Login(); f.Send([0x2e, 0xe2], 4);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.DaevanionPoints.Status);
        Assert.True(f.Network.CombatSnapshot.Stats.IsFresh);
        f.Send(UsedPoints(4), 5); Assert.False(f.Network.CombatSnapshot.DaevanionPoints.IsFresh);
        f.Send(Own(), 6); f.Send(UsedPoints(4), 7);
        Assert.True(f.Network.CombatSnapshot.DaevanionPoints.IsFresh);
        Assert.Equal((ushort)4, f.Network.CombatSnapshot.DaevanionPoints.Value!.Used[1]);
        Assert.Equal((ushort)4, f.Network.CombatSnapshot.DaevanionPoints.Value.Bonus[1]);
    }

    [Fact]
    public void ResourceMaximumsUseFullOwnAndNeverTheRawHpStatOrItsTrailer()
    {
        using var f = new Fixture();
        f.Send(ResourceMaximums(300, 12345, 5432), 0);
        Assert.Null(f.Network.CombatSnapshot.HpMaximum.Value);
        f.Send(Candidate(), 1); f.Send(Prepare(), 2);
        f.Send(ResourceMaximums(300, 12345, 5432), 2);
        f.Send(Stats(), 2); f.Send(Own(maximumHp: 25000, maximumMp: 5000), 3);
        var state = f.Network.CombatSnapshot;
        Assert.Equal(25000, state.HpMaximum.Value!.Value); Assert.Equal(5000, state.MpMaximum.Value!.Value);
        Assert.Equal(9999, state.Stats.Value![193]);
        f.Send(Stats(), 4);
        Assert.Same(state.HpMaximum, f.Network.CombatSnapshot.HpMaximum);
        Assert.Equal(At.AddSeconds(3), state.HpMaximum.ObservedAt);
    }

    [Fact]
    public void OwnResourceUpdatesKeepSeparateFactTimesAndIgnoreOtherEntitiesStreamsAndOlderFacts()
    {
        using var f = new Fixture(); f.Send(Own(maximumHp: 25000, maximumMp: 5000), 3);
        var before = f.Network.CombatSnapshot;
        f.Send(ResourceMaximums(300, hp: 24850), 4);
        var changed = f.Network.CombatSnapshot;
        Assert.Equal(24850, changed.HpMaximum.Value!.Value);
        Assert.Equal(25000, before.HpMaximum.Value!.Value);
        Assert.Same(before.MpMaximum, changed.MpMaximum);
        Assert.Equal(At.AddSeconds(4), changed.HpMaximum.ObservedAt);
        Assert.Equal(At.AddSeconds(3), changed.MpMaximum.ObservedAt);
        f.Send(ResourceMaximums(301, 1, 2), 5);
        f.Send(ResourceMaximums(300, 1, 2), 5, "synthetic-other");
        f.Send(ResourceMaximums(300, 1, 2), 2);
        Assert.Same(changed, f.Network.CombatSnapshot);
        f.Send(ResourceMaximums(300, hp: 24000), 3);
        Assert.Same(changed, f.Network.CombatSnapshot);
        f.Send(ResourceMaximums(300, mp: 0), 6);
        Assert.Equal(0, f.Network.CombatSnapshot.MpMaximum.Value!.Value);
        Assert.Equal(At.AddSeconds(4), f.Network.CombatSnapshot.HpMaximum.ObservedAt);
    }

    [Fact]
    public void MalformedForeignAndOlderResourceMessagesCannotInvalidateNewerOwnMaximumFacts()
    {
        using var f = new Fixture(); f.Send(Own(maximumHp: 25000, maximumMp: 5000), 3);
        f.Send(ResourceMaximums(300, 24850, 4900), 5);
        var current = f.Network.CombatSnapshot;
        f.Send(ResourceMaximums(301, hp: -1), 6);
        f.Send([.. ResourceMaximums(301, hp: 1), 0], 6);
        f.Send(ResourceMaximums(300, hp: -1), 4);
        f.Send([.. ResourceMaximums(300, hp: 1), 0], 4);
        f.Send([0x00, 0x8d], 4);
        Assert.Same(current, f.Network.CombatSnapshot);
        Assert.True(current.HpMaximum.IsFresh); Assert.True(current.MpMaximum.IsFresh);
        f.Send(ResourceMaximums(300, hp: 24700), 7);
        var hpOnly = f.Network.CombatSnapshot;
        f.Send(ResourceMaximums(300, hp: -1), 6);
        Assert.Same(hpOnly.HpMaximum, f.Network.CombatSnapshot.HpMaximum);
        Assert.Equal(At.AddSeconds(7), f.Network.CombatSnapshot.ObservedAt);
        Assert.True(f.Network.CombatSnapshot.HpMaximum.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.MpMaximum.Status);
    }

    [Fact]
    public void ResourceFormatErrorsAndGapsInvalidateAuthorityWithoutInventingMissingMaximums()
    {
        using var f = new Fixture(); f.Login();
        f.Send(ResourceMaximums(300, 25000, 5000), 4);
        f.Send([0x00, 0x8d], 5);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.HpMaximum.Status);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.MpMaximum.Status);
        Assert.True(f.Network.CombatSnapshot.Stats.IsFresh);
        f.Send(ResourceMaximums(300, hp: 24500), 6);
        Assert.True(f.Network.CombatSnapshot.HpMaximum.IsFresh);
        Assert.False(f.Network.CombatSnapshot.MpMaximum.IsFresh);
        f.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(7));
        var stale = f.Network.CombatSnapshot;
        Assert.Null(stale.Character); Assert.False(stale.HpMaximum.IsFresh);
        f.Send(ResourceMaximums(300, 1, 2), 8); Assert.Same(stale, f.Network.CombatSnapshot);
        f.Send(Own(102, maximumHp: 19000, maximumMp: -1), 9);
        Assert.Equal(19000, f.Network.CombatSnapshot.HpMaximum.Value!.Value);
        Assert.Null(f.Network.CombatSnapshot.MpMaximum.Value);
    }

    [Fact]
    public void TitleOwnershipIsPublishedOnlyAfterOwnAndCannotCrossAConfirmedCharacterChange()
    {
        using var f = new Fixture();
        var owned = Enumerable.Range(700001, 270).ToArray();
        f.Send(Candidate(), 0); f.Send(TitleList(owned, [(1, owned[0]), (2, owned[0]), (3, owned[2])]), 1);
        Assert.Null(f.Network.CombatSnapshot.TitleOwnership.Value);
        f.Send(Prepare(), 2); f.Send(Own(), 3);
        var first = f.Network.CombatSnapshot;
        var inventory = first.TitleOwnership;
        Assert.Equal(NetworkCombatSourceStatus.Complete, inventory.Status); Assert.True(inventory.IsFresh);
        Assert.Equal(270, inventory.Value!.Count); Assert.Equal(At.AddSeconds(1), inventory.ObservedAt);
        Assert.Equal("own-title-ownership-list", inventory.Source);
        Assert.Equal(3, first.Titles.Value!.EquippedByCategory.Count);

        f.Send(Own(maximumHp: 20000), 4);
        var repeated = f.Network.CombatSnapshot;
        Assert.True(repeated.Generation > first.Generation); Assert.Equal(20000, repeated.HpMaximum.Value!.Value);
        Assert.Same(inventory, repeated.TitleOwnership); Assert.False(repeated.Titles.IsFresh);
        f.Send(Own(102, entity: 301, classCode: 21), 5);
        var otherCharacter = f.Network.CombatSnapshot;
        Assert.Equal(NetworkCombatSourceStatus.Unknown, otherCharacter.TitleOwnership.Status);
        Assert.Null(otherCharacter.TitleOwnership.Value); Assert.False(otherCharacter.TitleOwnership.IsFresh);
        Assert.Null(otherCharacter.Titles.Value); Assert.False(otherCharacter.Titles.IsFresh);
        Assert.Null(otherCharacter.TitleOwnership.ObservedAt);
        Assert.Equal(270, first.TitleOwnership.Value!.Count);
        f.Send(Own(), 6);
        Assert.Equal(NetworkCombatSourceStatus.Unknown, f.Network.CombatSnapshot.TitleOwnership.Status);
        f.Send(TitleList([700002]), 7);
        Assert.True(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.Equal([700002], f.Network.CombatSnapshot.TitleOwnership.Value!);
    }

    [Fact]
    public void SameCharacterSoftEntryHoldsOwnershipPrivatelyUntilFullOwnConfirmsItsContinuity()
    {
        using var f = new Fixture(); f.Login();
        var original = f.Network.CombatSnapshot.TitleOwnership;

        f.Send(Candidate(), 4);
        Assert.Null(f.Network.CombatSnapshot.Character);
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Stale, f.Network.CombatSnapshot.TitleOwnership.Status);
        f.Send(Prepare(), 5);
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        f.Send(Own(), 6);

        Assert.Same(original, f.Network.CombatSnapshot.TitleOwnership);
        Assert.True(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.Equal(original.ObservedAt, f.Network.CombatSnapshot.TitleOwnership.ObservedAt);
        Assert.False(f.Network.CombatSnapshot.Titles.IsFresh);
    }

    [Fact]
    public void PreparationOnTheCurrentOwnTransportCanRetainOnlyThatCharactersOwnership()
    {
        using var f = new Fixture(); f.Login();
        var original = f.Network.CombatSnapshot.TitleOwnership;
        f.Send(Prepare(), 4);
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        f.Send(Own(), 5);
        Assert.Same(original, f.Network.CombatSnapshot.TitleOwnership);
        f.Send(Prepare(), 6);
        f.Send(Own(102), 7);
        Assert.Equal(NetworkCombatSourceStatus.Unknown, f.Network.CombatSnapshot.TitleOwnership.Status);
        Assert.Null(f.Network.CombatSnapshot.TitleOwnership.Value);
    }

    [Fact]
    public void AnotherCharactersPreparedTitleListReplacesOwnershipOnlyAfterItsMatchingOwn()
    {
        using var f = new Fixture(); f.Login();
        var original = f.Network.CombatSnapshot.TitleOwnership;
        f.Send(Candidate(character: 102, entity: 301), 4);
        f.Send(TitleList([700002, 700003]), 5);
        f.Send(Prepare(), 6);
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.DoesNotContain(700002, f.Network.CombatSnapshot.TitleOwnership.Value!);
        f.Send(Own(102, 301), 7);

        Assert.True(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.Equal(At.AddSeconds(5), f.Network.CombatSnapshot.TitleOwnership.ObservedAt);
        Assert.True(f.Network.CombatSnapshot.TitleOwnership.Value!.SetEquals([700002, 700003]));
        Assert.True(original.IsFresh);
        Assert.DoesNotContain(700002, original.Value!);
    }

    [Theory]
    [InlineData("other-candidate")]
    [InlineData("unknown-candidate")]
    [InlineData("server")]
    [InlineData("stream")]
    [InlineData("expired")]
    public void UnprovenEntryContinuityCannotRestoreAnOlderCharactersTitleOwnership(string boundary)
    {
        using var f = new Fixture(); f.Login();
        switch (boundary)
        {
            case "other-candidate": f.Send(Candidate(character: 102), 4); break;
            case "unknown-candidate": f.Send([0x20, 0x36], 4); break;
            case "expired": f.Send(Candidate(), 4); f.Send(Prepare(), 5); break;
        }
        f.Send(Own(server: boundary == "server" ? (ushort)502 : (ushort)501), boundary == "expired" ? 125 : 6,
            boundary == "stream" ? "synthetic-other" : "synthetic-own");

        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        f.Send(Own(), 126);
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        f.Send(TitleList([700002]), 127);
        Assert.True(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.Equal([700002], f.Network.CombatSnapshot.TitleOwnership.Value!);
    }

    [Fact]
    public void AReentrantConfirmedCharacterChangeWinsOverTheOlderOwnershipContinuityWitness()
    {
        using var f = new Fixture(); f.Login();
        var reentered = false;
        f.Network.WorldContext.Changed += () =>
        {
            if (reentered || f.Network.WorldContext.Snapshot.Status != WorldContextStatus.Transitioning) return;
            reentered = true;
            f.Send(Own(102), 6);
        };
        f.Send(Own(), 5);

        Assert.True(reentered);
        Assert.Equal(102, f.Network.CombatSnapshot.Character!.Identity.CharacterId);
        Assert.Equal(NetworkCombatSourceStatus.Unknown, f.Network.CombatSnapshot.TitleOwnership.Status);
        Assert.Null(f.Network.CombatSnapshot.TitleOwnership.Value);
        f.Send(Own(), 7);
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
    }

    [Fact]
    public void NewFullTitleListsReplaceOwnershipIncludingAnExplicitEmptyListAndDoNotCountEquippedSlotsAsOwned()
    {
        using var f = new Fixture(); f.Send(Own(), 0);
        f.Send(TitleList([700001, 700002], [(1, 700001), (2, 700001), (3, 700003)]), 1);
        var first = f.Network.CombatSnapshot;
        Assert.Equal(2, first.TitleOwnership.Value!.Count);
        Assert.DoesNotContain(700003, first.TitleOwnership.Value);
        f.Send(TitleList([], [(1, 0), (2, 0), (3, 0)]), 2);
        Assert.True(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Complete, f.Network.CombatSnapshot.TitleOwnership.Status);
        Assert.Empty(f.Network.CombatSnapshot.TitleOwnership.Value!);
        Assert.Equal(At.AddSeconds(2), f.Network.CombatSnapshot.TitleOwnership.ObservedAt);
        Assert.Equal(2, first.TitleOwnership.Value.Count);
    }

    [Theory]
    [InlineData("own-gap")]
    [InlineData("global-gap")]
    [InlineData("manual")]
    [InlineData("capture-stopped")]
    [InlineData("capture-closed")]
    [InlineData("capture-generation")]
    [InlineData("older-own")]
    public void HardBoundariesRevokeCharacterTitleOwnershipAndAnOwnAloneCannotHealIt(string boundary)
    {
        using var f = new Fixture(); f.Login(); var original = f.Network.CombatSnapshot.TitleOwnership;
        Assert.True(original.IsFresh);
        switch (boundary)
        {
            case "own-gap": f.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(4)); break;
            case "global-gap": f.Network.ProcessProgressGap("*", capturedAtUtc: At.AddSeconds(4)); break;
            case "capture-generation":
                typeof(NetworkLootService).GetField("_generation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .SetValue(f.Network, 1L);
                f.Send(Own(), 4); break;
            case "older-own": f.Send(Own(), 1); break;
            default: f.Network.ResetProgressContext(boundary, At.AddSeconds(4)); break;
        }
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        f.Send(Own(), 5);
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        f.Send(TitleList([700002]), 6);
        Assert.True(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.Equal([700002], f.Network.CombatSnapshot.TitleOwnership.Value!);
        Assert.True(original.IsFresh); // Previously published facts remain immutable.
    }

    [Fact]
    public void ForeignTitleMessagesAndForeignGapsCannotAlterQualifiedCharacterOwnership()
    {
        using var f = new Fixture(); f.Login(); var current = f.Network.CombatSnapshot;
        f.Send(TitleList([]), 4, "synthetic-other"); f.Send([0x79, 0x56], 5, "synthetic-other");
        f.Network.ProcessProgressGap("synthetic-other", capturedAtUtc: At.AddSeconds(6));
        Assert.Same(current, f.Network.CombatSnapshot);
        Assert.True(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        f.Send(TitleList([]), 1);
        Assert.Same(current, f.Network.CombatSnapshot);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("entity")]
    [InlineData("stream")]
    [InlineData("unprepared")]
    [InlineData("expired")]
    public void UnmatchedOrExpiredProvisionalTitlesDoNotGrantCharacterOwnership(string variant)
    {
        using var f = new Fixture(); f.Send(Candidate(), 0); f.Send(TitleList([700001]), 1);
        if (variant != "unprepared") f.Send(Prepare(), 2);
        f.Send(Own(variant == "character" ? 102 : 101, variant == "entity" ? 301u : 300u),
            variant == "expired" ? 125 : 3, variant == "stream" ? "synthetic-other" : "synthetic-own");
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.Null(f.Network.CombatSnapshot.TitleOwnership.Value);
    }

    [Fact]
    public void MalformedOwnAndDuplicateProvisionalTitleListsCannotBeHealedByCharacterEntries()
    {
        using var f = new Fixture(); f.Login(); f.Send([0x79, 0x56], 4);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.CombatSnapshot.TitleOwnership.Status);
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.True(f.Network.CombatSnapshot.Stats.IsFresh);
        f.Send(Own(), 5); Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        f.Send(Candidate(), 6); f.Send(TitleList([700001]), 7); f.Send(TitleList([700002]), 8);
        f.Send(Prepare(), 9); f.Send(Own(), 10);
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        f.Send(TitleList([700003]), 11);
        Assert.True(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.Equal([700003], f.Network.CombatSnapshot.TitleOwnership.Value!);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("stop")]
    public void ReentrantHardBoundaryDuringOwnResetCannotResurrectCharacterOrProvisionalOwnership(string boundary)
    {
        using var f = new Fixture(); f.Login();
        f.Send(Candidate(), 4); f.Send(TitleList([700002]), 5); f.Send(Prepare(), 6);
        var reentered = false;
        f.Network.WorldContext.Changed += () =>
        {
            if (reentered || f.Network.WorldContext.Snapshot.Status != WorldContextStatus.Transitioning) return;
            reentered = true;
            if (boundary == "gap") f.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(7));
            else f.Network.Stop();
        };
        f.Send(Own(), 7);
        Assert.True(reentered); Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.False(f.Network.CombatSnapshot.Titles.IsFresh);
        f.Send(Own(), 8);
        Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.False(f.Network.CombatSnapshot.Titles.IsFresh);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("malformed-title")]
    [InlineData("new-full-title")]
    public void ReentrantOwnSubscribersWinOverOlderCharacterOwnership(string change)
    {
        using var f = new Fixture(); f.Login(); var original = f.Network.CombatSnapshot.TitleOwnership;
        var reentered = false;
        f.Network.OwnCharacterChanged += () =>
        {
            if (reentered || f.Network.CombatSnapshot.Character is null) return;
            reentered = true;
            if (change == "gap") f.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(6));
            else if (change == "malformed-title") f.Send([0x79, 0x56], 6);
            else f.Send(TitleList([700002]), 6);
        };
        f.Send(Own(), 5);
        Assert.True(reentered);
        if (change == "new-full-title")
        {
            Assert.True(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
            Assert.Equal([700002], f.Network.CombatSnapshot.TitleOwnership.Value!);
            Assert.Equal(At.AddSeconds(6), f.Network.CombatSnapshot.TitleOwnership.ObservedAt);
        }
        else Assert.False(f.Network.CombatSnapshot.TitleOwnership.IsFresh);
        Assert.True(original.IsFresh); Assert.DoesNotContain(700002, original.Value!);
    }

    [Theory]
    [InlineData(320101000)]
    [InlineData(320101100)]
    [InlineData(420101000)]
    [InlineData(420101100)]
    [InlineData(320101200)]
    [InlineData(420101200)]
    [InlineData(401100100)]
    [InlineData(401100300)]
    [InlineData(441100900)]
    [InlineData(300100100)]
    public void ScopedIndividualRewardAcknowledgementsChangeOnlyTheExistingOwnRewardStep(int family)
    {
        using var f = new Fixture(); f.Login();
        f.Send(AchievementRows(true, [(family, 5, 1234567890123L, (byte)0), (123, 9, 0L, (byte)2)]), 4);
        var before = f.Network.CombatSnapshot;
        f.Send(AchievementReward(family, 1), 5);
        var after = f.Network.CombatSnapshot;
        Assert.True(after.Achievements.IsFresh); Assert.Equal(NetworkCombatSourceStatus.Complete, after.Achievements.Status);
        Assert.Equal("own-achievement-reward", after.Achievements.Source); Assert.Equal(At.AddSeconds(5), after.Achievements.ObservedAt);
        Assert.Equal(before.Generation, after.Generation); Assert.Same(before.Character, after.Character);
        Assert.Equal(new CharacterAchievement(5, 1234567890123L, 1), after.Achievements.Value!.Achievements[family]);
        Assert.Equal(new CharacterAchievement(5, 1234567890123L, 0), before.Achievements.Value!.Achievements[family]);
        Assert.Same(before.Achievements.Value.RewardGroups, after.Achievements.Value.RewardGroups);
        Assert.Equal(new[] { true, false, true, false, true }, after.Achievements.Value.RewardGroups[77].ToArray());
        Assert.Same(before.Achievements.Value.Achievements[123], after.Achievements.Value.Achievements[123]);
        Assert.True(after.Achievements.Sequence > before.Achievements.Sequence);
    }

    [Fact]
    public void RewardAcknowledgementsCannotCreateOrRefreshAMissingBaselineOrMissingFamily()
    {
        using var f = new Fixture(); f.Login();
        var withoutBaseline = f.Network.CombatSnapshot;
        f.Send(AchievementReward(420101100, 1), 4);
        Assert.Same(withoutBaseline, f.Network.CombatSnapshot); Assert.Null(f.Network.CombatSnapshot.Achievements.Value);
        f.Send(AchievementRows(true, [(123, 7, 0L, (byte)0)]), 5);
        var withoutFamily = f.Network.CombatSnapshot;
        f.Send(AchievementReward(420101100, 1), 6);
        Assert.Same(withoutFamily, f.Network.CombatSnapshot);
        Assert.False(f.Network.CombatSnapshot.Achievements.Value!.Achievements.ContainsKey(420101100));
        f.Send(AchievementReward(123, 1), 7); // Decodable raw variant, outside the explicit individual reward scope.
        Assert.Same(withoutFamily, f.Network.CombatSnapshot);
    }

    [Fact]
    public void SecondTierWarehouseAckPreservesCountersTimesOtherRowsAndGroups()
    {
        using var f = new Fixture(); f.Login();
        f.Send(AchievementRows(true, [(441100900, 20, 1234567890123L, (byte)1), (401100300, 233, 9876543210123L, (byte)1)]), 4);
        var before = f.Network.CombatSnapshot;
        f.Send(AchievementReward(441100900, 2), 5);
        var after = f.Network.CombatSnapshot;
        Assert.True(after.Achievements.IsFresh); Assert.Equal("own-achievement-reward", after.Achievements.Source);
        Assert.Equal(new CharacterAchievement(20, 1234567890123L, 2), after.Achievements.Value!.Achievements[441100900]);
        Assert.Equal(new CharacterAchievement(20, 1234567890123L, 1), before.Achievements.Value!.Achievements[441100900]);
        Assert.Same(before.Achievements.Value.Achievements[401100300], after.Achievements.Value.Achievements[401100300]);
        Assert.Same(before.Achievements.Value.RewardGroups, after.Achievements.Value.RewardGroups);
        Assert.Same(before.Character, after.Character); Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(At.AddSeconds(5), after.Achievements.ObservedAt);
    }

    [Fact]
    public void OutOfRangeAndKnownOutsideScopeAcksDoNotAlterOrRefreshExistingRows()
    {
        using var f = new Fixture(); f.Login();
        f.Send(AchievementRows(true, [(441100900, 20, 111L, (byte)1), (210000000, 9, 222L, (byte)0)]), 4);
        var before = f.Network.CombatSnapshot;
        var maximum = CombatAchievementRewardScopes.Default.Families[441100900];
        f.Send(AchievementReward(441100900, checked((byte)(maximum + 1))), 5);
        f.Send(AchievementReward(441100900, byte.MaxValue), 6);
        f.Send(AchievementReward(210000000, 1), 7);
        Assert.Same(before, f.Network.CombatSnapshot);
        Assert.Equal(At.AddSeconds(4), f.Network.CombatSnapshot.Achievements.ObservedAt);
    }

    [Fact]
    public void BeforeOwnRewardAcknowledgementsAreNeverBufferedOrReplayedOntoTheLoginList()
    {
        using var f = new Fixture();
        f.Send(AchievementReward(420101100, 1), 0);
        f.Send(Candidate(), 1); f.Send(AchievementRows(true, [(420101100, 5, 111L, (byte)0)]), 2);
        f.Send(AchievementReward(420101100, 1), 2); f.Send(Prepare(), 3);
        f.Send(AchievementReward(420101100, 1), 3); f.Send(Own(), 4);
        Assert.True(f.Network.CombatSnapshot.Achievements.IsFresh);
        Assert.Equal(0, f.Network.CombatSnapshot.Achievements.Value!.Achievements[420101100].RewardedStep);
        f.Send(AchievementReward(420101100, 1), 5);
        Assert.Equal(1, f.Network.CombatSnapshot.Achievements.Value!.Achievements[420101100].RewardedStep);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("conflict")]
    [InlineData("gap")]
    [InlineData("character")]
    [InlineData("foreign-stream")]
    public void RewardAcknowledgementsCannotHealOrCrossOwnSourceBoundaries(string boundary)
    {
        using var f = new Fixture(); f.Login();
        f.Send(AchievementRows(true, [(420101100, 5, 111L, (byte)0)]), 4);
        switch (boundary)
        {
            case "stale": f.Send(Own(), 5); break;
            case "conflict": f.Send([0x83, 0x56], 5); break;
            case "gap": f.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(5)); break;
            case "character": f.Send(Own(102, 301), 5); break;
        }
        var before = f.Network.CombatSnapshot;
        f.Send(AchievementReward(420101100, 1), 6, boundary == "foreign-stream" ? "synthetic-other" : "synthetic-own");
        Assert.Same(before, f.Network.CombatSnapshot);
        Assert.False(f.Network.CombatSnapshot.Achievements.Value?.Achievements.GetValueOrDefault(420101100)?.RewardedStep == 1);
    }

    [Fact]
    public void DuplicateRegressiveAndOlderRewardAcksDoNotReplaceTheOriginalFactTime()
    {
        using var f = new Fixture(); f.Login();
        f.Send(AchievementRows(true, [(420101100, 5, 0L, (byte)2)]), 4);
        var before = f.Network.CombatSnapshot;
        f.Send(AchievementReward(420101100, 1), 5); f.Send(AchievementReward(420101100, 2), 6);
        f.Send(AchievementReward(420101100, 3), 3);
        Assert.Same(before, f.Network.CombatSnapshot);
        f.Send(AchievementReward(420101100, 3), 7);
        var updated = f.Network.CombatSnapshot;
        Assert.Equal(new CharacterAchievement(5, 0, 3), updated.Achievements.Value!.Achievements[420101100]);
        f.Send(AchievementReward(420101100, 4), 6); f.Send(AchievementReward(420101100, 3), 8);
        Assert.Same(updated, f.Network.CombatSnapshot); Assert.Equal(At.AddSeconds(7), updated.Achievements.ObservedAt);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("trailer")]
    [InlineData("nonzero-result")]
    [InlineData("zero-step")]
    public void UnsupportedRewardShapesNeitherAlterNorReconfirmAValidBaseline(string variant)
    {
        using var f = new Fixture(); f.Login();
        f.Send(AchievementRows(true, [(420101100, 5, 0L, (byte)0)]), 4);
        var before = f.Network.CombatSnapshot;
        var message = AchievementReward(420101100, 1);
        message = variant switch
        {
            "truncated" => message[..^1], "trailer" => [.. message, 0],
            "nonzero-result" => AchievementReward(420101100, 1, 1), "zero-step" => AchievementReward(420101100, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        f.Send(message, 5); Assert.Same(before, f.Network.CombatSnapshot);
    }

    private static byte[] AchievementRows(bool full, (int Id, int Count, long Time, byte Step)[] rows) => Packet(full ? (ushort)0x5683 : (ushort)0x5684, w =>
    {
        V(w, (uint)rows.Length);
        foreach (var row in rows) { w.Write(row.Id); w.Write(row.Count); w.Write(row.Time); w.Write(row.Step); }
        if (full) { V(w, 1); w.Write(77); V(w, 5); w.Write((byte)0b00010101); }
    });
    private static byte[] AchievementReward(int family, byte step, ushort result = 0) => Packet(0x5686, w =>
    { w.Write(result); w.Write(family); w.Write(step); });

    private static byte[] TitleList(int[] owned, (byte Slot, int Id)[]? equipped = null) => Packet(0x5679, w =>
    {
        V(w, (uint)owned.Length); foreach (var id in owned) w.Write(id);
        equipped ??= []; V(w, (uint)equipped.Length);
        foreach (var slot in equipped) { w.Write(slot.Slot); w.Write(slot.Id); }
    });

    private static byte[] ResourceMaximums(uint entity, long? hp = null, long? mp = null) => Packet(0x8D00, w =>
    {
        V(w, entity); w.Write((byte)2); V(w, (uint)((hp.HasValue ? 1 : 0) + (mp.HasValue ? 1 : 0)));
        if (hp is { } health) { w.Write((byte)7); w.Write(health); }
        if (mp is { } mana) { w.Write((byte)8); w.Write(mana); }
    });

    private static byte[] Stats(int attack = 200) => Packet(0x3649, w =>
    { w.Write((ushort)0); V(w, 2); w.Write((ushort)19); w.Write(attack); w.Write((ushort)193); w.Write(9999); w.Write(9999L); });
    private static byte[] Update(uint entity, int attack) => Packet(0x364A, w =>
    { V(w, entity); V(w, 1); w.Write((ushort)19); w.Write(attack); w.Write(9999L); });
    private static byte[] Titles() => Packet(0x5679, w =>
    { V(w, 1); w.Write(11010001); V(w, 1); w.Write((byte)1); w.Write(11010001); });
    private static byte[] Boards() => Packet(0xE226, w =>
    { V(w, 1); w.Write(71); V(w, 1); w.Write(710001); });
    private static byte[] Achievements(bool full, int count) => Packet(full ? (ushort)0x5683 : (ushort)0x5684, w =>
    { V(w, 1); w.Write(123); w.Write(count); w.Write(0L); w.Write((byte)0); if (full) V(w, 0); });
    private static byte[] UsedPoints(ushort normal) => Packet(0xE22E, w =>
    { V(w, 5); w.Write((byte)1); w.Write(normal); w.Write((byte)2); w.Write((ushort)0); w.Write((byte)3); w.Write((ushort)2);
        w.Write((byte)4); w.Write((ushort)0); w.Write((byte)5); w.Write((ushort)0); });
    private static byte[] Candidate(long character = 101, uint entity = 300) => Packet(0x3620, w =>
    { w.Write((ushort)0); V(w, entity); w.Write(0); w.Write(character); });
    private static byte[] Prepare() => Packet(0x3621, w => w.Write(new byte[46]));
    private static byte[] Own(long character = 101, uint entity = 300, uint maximumHp = 0, int maximumMp = 0, int classCode = 31,
        ushort server = 501) => Packet(0x3633, w =>
    {
        V(w, entity); w.Write(0); w.Write((byte)1);
        var name = Encoding.UTF8.GetBytes("SyntheticAlpha"); V(w, (uint)name.Length); w.Write(name);
        w.Write(server); w.Write(classCode); w.Write(45); w.Write(new byte[20]); w.Write(1); w.Write(0L);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, maximumHp); V(w, maximumHp);
        w.Write(0); w.Write(maximumMp); w.Write(new byte[44]);
        V(w, 1); w.Write((ushort)193); w.Write(5000); V(w, 0); V(w, 0);
        w.Write(new byte[12]); w.Write(character); V(w, 0); w.Write((byte)1);
        w.Write(new byte[16]); w.Write(12345L); w.Write(67890L);
        V(w, 3); w.Write((byte)1); w.Write((ushort)5); w.Write((byte)2); w.Write((ushort)0); w.Write((byte)3); w.Write((ushort)2);
        V(w, 3); w.Write((byte)1); w.Write((ushort)4); w.Write((byte)2); w.Write((ushort)0); w.Write((byte)3); w.Write((ushort)2);
    });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    { using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes); writer.Write(opcode); write(writer); return bytes.ToArray(); }
    private static void V(BinaryWriter w, uint value)
    { while (value >= 128) { w.Write((byte)(value | 128)); value >>= 7; } w.Write((byte)value); }
}
