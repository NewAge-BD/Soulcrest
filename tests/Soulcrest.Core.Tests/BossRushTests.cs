using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class BossRushTests
{
    // 0191 from 2026-10-03: boss IDs/times only, no optional coordinates or personal data.
    private const string Captured = "0191000056040000180099E306003898BB03A101000000A3E30680CA4C04A1010000009BE30617CCB303A1010000009AE306F492BF03A1010000009CE3062429C403A101000000AEE3068C5DBC04A101000000A0E306793BEE03A1010000009DE3068ABAC603A1010000009EE30600BA10E403A101000000ABE3064562BC04A101000000A5E3068A1B7B04A1010000009FE306EE151804A101000000A1E30691700304A101000000A2E30691DCEE03A101000000A4E3061D2E7F04A101000000A6E306E21A4F04A101000000A7E3060085468604A101000000A8E306C707D404A101000000A9E3066B293B04A101000000AAE30671E36B04A101000000ACE306936BC204A101000000ADE306FE8A0104A101000000AFE306DC48C203A101000000B0E306D589B203A1010000000000";
    private const long Now = 1791257500000;

    [Fact]
    public void RecordedListHasAll24AltgardSpawnIdsAndExactTimes()
    {
        var parsed = BossSpawnMessage.TryParse(Convert.FromHexString(Captured));
        Assert.NotNull(parsed);
        Assert.Equal(1110, parsed.MapId);
        Assert.Equal(Enumerable.Range(111001, 24), parsed.Bosses.Select(b => b.SpawnId).Order());
        Assert.All(parsed.Bosses, b => Assert.False(b.Spawned));
        Assert.Equal(1791063988280, parsed.Bosses.Single(b => b.SpawnId == 111001).SpawnUnixMs);
        Assert.Equal(1791063477271, parsed.Bosses.Single(b => b.SpawnId == 111003).SpawnUnixMs);
    }

    [Fact]
    public void TruncatedAndContradictoryListsAreRejectedWithoutThrowing()
    {
        var bytes = Convert.FromHexString(Captured);
        for (int length = 0; length < bytes.Length; length++)
            Assert.Null(BossSpawnMessage.TryParse(bytes.AsSpan(0, length)));
        bytes[13] = 1; // group bit says spawned, record says waiting
        Assert.Null(BossSpawnMessage.TryParse(bytes));
        Assert.Null(BossSpawnMessage.TryParse(List(new BossSpawn(111001, false, Now), new(111001, true, Now))));
        Assert.Null(BossSpawnMessage.TryParse([.. Convert.FromHexString(Captured), 0]));
    }

    [Fact]
    public void OptionalNpcDataAndSecondGroupAreHandled()
    {
        var entries = Enumerable.Range(111001, 24).Select((id, i) => new BossSpawn(id, i is 0 or 1 or 8 or 23, Now + i * 1000)).ToArray();
        var parsed = BossSpawnMessage.TryParse(List(entries));
        Assert.NotNull(parsed);
        Assert.Equal(entries, parsed.Bosses);
    }

    [Fact]
    public void MixedEightRecordListRetainsEveryIdAndOnlyTheAnnouncedTimerCanBecomeNext()
    {
        var entries = Enumerable.Range(2001, 8).Select(id => new BossSpawn(id, false, id == 2001 ? Now + 60000 : null)).ToArray();
        var message = ListForMap(20, entries);
        var parsed = BossSpawnMessage.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(20, parsed.MapId);
        Assert.Equal(entries, parsed.Bosses);
        Assert.Equal(Now + 60000, parsed.Bosses[0].SpawnUnixMs);
        Assert.All(parsed.Bosses.Skip(1), boss => Assert.Null(boss.SpawnUnixMs));

        var time = new ManualTime();
        var state = new BossRushState(time);
        Assert.False(state.Accept("game", message)); // An unknown time does not weaken the own-stream clock requirement.
        Assert.Null(state.Snapshot);
        state.Accept("game", Clock(Now));
        Assert.True(state.Accept("game", message));
        Assert.Equal(8, state.Snapshot!.Bosses.Count);
        Assert.Equal(2001, state.Snapshot.Next!.SpawnId);
        time.Advance(61000);
        Assert.False(state.Snapshot!.Next!.Spawned); // Elapsed known time still cannot confirm a spawn.
        Assert.All(state.Snapshot.Bosses.Skip(1), boss => { Assert.Null(boss.SpawnUnixMs); Assert.False(boss.Spawned); });
    }

    [Fact]
    public void AllUnknownTimesKeepTheCompleteListWithoutNextOrAlertsAcrossClockAndSessionChanges()
    {
        var entries = Enumerable.Range(2001, 8).Select(id => new BossSpawn(id, false, null)).ToArray();
        var parsed = BossSpawnMessage.TryParse(ListForMap(20, entries));
        Assert.NotNull(parsed);
        Assert.Equal(entries, parsed.Bosses);
        var time = new ManualTime();
        var state = new BossRushState(time);
        state.Accept("game", Clock(Now));
        Assert.True(state.Accept("game", ListForMap(20, entries)));
        Assert.Null(state.Snapshot!.Next);
        var selected = entries.Select(boss => $"20:{boss.SpawnId}").ToHashSet(StringComparer.Ordinal);
        Assert.Empty(new BossAlertPolicy().Evaluate(state.Snapshot, selected, 3600));
        time.Advance(3_600_000);
        Assert.Null(state.Snapshot!.Next);
        Assert.Equal(8, state.Snapshot.Bosses.Count);
        state.Invalidate();
        Assert.True(state.Snapshot!.Cached);
        Assert.Null(state.Snapshot.Next);
        Assert.All(state.Snapshot.Bosses, boss => Assert.Null(boss.SpawnUnixMs));
        Assert.Empty(new BossAlertPolicy().Evaluate(state.Snapshot, selected, 3600));
    }

    [Fact]
    public void UnknownTimesDoNotRelaxTruncationPackedMaskOrExactSuffixValidation()
    {
        var entries = Enumerable.Range(2001, 8).Select(id => new BossSpawn(id, false, id == 2001 ? Now + 60000 : null)).ToArray();
        var message = ListForMap(20, entries);
        Assert.NotNull(BossSpawnMessage.TryParse(message));
        for (var length = 0; length < message.Length; length++)
            Assert.Null(BossSpawnMessage.TryParse(message.AsSpan(0, length)));
        var maskMismatch = message.ToArray();
        Assert.Equal(0, maskMismatch[12]); // Header8, count1, waiting flag1, two-byte ID2001, carried mask1.
        maskMismatch[12] = 0x80; // Last waiting record contradicts its packed spawned bit.
        Assert.Null(BossSpawnMessage.TryParse(maskMismatch));
        var suffixMismatch = message.ToArray();
        suffixMismatch[^1] = 1;
        Assert.Null(BossSpawnMessage.TryParse(suffixMismatch));
        Assert.Null(BossSpawnMessage.TryParse([.. message, 0]));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1L)]
    [InlineData(1577836799999L)]
    [InlineData(4102444800000L)]
    [InlineData(long.MaxValue)]
    public void InvalidNonzeroTimesStillRejectTheWholeMixedListAndCannotBecomeServerClocks(long invalid)
    {
        Assert.Null(BossSpawnMessage.TryParse(ListForMap(20, new BossSpawn(2001, false, Now + 60000), new(2002, false, invalid), new(2003, false, null))));
        Assert.Null(BossSpawnMessage.ReadServerClock(Clock(invalid)));
    }

    [Fact]
    public void ZeroClockAndSpawnedZeroTimeRemainUnsupported()
    {
        Assert.Null(BossSpawnMessage.ReadServerClock(Clock(0)));
        Assert.Null(BossSpawnMessage.TryParse(ListForMap(20, new BossSpawn(2001, true, null))));
        var state = new BossRushState(new ManualTime());
        Assert.False(state.Accept("game", Clock(0)));
        Assert.False(state.Accept("game", ListForMap(20, new BossSpawn(2001, false, null))));
        Assert.Null(state.Snapshot);
    }

    [Fact]
    public void AliveBossesPrecedeFutureBossesInSpawnOrderAndDeathAdvancesTarget()
    {
        var time = new ManualTime();
        var state = new BossRushState(time);
        state.Accept("a", Clock(Now));
        state.Accept("a", List(new BossSpawn(111001, false, Now + 1000), new(111002, true, Now - 1000), new(111003, true, Now - 2000)));
        Assert.Equal(111003, state.Snapshot!.Next!.SpawnId);
        state.Accept("a", List(new BossSpawn(111001, false, Now + 1000), new(111002, true, Now - 1000), new(111003, false, Now + 3600000)));
        Assert.Equal(111002, state.Snapshot!.Next!.SpawnId);
        state.Accept("a", List(new BossSpawn(111001, false, Now + 1000), new(111002, false, Now + 7200000), new(111003, false, Now + 3600000)));
        Assert.Equal(111001, state.Snapshot!.Next!.SpawnId);
    }

    [Fact]
    public void ClockIsMonotonicAndBoundToItsConnectionAndExpiryDoesNotInventSpawn()
    {
        var time = new ManualTime();
        var state = new BossRushState(time);
        state.Accept("a", Clock(Now));
        Assert.False(state.Accept("b", List(new BossSpawn(111001, false, Now + 1000))));
        Assert.Null(state.Snapshot);
        Assert.True(state.Accept("a", List(new BossSpawn(111001, false, Now + 1000))));
        time.Advance(2000);
        state.Accept("b", Clock(Now + 86400000)); // cannot replace connection a's clock
        Assert.Equal(Now + 2000, state.Snapshot!.ServerUnixMs);
        Assert.False(state.Snapshot.Next!.Spawned);
        Assert.Equal(TimeSpan.FromSeconds(2), state.Snapshot.Age);
        state.Accept("a", List(new BossSpawn(111001, true, Now + 1000)));
        Assert.True(state.Snapshot!.Next!.Spawned);
        state.Reset();
        Assert.Null(state.Snapshot);
        Assert.False(state.Accept("a", List(new BossSpawn(111001, false, Now + 1000))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedListRemainsConfirmedUntilInvalidationWithOrWithoutClockUpdates(bool updateClocks)
    {
        var time = new ManualTime();
        var state = new BossRushState(time);
        var bosses = new[] { new BossSpawn(111001, false, Now + 300000), new BossSpawn(111002, true, Now - 1000) };
        state.Accept("game", Clock(Now));
        Assert.True(state.Accept("game", List(bosses)));
        for (var minute = 1; minute <= 10; minute++)
        {
            time.Advance(60000);
            if (updateClocks) Assert.False(state.Accept("game", Clock(Now + minute * 60000)));
            var snapshot = state.Snapshot!;
            Assert.False(snapshot.Cached);
            Assert.Equal(TimeSpan.FromMinutes(minute), snapshot.Age); // clocks do not pretend the list was freshly observed
            Assert.Equal(Now + minute * 60000, snapshot.ServerUnixMs);
            Assert.Equal(bosses, snapshot.Bosses);
            Assert.False(snapshot.Bosses.Single(boss => boss.SpawnId == 111001).Spawned); // even after its countdown expires
        }
        state.Invalidate();
        Assert.True(state.Snapshot!.Cached);
        Assert.Equal(TimeSpan.FromMinutes(10), state.Snapshot.Age);
        Assert.False(state.Accept("game", Clock(Now + 600000)));
        Assert.True(state.Snapshot.Cached); // clocks alone cannot reconfirm a retained list
        Assert.True(state.Accept("game", List(bosses)));
        Assert.False(state.Snapshot!.Cached);
        Assert.Equal(TimeSpan.Zero, state.Snapshot.Age);
    }

    [Fact]
    public void StableTieUsesSpawnIdInsteadOfPacketOrder()
    {
        var snapshot = new BossRushSnapshot(1110, [new(111004, true, Now), new(111003, true, Now)], Now, TimeSpan.Zero);
        Assert.Equal(111003, snapshot.Next!.SpawnId);
    }

    [Fact]
    public void PortRetainsScheduleAndMonotonicCountdownUntilItsOwnClockAndUpdatedListArrive()
    {
        var time = new ManualTime();
        var state = new BossRushState(time);
        state.Accept("old", Clock(Now));
        state.Accept("old", List(new BossSpawn(111001, false, Now + 60000)));
        time.Advance(5000);
        state.Invalidate();
        time.Advance(10000);
        Assert.True(state.Snapshot!.Cached);
        Assert.Equal(Now + 15000, state.Snapshot.ServerUnixMs);
        Assert.Equal(45000, state.Snapshot.Next!.SpawnUnixMs - state.Snapshot.ServerUnixMs);
        Assert.False(state.Accept("old", List(new BossSpawn(111001, true, Now + 60000)))); // old clock was discarded
        Assert.False(state.Accept("new", List(new BossSpawn(111001, true, Now + 60000))));
        Assert.False(state.Snapshot.Next.Spawned);
        state.Accept("other", Clock(Now + 86400000));
        Assert.Equal(Now + 15000, state.Snapshot.ServerUnixMs); // unrelated clock cannot rebase retained timers
        Assert.False(state.Accept("new", Clock(Now + 15000)));
        Assert.True(state.Snapshot.Cached); // its clock alone is not a new list confirmation
        Assert.True(state.Accept("new", List(new BossSpawn(111001, false, Now + 120000))));
        Assert.False(state.Snapshot!.Cached);
        Assert.Equal(Now + 120000, state.Snapshot.Next!.SpawnUnixMs);
        state.Invalidate();
        time.Advance(200000);
        Assert.False(state.Snapshot!.Next!.Spawned); // cached countdown expiry is never a confirmation
    }

    [Fact]
    public void UnrelatedStreamGapDiscardsOnlyItsClockAndKeepsTheConfirmedSchedule()
    {
        var time = new ManualTime();
        var state = new BossRushState(time);
        state.Accept("game", Clock(Now));
        state.Accept("game", List(new BossSpawn(111001, false, Now + 60000)));
        state.Accept("other", Clock(Now + 86400000));
        time.Advance(2000);
        Assert.False(state.Invalidate("other"));
        Assert.False(state.Snapshot!.Cached);
        Assert.Equal(Now + 2000, state.Snapshot.ServerUnixMs);
        Assert.Equal(TimeSpan.FromSeconds(2), state.Snapshot.Age);
        Assert.False(state.Accept("other", List(new BossSpawn(111001, true, Now)))); // only the broken stream lost its clock
        Assert.False(state.Snapshot.Next!.Spawned);
        Assert.True(state.Accept("game", List(new BossSpawn(111001, true, Now))));
        Assert.False(state.Snapshot!.Cached);
        Assert.True(state.Snapshot.Next!.Spawned);
    }

    [Fact]
    public void BoundStreamGapCachesOnceAndRequiresItsNewClockAndList()
    {
        var time = new ManualTime();
        var state = new BossRushState(time);
        var list = List(new BossSpawn(111001, false, Now + 60000));
        state.Accept("game", Clock(Now));
        state.Accept("game", list);
        time.Advance(2000);
        Assert.True(state.Invalidate("game"));
        Assert.True(state.Snapshot!.Cached);
        Assert.Equal(TimeSpan.FromSeconds(2), state.Snapshot.Age);
        Assert.False(state.Invalidate("game"));
        Assert.False(state.Accept("game", list)); // the former clock cannot confirm the next list
        Assert.False(state.Accept("other", Clock(Now + 86400000)));
        Assert.False(state.Accept("game", list)); // another stream's clock is not its own clock
        Assert.True(state.Snapshot.Cached);
        Assert.Equal(Now + 2000, state.Snapshot.ServerUnixMs);
        Assert.False(state.Accept("game", Clock(Now + 2000)));
        Assert.True(state.Snapshot.Cached); // a clock alone still does not confirm the list
        Assert.True(state.Accept("game", list));
        Assert.False(state.Snapshot!.Cached);
        Assert.Equal(TimeSpan.Zero, state.Snapshot.Age);
    }

    [Fact]
    public void GlobalGapCachesTheScheduleAndDiscardsAllClocksEvenWhenAlreadyCached()
    {
        var state = new BossRushState(new ManualTime());
        var list = List(new BossSpawn(111001, false, Now + 60000));
        state.Accept("game", Clock(Now));
        state.Accept("game", list);
        state.Accept("other", Clock(Now + 86400000));
        Assert.True(state.Invalidate("*"));
        Assert.True(state.Snapshot!.Cached);
        Assert.False(state.Accept("game", list));
        Assert.False(state.Accept("other", list));
        Assert.False(state.Invalidate("*"));
        state.Accept("other", Clock(Now));
        Assert.False(state.Invalidate("*")); // unchanged cached state, but a newly received clock is also discarded
        Assert.False(state.Accept("other", list));
        state.Accept("game", Clock(Now));
        Assert.True(state.Snapshot.Cached);
        Assert.True(state.Accept("game", list));
        Assert.False(state.Snapshot!.Cached);
        state.Reset();
        state.Accept("game", Clock(Now));
        Assert.False(state.Invalidate("*")); // even without a schedule, every clock must be discarded
        Assert.False(state.Accept("game", list));
        Assert.Null(state.Snapshot);
    }

    [Fact]
    public void RestoredScheduleUsesMonotonicTimeAndKeepsItsObservationAge()
    {
        var time = new ManualTime();
        var state = new BossRushState(time);
        state.Restore(new(1110, [new(111001, false, Now + 60000)], Now, TimeSpan.FromMinutes(1)));
        time.Advance(10000);
        Assert.True(state.Snapshot!.Cached);
        Assert.Equal(Now + 10000, state.Snapshot.ServerUnixMs);
        Assert.Equal(TimeSpan.FromSeconds(70), state.Snapshot.Age);
        Assert.False(state.Accept("game", Clock(Now + 10000)));
        Assert.True(state.Snapshot.Cached);
        Assert.Equal(TimeSpan.FromSeconds(70), state.Snapshot.Age);
        Assert.True(state.Accept("game", List(new BossSpawn(111001, false, Now + 60000))));
        Assert.False(state.Snapshot!.Cached);
        Assert.Equal(TimeSpan.Zero, state.Snapshot.Age);
    }

    private static byte[] Clock(long value) => [0, 0x36, .. BitConverter.GetBytes(value)];

    private static byte[] List(params BossSpawn[] entries) => ListForMap(1110, entries);

    private static byte[] ListForMap(int mapId, params BossSpawn[] entries)
    {
        List<byte> bytes = [1, 0x91, 0, 0, .. BitConverter.GetBytes(mapId), (byte)entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            bytes.Add(e.Spawned ? (byte)1 : (byte)0);
            var id = e.SpawnId;
            do { byte b = (byte)(id & 127); id >>= 7; bytes.Add((byte)(b | (id > 0 ? 128 : 0))); } while (id > 0);
            if (e.Spawned) bytes.AddRange(new byte[12]);
            if (i % 8 == 0)
            {
                byte flags = 0;
                for (int j = 0; j < 8 && i + j < entries.Length; j++) if (entries[i + j].Spawned) flags |= (byte)(1 << j);
                bytes.Add(flags);
            }
            bytes.AddRange(BitConverter.GetBytes(e.SpawnUnixMs ?? 0));
        }
        return [.. bytes, 0, 0, 0];
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _stamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _stamp;
        public void Advance(long milliseconds) => _stamp += milliseconds;
    }
}
