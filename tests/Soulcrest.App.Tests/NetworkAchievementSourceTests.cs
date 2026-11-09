using System.Collections.Concurrent;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkAchievementSourceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private const int Family = 420101100;

    [Fact]
    public async Task ProvisionalRowsRemainInvisibleUntilMatchingCompleteOwn()
    {
        using var fixture = new Fixture();
        var received = new ConcurrentQueue<NetworkAchievementObservation>();
        fixture.Network.AchievementObserved += received.Enqueue;
        fixture.Send(Candidate(), 0);
        fixture.Send(Rows(true, (Family, 4, 0), (999999, -17, 0)), 1);
        fixture.Send(Rows(false, (Family, 5, 0)), 2);
        fixture.Send(Prepare(), 3);
        await Drain(fixture.Network);
        Assert.DoesNotContain(received, item => item.Domain.IsFresh);
        Assert.Null(fixture.Network.AchievementSnapshot.Character);

        fixture.Send(Own(), 4);
        await Drain(fixture.Network);
        var baseline = Assert.Single(received, item => item.Domain.IsFresh);
        Assert.Equal(NetworkAchievementObservationKind.Baseline, baseline.Kind);
        Assert.NotNull(baseline.Character);
        Assert.Equal(5, baseline.Domain.Value!.Achievements[Family].Count);
        Assert.Equal(-17, baseline.Domain.Value.Achievements[999999].Count);
        Assert.Equal(new[] { true, false, true }, baseline.Domain.Value.RewardGroups[77].ToArray());
        Assert.NotEqual(Guid.Empty, baseline.SourceInstanceId);
    }

    [Fact]
    public async Task RapidCompletionAndClaimPreserveEveryAcceptedImmutableStateInOrder()
    {
        using var fixture = new Fixture();
        fixture.Login();
        await Drain(fixture.Network);
        var received = new ConcurrentQueue<NetworkAchievementObservation>();
        fixture.Network.AchievementObserved += received.Enqueue;
        fixture.Send(Rows(true, (Family, 4, 0)), 4);
        fixture.Send(Rows(false, (Family, 5, 0)), 5);
        fixture.Send(Reward(1), 6);
        fixture.Send(Reward(1), 7); // Duplicate and regressive ACKs have no authority to refresh facts.
        fixture.Send(Reward(0), 8);
        fixture.Send(Rows(false, (Family, 8, 0)), 4); // Older fact.
        fixture.Send(Rows(false, (Family, 9, 0)), 9, "synthetic-other");
        await Drain(fixture.Network);

        var events = received.ToArray();
        Assert.Equal(new[] { NetworkAchievementObservationKind.Baseline, NetworkAchievementObservationKind.Update,
            NetworkAchievementObservationKind.Reward }, events.Select(item => item.Kind));
        Assert.Equal(new[] { 4, 5, 5 }, events.Select(item => item.Domain.Value!.Achievements[Family].Count));
        Assert.Equal(new byte[] { 0, 0, 1 }, events.Select(item => item.Domain.Value!.Achievements[Family].RewardedStep));
        Assert.Equal(At.AddSeconds(5), events[1].Domain.ObservedAt);
        Assert.Equal(At.AddSeconds(6), events[2].Domain.ObservedAt);
        AssertOrdered(events);
        Assert.All(events, item => Assert.Equal(events[0].SourceInstanceId, item.SourceInstanceId));
        Assert.Equal(0, events[0].Domain.Value!.Achievements[Family].RewardedStep);
    }

    [Fact]
    public async Task ReentrantCharacterChangeDoesNotInterleaveSubscribersOrRebindAnOldEvent()
    {
        using var fixture = new Fixture();
        fixture.Login();
        fixture.Send(Rows(true, (Family, 4, 0)), 4);
        await Drain(fixture.Network);
        var received = new ConcurrentQueue<NetworkAchievementObservation>();
        fixture.Network.AchievementObserved += item =>
        {
            if (item.Kind != NetworkAchievementObservationKind.Update) return;
            fixture.Send(Own(character: 102, entity: 301), 6);
            fixture.Send(Rows(true, (999999, 12, 0)), 7);
        };
        fixture.Network.AchievementObserved += received.Enqueue;
        fixture.Send(Rows(false, (Family, 5, 0)), 5);
        await Drain(fixture.Network);

        var events = received.ToArray();
        Assert.Equal(NetworkAchievementObservationKind.Update, events[0].Kind);
        Assert.Equal(101, events[0].Character!.Identity.CharacterId);
        Assert.Equal(5, events[0].Domain.Value!.Achievements[Family].Count);
        Assert.Contains(events, item => item.Kind == NetworkAchievementObservationKind.Invalidated
            && item.Character?.Identity.CharacterId == 101);
        Assert.Equal(102, events[^1].Character!.Identity.CharacterId);
        Assert.False(events[^1].Domain.Value!.Achievements.ContainsKey(Family));
        AssertOrdered(events);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("conflict")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task BoundariesPublishInvalidationWithoutLosingThePreviousOwner(string boundary)
    {
        using var fixture = new Fixture();
        fixture.Login();
        fixture.Send(Rows(true, (Family, 5, 0)), 4);
        await Drain(fixture.Network);
        var before = fixture.Network.AchievementSnapshot;
        var received = new ConcurrentQueue<NetworkAchievementObservation>();
        fixture.Network.AchievementObserved += received.Enqueue;
        switch (boundary)
        {
            case "gap": fixture.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(5)); break;
            case "conflict": fixture.Send([0x83, 0x56], 5); break;
            case "stop": fixture.Network.Stop(); break;
            case "dispose": fixture.Network.Dispose(); break;
        }
        fixture.Send(Reward(1), 6);
        await Drain(fixture.Network);
        var last = received.Last();
        Assert.Equal(NetworkAchievementObservationKind.Invalidated, last.Kind);
        Assert.False(last.Domain.IsFresh);
        Assert.Equal(101, last.Character!.Identity.CharacterId);
        Assert.Equal(0, before.Domain.Value!.Achievements[Family].RewardedStep);
        Assert.True(last.Sequence > before.Sequence);
    }

    [Fact]
    public async Task AThrowingConsumerCannotSuppressAnotherConsumerAndDoesNotWriteDiagnostics()
    {
        using var fixture = new Fixture();
        fixture.Login();
        await Drain(fixture.Network);
        var received = new ConcurrentQueue<NetworkAchievementObservation>();
        fixture.Network.AchievementObserved += _ => throw new InvalidOperationException("Synthetic failure");
        fixture.Network.AchievementObserved += received.Enqueue;
        fixture.Send(Rows(true, (Family, 4, 0)), 4);
        fixture.Send(Rows(false, (Family, 5, 0)), 5);
        await Drain(fixture.Network);
        Assert.Equal(2, received.Count);
        Assert.Equal(2, fixture.Network.AchievementDiagnostics.SubscriberFailures);
    }

    [Fact]
    public async Task BoundedQueueMarksHistoryGapRecoversFromRamAndRetainsFinalShutdownInvalidation()
    {
        using var fixture = new Fixture();
        fixture.Login();
        fixture.Send(Rows(true, (Family, 1, 0)), 4);
        await Drain(fixture.Network);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var received = new ConcurrentQueue<NetworkAchievementObservation>();
        var blockOnce = 0;
        fixture.Network.AchievementObserved += item =>
        {
            if (Interlocked.Exchange(ref blockOnce, 1) == 0)
            {
                blocked.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            received.Enqueue(item);
        };
        fixture.Send(Rows(false, (Family, 2, 0)), 5);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (var count = 3; count <= 170; count++) fixture.Send(Rows(false, (Family, count, 0)), 6);
            Assert.True(fixture.Network.AchievementDiagnostics.QueueOverflows > 0);
            Assert.InRange(fixture.Network.AchievementDiagnostics.PendingNotifications, 1, 128);
            fixture.Network.Dispose();
        }
        finally { release.Set(); }
        await Drain(fixture.Network);
        var events = received.ToArray();
        Assert.Contains(events, item => item.HistoryGap && item.Kind == NetworkAchievementObservationKind.Invalidated);
        Assert.Contains(events, item => item.HistoryGap && item.Kind == NetworkAchievementObservationKind.RecoveryBaseline
            && item.Domain.IsFresh);
        Assert.Equal(NetworkAchievementObservationKind.Invalidated, events[^1].Kind);
        Assert.False(events[^1].Domain.IsFresh);
        Assert.Equal(170, events[^1].Domain.Value!.Achievements[Family].Count);
        AssertOrdered(events);
    }

    [Fact]
    public async Task AnonymousIngressDistinguishesFramingParseBindingAndAcceptedOwnSource()
    {
        using var fixture = new Fixture();
        fixture.Send([0x33, 0x36], 0, "synthetic-other");
        fixture.Send(Rows(true, (Family, 3, 0)), 0, "synthetic-other");
        fixture.Login();
        fixture.Send(Rows(true, (Family, 4, 0)), 4);
        fixture.Send(Rows(false, (Family, 5, 0)), 5);
        fixture.Send(Reward(1), 6);
        await Drain(fixture.Network);
        var diagnostics = fixture.Network.AchievementDiagnostics;
        Assert.Equal(1, diagnostics.Entry3620Framed);
        Assert.Equal(1, diagnostics.Entry3620Parsed);
        Assert.Equal(1, diagnostics.Own3621Framed);
        Assert.Equal(1, diagnostics.Own3621Parsed);
        Assert.Equal(2, diagnostics.Own3633Framed);
        Assert.Equal(1, diagnostics.Own3633Parsed);
        Assert.Equal(1, diagnostics.OwnBindings);
        Assert.Equal(2, diagnostics.Lists5683Framed);
        Assert.Equal(1, diagnostics.AcceptedBaselines);
        Assert.Equal(1, diagnostics.AcceptedUpdates);
        Assert.Equal(1, diagnostics.AcceptedRewards);
        Assert.True(diagnostics.CharacterBound);
        Assert.True(diagnostics.SourceFresh);
        var report = DiagnosticsService.FormatAchievementIngressDiagnostics(diagnostics);
        Assert.Contains("Own3633Framed: 2; Own3633Parsed: 1; OwnBindings: 1", report);
        Assert.DoesNotContain("SyntheticAlpha", report);
        Assert.DoesNotContain("synthetic-own", report);
        Assert.DoesNotContain("SyntheticAlpha", fixture.Network.AchievementSnapshot.ToString());
    }

    private static Task Drain(NetworkLootService source) => source.WaitForAchievementNotificationsAsync().WaitAsync(TimeSpan.FromSeconds(5));

    private static void AssertOrdered(NetworkAchievementObservation[] events)
    {
        for (var index = 1; index < events.Length; index++) Assert.True(events[index].Sequence > events[index - 1].Sequence);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-achievement-source-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        }
        internal void Send(byte[] message, int second, string stream = "synthetic-own") =>
            Network.ProcessProgressMessage(stream, message, At.AddSeconds(second));
        internal void Login() { Send(Candidate(), 0); Send(Prepare(), 1); Send(Own(), 3); }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, true); }
    }

    private static byte[] Rows(bool full, params (int Id, int Count, byte Step)[] rows) => Packet(full ? (ushort)0x5683 : (ushort)0x5684, writer =>
    {
        V(writer, (uint)rows.Length);
        foreach (var row in rows) { writer.Write(row.Id); writer.Write(row.Count); writer.Write(1234567890123L); writer.Write(row.Step); }
        if (full) { V(writer, 1); writer.Write(77); V(writer, 3); writer.Write((byte)0b00000101); }
    });
    private static byte[] Reward(byte step) => Packet(0x5686, writer => { writer.Write((ushort)0); writer.Write(Family); writer.Write(step); });
    private static byte[] Candidate() => Packet(0x3620, writer => { writer.Write((ushort)0); V(writer, 300); writer.Write(0); writer.Write(101L); });
    private static byte[] Prepare() => Packet(0x3621, writer => writer.Write(new byte[46]));
    private static byte[] Own(long character = 101, uint entity = 300) => Packet(0x3633, writer =>
    {
        V(writer, entity); writer.Write(0); writer.Write((byte)1);
        var name = Encoding.UTF8.GetBytes("SyntheticAlpha"); V(writer, (uint)name.Length); writer.Write(name);
        writer.Write((ushort)501); writer.Write(31); writer.Write(45); writer.Write(new byte[20]); writer.Write(1); writer.Write(0L);
        V(writer, 0); V(writer, 0); writer.Write(new byte[16]); writer.Write((byte)0);
        writer.Write((ushort)0); writer.Write(new byte[18]); writer.Write((byte)1); V(writer, 0); V(writer, 0);
        writer.Write(0); writer.Write(0); writer.Write(new byte[44]);
        V(writer, 1); writer.Write((ushort)193); writer.Write(5000); V(writer, 0); V(writer, 0);
        writer.Write(new byte[12]); writer.Write(character); V(writer, 0); writer.Write((byte)1);
        writer.Write(new byte[16]); writer.Write(12345L); writer.Write(67890L);
        V(writer, 3); writer.Write((byte)1); writer.Write((ushort)5); writer.Write((byte)2); writer.Write((ushort)0); writer.Write((byte)3); writer.Write((ushort)2);
        V(writer, 3); writer.Write((byte)1); writer.Write((ushort)4); writer.Write((byte)2); writer.Write((ushort)0); writer.Write((byte)3); writer.Write((ushort)2);
    });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer); return bytes.ToArray();
    }
    private static void V(BinaryWriter writer, uint value)
    { while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; } writer.Write((byte)value); }
}
