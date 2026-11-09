using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementStateStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void MissingStoreMeansUnknownAndReadingCreatesNoFiles()
    {
        using var fixture = new Fixture();
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Null(read.Snapshot);
        Assert.Empty(read.Milestones);
        Assert.Empty(read.Issues);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public void LastStatePreservesUnknownIdsNegativeCountersOpaqueTimesAndExactGroupBits()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Snapshot(-17) with
        {
            Raw = new(ImmutableDictionary<int, CharacterAchievement>.Empty
                .Add(-91, new(-17, long.MinValue, 255)).Add(100001, new(0, 123456789, 0)),
                ImmutableDictionary<int, ImmutableArray<bool>>.Empty.Add(-8, [true, false, false, true])),
            Coverage = "complete-transmitted-list",
        };
        fixture.Store.Save(snapshot, []);
        var read = fixture.Store.Load(fixture.Binding);
        Assert.NotNull(read.Snapshot);
        Assert.Equal(snapshot.Raw.Achievements[-91], read.Snapshot.Raw.Achievements[-91]);
        Assert.Equal(snapshot.Raw.RewardGroups[-8].ToArray(), read.Snapshot.Raw.RewardGroups[-8].ToArray());
        Assert.Equal(snapshot.Coverage, read.Snapshot.Coverage);
        Assert.Equal(0, read.Snapshot.JournalWatermark);
        Assert.Empty(read.Issues);
        Assert.False(File.Exists(fixture.Journal));
    }

    [Fact]
    public void CounterChangesReplaceLatestWithoutGrowingTheMilestoneJournal()
    {
        using var fixture = new Fixture();
        var first = fixture.Snapshot(5);
        var saved = fixture.Store.Save(first, [fixture.Milestone(first)]);
        var journal = File.ReadAllBytes(fixture.Journal);
        var later = fixture.Snapshot(6) with { ObservedAt = At.AddSeconds(1), SourceSequence = 2 };
        fixture.Store.Save(later, []);
        Assert.Equal(journal, File.ReadAllBytes(fixture.Journal));
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Equal(6, read.Snapshot!.Raw.Achievements[101].Count);
        Assert.Equal(saved.JournalWatermark, read.Snapshot.JournalWatermark);
        Assert.Single(read.Milestones);
    }

    [Fact]
    public void RepeatedImportsAndAcknowledgementsKeepTheOriginalHistoricalFact()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Snapshot(5);
        var historical = fixture.Milestone(snapshot) with { Origin = "Baseline", NotificationEligible = false };
        fixture.Store.Save(snapshot, [historical]);
        var duplicate = historical with { FirstObservedAt = At.AddMinutes(1), Origin = "Live", NotificationEligible = true };
        fixture.Store.Save(snapshot with { ObservedAt = At.AddMinutes(1) }, [duplicate, duplicate]);
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Single(read.Milestones);
        Assert.Equal(At, read.Milestones[0].FirstObservedAt);
        Assert.False(read.Milestones[0].NotificationEligible);
        Assert.Null(read.Milestones[0].OccurredAt);
    }

    [Fact]
    public void CrashAfterDurableAppendRecoversTheInitialFullCheckpoint()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Snapshot(5) with
        {
            Raw = fixture.Snapshot(5).Raw with
            {
                Achievements = fixture.Snapshot(5).Raw.Achievements.Add(9999, new(-4, -22, 0)),
            },
        };
        var failing = new AchievementStateStore(fixture.Root, AppendDurably,
            (_, _) => throw new IOException("Synthetic snapshot failure."));
        Assert.Throws<IOException>(() => failing.Save(snapshot, [fixture.Milestone(snapshot)]));
        Assert.False(File.Exists(fixture.Latest));
        var recovered = fixture.Store.Load(fixture.Binding);
        Assert.Equal(-4, recovered.Snapshot!.Raw.Achievements[9999].Count);
        Assert.Equal(1, recovered.Snapshot.JournalWatermark);
        Assert.Single(recovered.Milestones);
        Assert.Empty(recovered.Issues);
    }

    [Fact]
    public void RecoveryAfterAnExistingCheckpointIsIdempotent()
    {
        using var fixture = new Fixture();
        var initial = fixture.Snapshot(5);
        fixture.Store.Save(initial, [fixture.Milestone(initial)]);
        var later = fixture.Snapshot(20) with { ObservedAt = At.AddMinutes(1), SourceSequence = 2 };
        var claim = fixture.Milestone(later) with { Id = "test-claim", Kind = AchievementMilestoneKind.RewardClaimed };
        var failing = new AchievementStateStore(fixture.Root, AppendDurably,
            (_, _) => throw new IOException("Synthetic snapshot failure."));
        Assert.Throws<IOException>(() => failing.Save(later, [claim]));
        var recovered = fixture.Store.Load(fixture.Binding);
        Assert.Equal(20, recovered.Snapshot!.Raw.Achievements[101].Count);
        Assert.Equal(2, recovered.Milestones.Length);
        fixture.Store.Save(recovered.Snapshot, [claim]);
        var reloaded = fixture.Store.Load(fixture.Binding);
        Assert.Equal(2, reloaded.Milestones.Length);
        Assert.Equal(2, reloaded.Snapshot!.JournalWatermark);
    }

    [Fact]
    public void TornTailPreservesValidFactsAndFutureAppendsRemainReadable()
    {
        using var fixture = new Fixture();
        var first = fixture.Snapshot(5);
        fixture.Store.Save(first, [fixture.Milestone(first)]);
        File.AppendAllText(fixture.Journal, "{\"SchemaVersion\":1", new UTF8Encoding(false));
        var damaged = fixture.Store.Load(fixture.Binding);
        Assert.True(damaged.Snapshot!.HistoryGap);
        Assert.Contains("journal-tail-incomplete", damaged.Issues);
        Assert.Single(damaged.Milestones);
        var later = fixture.Snapshot(20) with { ObservedAt = At.AddSeconds(1) };
        fixture.Store.Save(later, [fixture.Milestone(later) with { Id = "test-second", StageId = 102 }]);
        var recovered = new AchievementStateStore(fixture.Root).Load(fixture.Binding);
        Assert.True(recovered.Snapshot!.HistoryGap);
        Assert.Equal(20, recovered.Snapshot.Raw.Achievements[101].Count);
        Assert.Equal(2, recovered.Milestones.Length);
    }

    [Fact]
    public void CorruptTerminatedRecordDoesNotDeleteValidRecordsOnEitherSide()
    {
        using var fixture = new Fixture();
        var first = fixture.Snapshot(5);
        fixture.Store.Save(first, [fixture.Milestone(first)]);
        File.AppendAllText(fixture.Journal, "invalid-record\n", new UTF8Encoding(false));
        var later = fixture.Snapshot(10) with { ObservedAt = At.AddSeconds(1) };
        fixture.Store.Save(later, [fixture.Milestone(later) with { Id = "test-later", StageId = 102 }]);
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Equal(2, read.Milestones.Length);
        Assert.Contains("journal-frame-unreadable", read.Issues);
        Assert.True(read.Snapshot!.HistoryGap);
    }

    [Fact]
    public void ExactDuplicateJournalFrameIsIgnored()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Snapshot(5);
        fixture.Store.Save(snapshot, [fixture.Milestone(snapshot)]);
        File.AppendAllText(fixture.Journal, File.ReadAllText(fixture.Journal), new UTF8Encoding(false));
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Single(read.Milestones);
        Assert.Empty(read.Issues);
    }

    [Fact]
    public void ConflictingDuplicateMilestoneCannotOverwriteAnEarnedFact()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Snapshot(5);
        var milestone = fixture.Milestone(snapshot);
        fixture.Store.Save(snapshot, [milestone]);
        var before = File.ReadAllBytes(fixture.Journal);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(snapshot, [milestone with { StageId = 999 }]));
        Assert.Equal(before, File.ReadAllBytes(fixture.Journal));
    }

    [Fact]
    public void FutureSchemaIsReportedAndCannotBeOverwritten()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Latest)!);
        const string future = "{\"SchemaVersion\":2,\"FuturePayload\":true}";
        File.WriteAllText(fixture.Latest, future);
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Null(read.Snapshot);
        Assert.Contains("snapshot-schema-unsupported", read.Issues);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(fixture.Snapshot(1), []));
        Assert.Equal(future, File.ReadAllText(fixture.Latest));
    }

    [Fact]
    public void DuplicateJsonPropertiesDoNotEstablishAValidSnapshot()
    {
        using var fixture = new Fixture();
        fixture.Store.Save(fixture.Snapshot(1), []);
        var json = File.ReadAllText(fixture.Latest).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1", StringComparison.Ordinal);
        File.WriteAllText(fixture.Latest, json);
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Null(read.Snapshot);
        Assert.Contains("snapshot-unreadable", read.Issues);
    }

    [Fact]
    public void CorruptSequenceIsReportedWithoutDiscardingTheFirstFact()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Snapshot(5);
        fixture.Store.Save(snapshot, [fixture.Milestone(snapshot)]);
        var original = File.ReadAllText(fixture.Journal);
        var conflicting = JsonNode.Parse(original)!.AsObject();
        conflicting["Recovery"]!["Raw"]!["Achievements"]!["101"]!["Count"] = 99;
        File.AppendAllText(fixture.Journal, conflicting.ToJsonString() + "\n");
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Single(read.Milestones);
        Assert.Contains("journal-sequence-conflict", read.Issues);
        Assert.Equal(5, read.Snapshot!.Raw.Achievements[101].Count);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("not-a-guid")]
    [InlineData("1234567812341234123412341234567A")]
    public void NonOpaqueStorageIdsAreRejected(string invalid)
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => fixture.Store.Load(fixture.Binding with { ProfileId = invalid }));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Load(fixture.Binding with { Revision = invalid }));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public void ProfileDeletionRejectsDelayedWritesAcrossStoreInstances()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Snapshot(5);
        fixture.Store.Save(snapshot, [fixture.Milestone(snapshot)]);
        fixture.Store.DeleteProfile(fixture.Binding.ProfileId);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, fixture.Binding.ProfileId)));
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Save(snapshot, []));
        var restarted = new AchievementStateStore(fixture.Root);
        Assert.Throws<InvalidOperationException>(() => restarted.Save(snapshot, []));
        var read = restarted.Load(fixture.Binding);
        Assert.Null(read.Snapshot);
        Assert.Contains("profile-deleted", read.Issues);
    }

    [Fact]
    public void NewNativeAssignmentUsesAnIsolatedHistory()
    {
        using var fixture = new Fixture();
        fixture.Store.Save(fixture.Snapshot(5), []);
        var replacement = fixture.Binding with { Revision = Guid.NewGuid().ToString("N") };
        Assert.Null(fixture.Store.Load(replacement).Snapshot);
        fixture.Store.Save(fixture.Snapshot(9) with { Binding = replacement }, []);
        Assert.Equal(5, fixture.Store.Load(fixture.Binding).Snapshot!.Raw.Achievements[101].Count);
        Assert.Equal(9, fixture.Store.Load(replacement).Snapshot!.Raw.Achievements[101].Count);
    }

    [Fact]
    public void OlderQueuedObservationCannotReplaceNewerCounters()
    {
        using var fixture = new Fixture();
        fixture.Store.Save(fixture.Snapshot(9) with { SourceSequence = 5 }, []);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(fixture.Snapshot(1) with { SourceSequence = 4 }, []));
        Assert.Equal(9, fixture.Store.Load(fixture.Binding).Snapshot!.Raw.Achievements[101].Count);
    }

    private static void AppendDurably(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.Flush(flushToDisk: true);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-achievement-history-" + Guid.NewGuid().ToString("N"));
        public AchievementProfileBinding Binding { get; } = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        public AchievementStateStore Store { get; }
        public string Latest => Path.Combine(Root, Binding.ProfileId, Binding.Revision, "latest-v1.json");
        public string Journal => Path.Combine(Root, Binding.ProfileId, Binding.Revision, "milestones-v1.jsonl");
        public Fixture() => Store = new(Root);
        public AchievementSavedSnapshot Snapshot(int count) => new()
        {
            Binding = Binding,
            Raw = new(ImmutableDictionary<int, CharacterAchievement>.Empty.Add(101, new(count, -1, 0)),
                ImmutableDictionary<int, ImmutableArray<bool>>.Empty),
            ObservedAt = At,
            MetadataBuild = 12345,
            RuleVersion = "test-v1",
            Generation = 1,
            SourceSequence = 1,
        };
        public AchievementMilestone Milestone(AchievementSavedSnapshot snapshot) => new("test-completed", AchievementMilestoneKind.Completed,
            101, 101, snapshot.ObservedAt, null, "Baseline", snapshot.MetadataBuild, snapshot.RuleVersion, "permanent", snapshot.Raw);
        public void Dispose()
        {
            var actual = Path.GetFullPath(Root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), actual, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(actual)) Directory.Delete(actual, recursive: true);
        }
    }
}
