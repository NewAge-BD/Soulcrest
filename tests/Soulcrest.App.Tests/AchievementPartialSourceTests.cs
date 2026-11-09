using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Drawing.Imaging;
using System.Text;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementPartialSourceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private const string OwnStream = "synthetic-own";
    private const int Riding = 420101100;
    private const int Other = 999999;
    private static readonly int UnqualifiedFamily = CombatAchievementProgressContractCatalog.Default.Contracts
        .Where(type => type.CounterRule == CombatAchievementCounterRule.Unqualified)
        .SelectMany(type => type.Families).Where(family => family.Stages.Count > 1
            && AchievementProgressThresholdCatalog.Default.FindFamily(family.Id) is null
            && new AchievementTrackerStageResolver().IsEligible(family.Id, AchievementDefinitionCatalog.Default.MetadataBuild))
        .Select(family => family.Id).First();

    [Fact]
    public async Task ARealPartialChangeReachesTheNativeTrackerWithoutAFullList()
    {
        var rows = await PartialTrackerRows();
        var known = Assert.Single(rows, row => row.FamilyId == Riding);
        Assert.Equal(4, known.Current);
        Assert.Equal(5, known.Target);
        Assert.Equal(420101101, known.StageId);
        var unqualified = Assert.Single(rows, row => row.FamilyId == UnqualifiedFamily);
        Assert.Equal(67, unqualified.RawValue);
        Assert.Null(unqualified.Target);
        Assert.False(unqualified.HasQualifiedGoal);
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.NameEn)));
        using var image = AchievementOverlayRenderer.Render(rows, language: "en");
        Assert.Equal(AchievementOverlayRenderer.HeightFor(rows.Length), image.Height);
    }

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public async Task RenderAnonymousPartialSourceTracker()
    {
        var target = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_ACHIEVEMENT_PARTIAL_PNG"));
        var root = Path.GetFullPath(Path.Combine(TestPrerequisites.RepositoryRoot, "output", "social-media"));
        Assert.StartsWith(root + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var rows = await PartialTrackerRows();
        foreach (var language in new[] { "de", "en" })
        {
            using var image = AchievementOverlayRenderer.Render(rows, language: language);
            image.Save(Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + "-" + language + ".png"), ImageFormat.Png);
        }
    }

    private static async Task<ImmutableArray<AchievementTrackerRow>> PartialTrackerRows()
    {
        using var fixture = new Fixture();
        using var favorites = new AchievementFavoriteService(Path.Combine(fixture.Root, "favorites"));
        using var tracker = new AchievementTrackerService(fixture.Service, favorites);
        fixture.Login();
        fixture.Send(Rows(false, (Riding, 3, 0), (UnqualifiedFamily, 66, 0), (441100900, 18, 1)), 4);
        await fixture.Drain();
        tracker.Tick();
        Assert.Empty(tracker.Snapshot.Rows);
        fixture.Send(Rows(false, (Riding, 4, 0), (UnqualifiedFamily, 67, 0), (441100900, 19, 1)), 5);
        await fixture.Drain();
        tracker.Tick();
        Assert.Equal(3, tracker.Snapshot.Rows.Length);
        Assert.Equal(0, fixture.Network.AchievementDiagnostics.Lists5683Framed);
        return tracker.Snapshot.Rows;
    }

    [Fact]
    public async Task FirstOwnAbsoluteUpdateConfirmsOnlyItsRowsAsAPartialRecoveryBaseline()
    {
        using var fixture = new Fixture();
        fixture.Login();
        await fixture.Drain();

        fixture.Send(Rows(false, (Riding, 4, 0), (Other, -17, 0)), 4);
        await fixture.Drain();

        var observation = fixture.Network.AchievementSnapshot;
        Assert.Equal(NetworkAchievementObservationKind.RecoveryBaseline, observation.Kind);
        Assert.False(observation.HistoryGap);
        Assert.NotNull(observation.Character);
        Assert.Equal(observation.Generation, observation.Character.Generation);
        Assert.True(observation.Domain.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Partial, observation.Domain.Status);
        Assert.Equal("own-achievement-update", observation.Domain.Source);
        Assert.Equal(At.AddSeconds(4), observation.Domain.ObservedAt);
        Assert.Equal(new[] { Riding, Other }.Order(), observation.Domain.Value!.Achievements.Keys.Order());
        Assert.Equal(4, observation.Domain.Value.Achievements[Riding].Count);
        Assert.Equal(-17, observation.Domain.Value.Achievements[Other].Count);
        Assert.Empty(observation.Domain.Value.RewardGroups);
        Assert.Empty(fixture.Service.View.Activities);
        Assert.Empty(fixture.Service.View.Milestones);
        Assert.Equal("received-partial", fixture.Store.Load(fixture.Binding).Snapshot!.Coverage);
        Assert.Equal(0, fixture.Network.AchievementDiagnostics.Lists5683Framed);
    }

    [Fact]
    public async Task FurtherOwnAbsoluteUpdatesMergeWithoutChangingPartialCoverageOrOldSnapshots()
    {
        using var fixture = new Fixture();
        fixture.Login();
        fixture.Send(Rows(false, (Riding, 4, 0)), 4);
        await fixture.Drain();
        var first = fixture.Network.AchievementSnapshot;

        fixture.Send(Rows(false, (Other, 12, 0)), 5);
        fixture.Send(Rows(false, (Riding, 3, 0)), 6);
        await fixture.Drain();

        var last = fixture.Network.AchievementSnapshot;
        Assert.Equal(NetworkAchievementObservationKind.Update, last.Kind);
        Assert.Equal(NetworkCombatSourceStatus.Partial, last.Domain.Status);
        Assert.Equal(3, last.Domain.Value!.Achievements[Riding].Count);
        Assert.Equal(12, last.Domain.Value.Achievements[Other].Count);
        Assert.Empty(last.Domain.Value.RewardGroups);
        Assert.Equal(4, Assert.Single(first.Domain.Value!.Achievements).Value.Count);
        Assert.Equal("received-partial", fixture.Store.Load(fixture.Binding).Snapshot!.Coverage);
    }

    [Fact]
    public async Task ANewOwnAfterTransportGapNeverRefreshesStaleRowsOrRewardGroups()
    {
        using var fixture = new Fixture();
        fixture.Login();
        fixture.Send(Rows(true, (Riding, 4, 0), (Other, 12, 0)), 4);
        await fixture.Drain();
        var old = fixture.Network.AchievementSnapshot;
        Assert.NotEmpty(old.Domain.Value!.RewardGroups);

        fixture.Network.ProcessProgressGap(OwnStream, capturedAtUtc: At.AddSeconds(5));
        fixture.Login(10);
        fixture.Send(Rows(false, (888888, 7, 0)), 14);
        await fixture.Drain();

        var recovered = fixture.Network.AchievementSnapshot;
        Assert.True(recovered.Generation > old.Generation);
        Assert.Equal(NetworkAchievementObservationKind.RecoveryBaseline, recovered.Kind);
        Assert.True(recovered.Domain.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Partial, recovered.Domain.Status);
        Assert.Equal(888888, Assert.Single(recovered.Domain.Value!.Achievements).Key);
        Assert.Empty(recovered.Domain.Value.RewardGroups);
        Assert.Equal(2, old.Domain.Value.Achievements.Count);
        Assert.NotEmpty(old.Domain.Value.RewardGroups);
        Assert.DoesNotContain(Riding, fixture.Service.View.ObservedValues.Keys);
        Assert.DoesNotContain(Other, fixture.Service.View.ObservedValues.Keys);
    }

    [Fact]
    public async Task FirstUpdateAfterGapImportsCompletionWithoutALiveCatchUpNotification()
    {
        using var fixture = new Fixture();
        fixture.Login();
        fixture.Send(Rows(false, (Riding, 4, 0)), 4);
        await fixture.Drain();
        Assert.Empty(fixture.Service.View.Milestones);
        fixture.Network.ProcessProgressGap(OwnStream, capturedAtUtc: At.AddSeconds(5));
        fixture.Login(10);

        fixture.Send(Rows(false, (Riding, 5, 0)), 14);
        await fixture.Drain();

        Assert.Equal(NetworkAchievementObservationKind.RecoveryBaseline, fixture.Network.AchievementSnapshot.Kind);
        Assert.Empty(fixture.Service.View.Activities);
        var completion = Assert.Single(fixture.Service.View.Milestones);
        Assert.Equal(AchievementMilestoneKind.Completed, completion.Kind);
        Assert.Equal(420101101, completion.StageId);
        Assert.Equal("Recovery", completion.Origin);
        Assert.False(completion.NotificationEligible);
        Assert.Equal("received-partial", fixture.Store.Load(fixture.Binding).Snapshot!.Coverage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUpdateBeforeCompleteOwnCannotSeedAnAchievementSource(bool matchingCandidate)
    {
        using var fixture = new Fixture();
        var received = new ConcurrentQueue<NetworkAchievementObservation>();
        fixture.Network.AchievementObserved += received.Enqueue;
        if (matchingCandidate) fixture.Send(Candidate(), 0);
        fixture.Send(Rows(false, (Riding, 5, 0)), 1);
        if (!matchingCandidate) fixture.Send(Candidate(), 2);
        fixture.Send(Prepare(), 3);
        fixture.Send(Own(), 4);
        await fixture.Drain();

        Assert.DoesNotContain(received, item => item.Domain.IsFresh);
        Assert.False(fixture.Network.AchievementSnapshot.Domain.IsFresh);
        Assert.Empty(fixture.Service.View.ObservedValues);

        fixture.Send(Rows(false, (Other, 8, 0)), 5);
        await fixture.Drain();
        Assert.Equal(Other, Assert.Single(fixture.Network.AchievementSnapshot.Domain.Value!.Achievements).Key);
    }

    [Fact]
    public async Task ForeignStreamsAndOlderFactsCannotSeedOrModifyTheOwnPartialSource()
    {
        using var fixture = new Fixture();
        fixture.Login();
        await fixture.Drain();
        var unknown = fixture.Network.AchievementSnapshot;
        fixture.Send(Rows(false, (Other, 20, 0)), 4, "synthetic-other");
        fixture.Send(Rows(false, (Other, 30, 0)), 2); // Before the confirmed Own at second 3.
        await fixture.Drain();
        Assert.Same(unknown, fixture.Network.AchievementSnapshot);

        fixture.Send(Rows(false, (Riding, 4, 0)), 6);
        await fixture.Drain();
        var current = fixture.Network.AchievementSnapshot;
        fixture.Send(Rows(false, (Other, 40, 0)), 7, "synthetic-other");
        fixture.Send(Rows(false, (Riding, 3, 0), (Other, 50, 0)), 5);
        await fixture.Drain();

        Assert.Same(current, fixture.Network.AchievementSnapshot);
        Assert.Equal(4, Assert.Single(current.Domain.Value!.Achievements).Value.Count);
    }

    [Fact]
    public async Task AnEmptyUpdateCannotCreateAConfirmedPartialBaseline()
    {
        using var fixture = new Fixture();
        fixture.Login();
        await fixture.Drain();
        var unknown = fixture.Network.AchievementSnapshot;

        fixture.Send(Rows(false), 4);
        await fixture.Drain();

        Assert.Same(unknown, fixture.Network.AchievementSnapshot);
        Assert.False(fixture.Network.AchievementSnapshot.Domain.IsFresh);
        Assert.Empty(fixture.Service.View.ObservedValues);
    }

    [Fact]
    public async Task ACompleteListReplacesPartialRowsAndRestoresCompleteCoverage()
    {
        using var fixture = new Fixture();
        fixture.Login();
        fixture.Send(Rows(false, (Riding, 4, 0), (Other, 12, 0)), 4);
        await fixture.Drain();
        var partial = fixture.Network.AchievementSnapshot;

        fixture.Send(Rows(true, (Riding, 7, 1), (888888, 9, 0)), 5);
        await fixture.Drain();

        var complete = fixture.Network.AchievementSnapshot;
        Assert.Equal(NetworkAchievementObservationKind.Baseline, complete.Kind);
        Assert.Equal(NetworkCombatSourceStatus.Complete, complete.Domain.Status);
        Assert.Equal("own-achievement-list", complete.Domain.Source);
        Assert.Equal(2, complete.Domain.Value!.Achievements.Count);
        Assert.False(complete.Domain.Value.Achievements.ContainsKey(Other));
        Assert.Equal(7, complete.Domain.Value.Achievements[Riding].Count);
        Assert.Equal(new[] { true, false, true }, complete.Domain.Value.RewardGroups[77].ToArray());
        Assert.Equal(12, partial.Domain.Value!.Achievements[Other].Count);
        Assert.Empty(partial.Domain.Value.RewardGroups);
        Assert.Equal("received-list", fixture.Store.Load(fixture.Binding).Snapshot!.Coverage);
    }

    [Fact]
    public async Task DuplicateAbsoluteUpdatesDoNotCreateActivityOrDuplicateCompletionMilestones()
    {
        using var fixture = new Fixture();
        fixture.Login();
        fixture.Send(Rows(false, (Riding, 4, 0)), 4);
        fixture.Send(Rows(false, (Riding, 4, 0)), 5);
        await fixture.Drain();
        Assert.Empty(fixture.Service.View.Activities);
        Assert.Empty(fixture.Service.View.Milestones);

        fixture.Send(Rows(false, (Riding, 5, 0)), 6);
        await fixture.Drain();
        var activity = Assert.Single(fixture.Service.View.Activities).Value;
        var completion = Assert.Single(fixture.Service.View.Milestones);
        Assert.Equal("Live", completion.Origin);
        Assert.True(completion.NotificationEligible);

        fixture.Send(Rows(false, (Riding, 5, 0)), 7);
        fixture.Send(Rows(false, (Riding, 5, 0)), 8);
        await fixture.Drain();

        Assert.Equal(activity, Assert.Single(fixture.Service.View.Activities).Value);
        Assert.Equal(completion, Assert.Single(fixture.Service.View.Milestones));
        Assert.Single(fixture.Store.Load(fixture.Binding).Milestones);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-achievement-partial-" + Guid.NewGuid().ToString("N"));
        internal string Root => _root;
        internal AchievementProfileBinding Binding { get; } = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal AchievementStateStore Store { get; }
        internal AchievementStateService Service { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Store = new(Path.Combine(_root, "achievements"));
            Service = new(Network, Store, _ => Binding, () => Binding);
        }

        internal void Send(byte[] message, int second, string stream = OwnStream) =>
            Network.ProcessProgressMessage(stream, message, At.AddSeconds(second));

        internal void Login(int second = 0)
        {
            Send(Candidate(), second);
            Send(Prepare(), second + 1);
            Send(Own(), second + 3);
        }

        internal async Task Drain()
        {
            await Network.WaitForAchievementNotificationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Service.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            Network.Dispose();
            Service.Dispose();
            var path = Path.GetFullPath(_root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static byte[] Rows(bool full, params (int Id, int Count, byte Step)[] rows) => Packet(full ? (ushort)0x5683 : (ushort)0x5684, writer =>
    {
        V(writer, (uint)rows.Length);
        foreach (var row in rows)
        {
            writer.Write(row.Id); writer.Write(row.Count); writer.Write(1234567890123L); writer.Write(row.Step);
        }
        if (full) { V(writer, 1); writer.Write(77); V(writer, 3); writer.Write((byte)0b00000101); }
    });

    private static byte[] Candidate() => Packet(0x3620, writer =>
    {
        writer.Write((ushort)0); V(writer, 300); writer.Write(0); writer.Write(101L);
    });

    private static byte[] Prepare() => Packet(0x3621, writer => writer.Write(new byte[46]));

    private static byte[] Own() => Packet(0x3633, writer =>
    {
        V(writer, 300); writer.Write(0); writer.Write((byte)1);
        var name = Encoding.UTF8.GetBytes("SyntheticAlpha"); V(writer, (uint)name.Length); writer.Write(name);
        writer.Write((ushort)501); writer.Write(31); writer.Write(45); writer.Write(new byte[20]); writer.Write(1); writer.Write(0L);
        V(writer, 0); V(writer, 0); writer.Write(new byte[16]); writer.Write((byte)0);
        writer.Write((ushort)0); writer.Write(new byte[18]); writer.Write((byte)1); V(writer, 0); V(writer, 0);
        writer.Write(0); writer.Write(0); writer.Write(new byte[44]);
        V(writer, 1); writer.Write((ushort)193); writer.Write(5000); V(writer, 0); V(writer, 0);
        writer.Write(new byte[12]); writer.Write(101L); V(writer, 0); writer.Write((byte)1);
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
    {
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }
}
