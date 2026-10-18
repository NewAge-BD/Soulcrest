using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatHistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
    private static readonly CombatSourceContext Source = new("local-stream-a", 3);
    private static readonly CombatEntityReference Boss = new("boss-entity", 3, "local-stream-a");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-combat-" + Guid.NewGuid().ToString("N"));
    private CombatHistoryStore Store => new(_directory);

    [Fact]
    public void RunRoundtripRetainsAllTypedTimelinesParticipantsAndMeasurementProfile()
    {
        var run = CreateRun();
        var attempt = run.Attempts[0];
        CombatEvent[] events =
        [
            new CombatRosterEvent([new("member-a", "Player A", "class-a", 12345), new("member-b", "Player B")]),
            new CombatCastEvent("member-a", "skill-a"),
            new CombatDamageEvent("member-a", Boss, "skill-a", 123, new(Critical: true, Front: false)),
            new CombatHealEvent("member-b", "member-a", "heal-a", 50),
            new CombatBuffEvent("buff-a", CombatBuffAction.Apply, "member-a", SourceParticipantId: "member-b", StackCount: 2, Level: 3),
            new CombatBuffEvent("debuff-a", CombatBuffAction.Apply, TargetEntity: Boss),
            new CombatHpEvent(Boss, 500, 1000),
            new CombatDeathEvent("member-a"),
            new CombatGapEvent(CombatGapReason.Transport),
            new CombatRosterEvent([new("member-b", "Player B"), new("member-c", "Player C")])
        ];
        run = run with { Attempts = [attempt with { Events = events.Select((e, i) => Envelope(i + 1, e, i)).ToArray() }] };

        Store.SaveRun(run);
        var loaded = new CombatHistoryStore(_directory).LoadRun(run.Id);

        Assert.False(loaded.HasIncompleteJournal);
        Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(loaded.Run));
        Assert.Equal(10, File.ReadAllLines(Journal(run)).Length);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Metadata(run)));
        Assert.Empty(metadata.RootElement.GetProperty("Attempts")[0].GetProperty("Events").EnumerateArray());
        Assert.DoesNotContain("Player A", File.ReadAllText(Path.Combine(_directory, "index.json")));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void SameSequenceIsIdempotentButIdenticalDamageAtAnotherSequenceIsRetained()
    {
        var first = Envelope(1, new CombatDamageEvent("member-a", Boss, "skill-a", 50));
        var initial = CreateRun(events: [first]);
        var run = initial with { EndedAtUtc = null, Attempts = [initial.Attempts[0] with { EndReason = null, DurationTicks = null }] };
        Store.SaveRun(run);

        Store.AppendEvents(run.Id, run.Attempts[0].Id, [first, first with { Sequence = 2 }]);
        Store.SaveRun(run); // A repeated metadata checkpoint does not replace the longer journal.

        Assert.Equal([1L, 2L], Store.LoadRun(run.Id).Run.Attempts[0].Events.Select(e => e.Sequence));
    }

    [Fact]
    public void DivergentDuplicateRejectsTheWholeBatchBeforeAppend()
    {
        var first = Envelope(1, new CombatDamageEvent("member-a", Boss, "skill-a", 50));
        var run = CreateRun(events: [first]);
        Store.SaveRun(run);
        var before = File.ReadAllBytes(Journal(run));

        Assert.Throws<InvalidOperationException>(() => Store.AppendEvents(run.Id, run.Attempts[0].Id,
            [first with { Sequence = 2 }, first with { Event = new CombatDamageEvent("member-a", Boss, "skill-a", 51) }]));

        Assert.Equal(before, File.ReadAllBytes(Journal(run)));
    }

    [Fact]
    public void CaptureUtcMayRegressWhileSequenceAndMonotonicOffsetAdvance()
    {
        var first = Envelope(1, new CombatCastEvent("member-a", "skill-a"), 1);
        var initial = CreateRun(events: [first]);
        var run = initial with { EndedAtUtc = null, Attempts = [initial.Attempts[0] with { EndReason = null, DurationTicks = null }] };
        Store.SaveRun(run);
        Store.AppendEvents(run.Id, run.Attempts[0].Id,
            [first with { Sequence = 2, OffsetTicks = 2, CapturedAtUtc = Start.AddMinutes(-1) }]);
        Assert.Equal(2, Store.LoadJournal(run.Id, run.Attempts[0].Id).Events.Count);
        Assert.Throws<InvalidOperationException>(() => Store.AppendEvents(run.Id, run.Attempts[0].Id,
            [first with { Sequence = 3, OffsetTicks = 0 }]));
    }

    [Fact]
    public void IncompleteTailReturnsValidatedPrefixAndBlocksAppendWithoutChangingBytes()
    {
        var run = CreateRun(events: [Envelope(1, new CombatCastEvent("member-a", "skill-a"))]);
        Store.SaveRun(run);
        File.AppendAllText(Journal(run), "{\"SchemaVersion\":1,\"Sequence\":2,");
        var bytes = File.ReadAllBytes(Journal(run));

        var loaded = Store.LoadRun(run.Id);
        Assert.True(loaded.HasIncompleteJournal);
        Assert.Equal([run.Attempts[0].Id], loaded.IncompleteJournalAttempts);
        Assert.Single(loaded.Run.Attempts[0].Events);
        Assert.True(loaded.Run.Attempts[0].HasIncompleteJournal);
        Assert.False(CombatProjection.Project(loaded.Run.Attempts[0]).IsComplete);
        var comparison = CombatComparison.Compare(loaded.Run.Attempts[0], run.Attempts[0]);
        Assert.Contains("incomplete-data", comparison.ExclusionReasons);
        Assert.Throws<InvalidOperationException>(() => Store.AppendEvents(run.Id, run.Attempts[0].Id,
            [Envelope(2, new CombatCastEvent("member-a", "skill-b"))]));
        Assert.Equal(bytes, File.ReadAllBytes(Journal(run)));
    }

    [Fact]
    public void ACompleteJsonRecordWithoutCommitNewlineIsStillAnIncompleteTail()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        File.AppendAllText(Journal(run), JsonSerializer.Serialize(Envelope(1, new CombatCastEvent("member-a", "skill-a"))));
        var loaded = Store.LoadRun(run.Id);
        Assert.True(loaded.HasIncompleteJournal);
        Assert.Empty(loaded.Run.Attempts[0].Events);
    }

    [Fact]
    public void CorruptMiddleRecordIsAnExplicitFailureRatherThanPartialSuccess()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        File.WriteAllText(Journal(run), JsonSerializer.Serialize(Envelope(1, new CombatCastEvent("member-a", "skill-a"))) +
            "\n{broken}\n" + JsonSerializer.Serialize(Envelope(3, new CombatCastEvent("member-a", "skill-b"))) + "\n");
        Assert.Throws<CombatHistoryFormatException>(() => Store.LoadRun(run.Id));
    }

    [Fact]
    public void MissingPrimaryJournalIsNeverLoadedAsAnEmptyFight()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        File.Delete(Journal(run));
        Assert.Throws<CombatHistoryFormatException>(() => Store.LoadRun(run.Id));
        Assert.Throws<CombatHistoryFormatException>(() => Store.RebuildIndex());
    }

    [Theory]
    [InlineData("SchemaVersion", "2")]
    [InlineData("SchemaVersion", "null")]
    [InlineData("Attempts", "null")]
    public void UnsupportedOrCorruptRunIsPreservedAndReported(string key, string json)
    {
        var run = CreateRun();
        Store.SaveRun(run);
        var document = JsonNode.Parse(File.ReadAllText(Metadata(run)))!.AsObject();
        document[key] = JsonNode.Parse(json);
        File.WriteAllText(Metadata(run), document.ToJsonString());
        var before = File.ReadAllBytes(Metadata(run));

        Assert.Throws<CombatHistoryFormatException>(() => Store.LoadRun(run.Id));
        Assert.Equal(before, File.ReadAllBytes(Metadata(run)));
    }

    [Fact]
    public void MissingMeasurementRulesetIsRejectedInsteadOfInventingTheDefault()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        var document = JsonNode.Parse(File.ReadAllText(Metadata(run)))!;
        document["Attempts"]![0]!.AsObject().Remove("MeasurementProfile");
        File.WriteAllText(Metadata(run), document.ToJsonString());
        Assert.Throws<CombatHistoryFormatException>(() => Store.LoadRun(run.Id));
    }

    [Fact]
    public void UnsupportedEventVersionIsRejectedEvenInsideASupportedRun()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        File.WriteAllText(Journal(run), JsonSerializer.Serialize(
            Envelope(1, new CombatCastEvent("member-a", "skill-a")) with { SchemaVersion = 2 }) + "\n");
        Assert.Throws<CombatHistoryFormatException>(() => Store.LoadRun(run.Id));
    }

    [Fact]
    public void CorruptIndexRequiresExplicitRebuildAndPrimaryRecordsStayUnchanged()
    {
        var run = CreateRun(events: [Envelope(1, new CombatCastEvent("member-a", "skill-a"))]);
        Store.SaveRun(run);
        var before = File.ReadAllBytes(Journal(run));
        File.WriteAllText(Path.Combine(_directory, "index.json"), "{broken");

        Assert.Throws<CombatHistoryFormatException>(() => Store.LoadIndex());
        Assert.Single(Store.RebuildIndex().Runs);
        Assert.Equal(before, File.ReadAllBytes(Journal(run)));
    }

    [Fact]
    public void MissingIndexRebuildsButFutureIndexIsNeverOverwritten()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        var indexPath = Path.Combine(_directory, "index.json");
        File.Delete(indexPath);
        Assert.Equal(run.Id, Assert.Single(Store.LoadIndex().Runs).Id);
        File.WriteAllText(indexPath, "{\"SchemaVersion\":2,\"Runs\":[]}");
        var before = File.ReadAllBytes(indexPath);

        Assert.Throws<CombatHistoryFormatException>(() => Store.LoadIndex());
        Assert.Throws<CombatHistoryFormatException>(() => Store.RebuildIndex());
        Assert.Throws<CombatHistoryFormatException>(() => Store.SaveRun(run));
        Assert.Equal(before, File.ReadAllBytes(indexPath));
    }

    [Fact]
    public void FailedRebuildPreservesExistingIndexWhenPrimaryMetadataIsCorrupt()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        var indexPath = Path.Combine(_directory, "index.json");
        var before = File.ReadAllBytes(indexPath);
        File.WriteAllText(Metadata(run), "{broken");
        Assert.Throws<CombatHistoryFormatException>(() => Store.RebuildIndex());
        Assert.Equal(before, File.ReadAllBytes(indexPath));
    }

    [Fact]
    public void RetentionCountsRunsAndAlwaysProtectsFavoritesAndActiveRuns()
    {
        var favorite = CreateRun(start: Start.AddHours(-4)) with { Favorite = true };
        var active = CreateRun(start: Start.AddHours(-3));
        active = active with { EndedAtUtc = null, Attempts = [active.Attempts[0] with { DurationTicks = null, EndReason = null }] };
        var oldest = CreateRun(start: Start.AddHours(-2));
        var newest = CreateRun(start: Start.AddHours(-1));
        foreach (var run in new[] { favorite, active, oldest, newest }) Store.SaveRun(run);

        Assert.Equal([oldest.Id], Store.Prune(3));
        Assert.Equal([newest.Id], Store.Prune(0));
        Assert.Equal([active.Id, favorite.Id], Store.LoadIndex().Runs.Select(r => r.Id));
        Assert.Equal(500, CombatHistoryStore.DefaultRetainedRuns);
        Assert.Empty(Store.Prune(0));
    }

    [Fact]
    public void FavoriteChangeSurvivesReloadWithoutRewritingTheJournal()
    {
        var run = CreateRun(events: [Envelope(1, new CombatCastEvent("member-a", "skill-a"))]);
        Store.SaveRun(run);
        var before = File.ReadAllBytes(Journal(run));
        Store.SetFavorite(run.Id, true);
        Assert.True(Store.LoadRun(run.Id).Run.Favorite);
        Assert.True(Assert.Single(Store.LoadIndex().Runs).Favorite);
        Assert.Equal(before, File.ReadAllBytes(Journal(run)));
    }

    [Fact]
    public void DeleteCascadesThroughOwnedJournalsAndProjectionsOnly()
    {
        var run = CreateRun();
        var other = CreateRun();
        Store.SaveRun(run);
        Store.SaveRun(other);
        var projectionDirectory = Path.Combine(RunDirectory(run), "projections");
        Directory.CreateDirectory(projectionDirectory);
        File.WriteAllText(Path.Combine(projectionDirectory, "derived.json"), "{}");

        Assert.True(Store.DeleteRun(run.Id));
        Assert.False(Directory.Exists(RunDirectory(run)));
        Assert.Equal(other.Id, Assert.Single(Store.LoadIndex().Runs).Id);
        Assert.Equal(other.Id, Store.LoadRun(other.Id).Run.Id);
        Assert.False(Store.DeleteRun(run.Id));
        Assert.Equal(["index.json", "writer.lock"], Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void MetadataCheckpointCannotDropAHistoricalAttempt()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        Assert.Throws<InvalidOperationException>(() => Store.SaveRun(run with { Attempts = [] }));
        Assert.Single(Store.LoadRun(run.Id).Run.Attempts);
    }

    [Fact]
    public void CheckpointPreservesHistoricalRosterBossAndMeasurementIdentity()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        var attempt = run.Attempts[0];
        foreach (var changed in new[]
        {
            attempt with { BossKey = "different-boss" },
            attempt with { Participants = [new("member-c", "Player C")] },
            attempt with { BossTargets = [] },
            attempt with { BossTargets = [Boss, new("add-entity", 3, "local-stream-a")] },
            attempt with { Capabilities = CombatCapabilities.Damage },
            attempt with { MeasurementProfile = new("different-ruleset") },
            attempt with { DurationTicks = 5, EndReason = CombatEndReason.Wipe }
        })
            Assert.Throws<InvalidOperationException>(() => Store.SaveRun(run with { Attempts = [changed] }));
        Assert.Throws<InvalidOperationException>(() => Store.SaveRun(run with { DungeonKey = "different-dungeon" }));
        Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(Store.LoadRun(run.Id).Run));
    }

    [Fact]
    public void RunWithAnActiveAttemptCannotBeMarkedCompletedAndPruned()
    {
        var run = CreateRun();
        Assert.Throws<ArgumentException>(() => Store.SaveRun(run with
        {
            Attempts = [run.Attempts[0] with { DurationTicks = null, EndReason = null }]
        }));
    }

    [Fact]
    public void UnfinishedFirstSaveDoesNotBlockOtherRunsAndCanBeExplicitlyDeleted()
    {
        var incomplete = CreateRun();
        var staging = Path.Combine(_directory, "staging", incomplete.Id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "journals"));
        File.WriteAllText(Path.Combine(staging, "journals", incomplete.Attempts[0].Id.ToString("N") + ".jsonl"), "{partial");
        var other = CreateRun();
        Store.SaveRun(other);

        Assert.Equal([incomplete.Id], Store.GetStagedRunIds());
        Assert.Equal(other.Id, Assert.Single(Store.LoadIndex().Runs).Id);
        Assert.Throws<FileNotFoundException>(() => Store.RecoverStagedRun(incomplete.Id));
        Assert.True(Store.DeleteRun(incomplete.Id));
        Assert.Empty(Store.GetStagedRunIds());
        Assert.Equal(other.Id, Assert.Single(Store.LoadIndex().Runs).Id);
    }

    [Fact]
    public void FullyWrittenStagedRunCanBeExplicitlyRecoveredWithoutChangingItsJournal()
    {
        var run = CreateRun(events: [Envelope(1, new CombatCastEvent("member-a", "skill-a"))]);
        Store.SaveRun(run);
        var before = File.ReadAllBytes(Journal(run));
        var staging = Path.Combine(_directory, "staging", run.Id.ToString("N"));
        Directory.Move(RunDirectory(run), staging);
        Assert.Empty(Store.RebuildIndex().Runs);

        Assert.True(Store.RecoverStagedRun(run.Id));
        Assert.False(Store.RecoverStagedRun(run.Id));
        Assert.Empty(Store.GetStagedRunIds());
        Assert.Equal(run.Id, Assert.Single(Store.LoadIndex().Runs).Id);
        Assert.Equal(before, File.ReadAllBytes(Journal(run)));
        Assert.Single(Store.LoadRun(run.Id).Run.Attempts[0].Events);
    }

    [Fact]
    public void InvalidIdentityAndCrossStreamTargetAreRejectedBeforeAnyDiskWrite()
    {
        Assert.Throws<ArgumentException>(() => Store.SaveRun(CreateRun() with { Id = Guid.Empty }));
        var crossStream = new CombatDamageEvent("member-a", Boss with { StreamId = "other-stream" }, "skill-a", 1);
        Assert.Throws<ArgumentException>(() => Store.SaveRun(CreateRun(events: [Envelope(1, crossStream)])));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void EventsOutsideTheAttemptDurationAreRejected()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        Assert.Throws<CombatHistoryFormatException>(() => Store.AppendEvents(run.Id, run.Attempts[0].Id,
            [Envelope(1, new CombatCastEvent("member-a", "skill-a"), TimeSpan.FromSeconds(11).Ticks)]));
        Assert.Empty(Store.LoadRun(run.Id).Run.Attempts[0].Events);
    }

    [Fact]
    public void ConcurrentWriterLockPreventsAnInterleavedAppend()
    {
        var run = CreateRun();
        Store.SaveRun(run);
        using var writer = new FileStream(Path.Combine(_directory, "writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => Store.AppendEvents(run.Id, run.Attempts[0].Id,
            [Envelope(1, new CombatCastEvent("member-a", "skill-a"))]));
        Assert.Empty(File.ReadAllBytes(Journal(run)));
    }

    [Fact]
    public void AuxiliaryHitValuesRoundtripWithoutDeduplicatingRepeatedValuesOrChangingPrimaryTotals()
    {
        var values = new CombatObservedHitValues(ulong.MaxValue, 72, 0);
        var run = CreateRun(events:
        [
            Envelope(1, new CombatDamageEvent("member-a", Boss, "skill-a", 100) { ObservedValues = values }),
            Envelope(2, new CombatDamageEvent("member-a", Boss, "skill-a", 100) { ObservedValues = values }),
            Envelope(3, new CombatHealEvent("member-a", "member-b", "heal-a", 25)
                { ObservedValues = new(DirectFlag400Value: ulong.MaxValue) }),
            Envelope(4, new CombatUnclassifiedAmountEvent("member-a", null, Boss, null, "effect-a", null, true)
                { ObservedValues = new(PeriodicFlag1Value: ulong.MaxValue) })
        ]);
        Store.SaveRun(run);
        var loaded = Store.LoadRun(run.Id);
        Assert.False(loaded.HasIncompleteJournal);
        Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(loaded.Run));
        var summary = CombatProjection.Project(loaded.Run.Attempts[0]);
        Assert.Equal(200, summary.Damage);
        var player = summary.Players.Single(p => p.Participant.ParticipantId == "member-a");
        Assert.Equal(25, player.Healing);
        Assert.Equal(2.5m, player.Hps);
        Assert.Equal(2, player.Skills.Single(s => s.SkillId == "skill-a").Hits);
        Assert.False(summary.IsComplete);
        Assert.Equal(4, summary.Timeline.Count);
        Assert.Equal(4, summary.QualityIssues.Count(i => i.Code == "auxiliary-amount-semantics-unconfirmed"));
    }

    [Fact]
    public void JournalsWithoutOptionalAuxiliaryPropertyStillLoadAndRemainAppendIdempotent()
    {
        var item = Envelope(1, new CombatDamageEvent("member-a", Boss, "skill-a", 50));
        var run = CreateRun(events: [item]);
        Store.SaveRun(run);
        Assert.DoesNotContain("ObservedValues", File.ReadAllText(Journal(run)));
        Assert.Null(Assert.IsType<CombatDamageEvent>(Store.LoadRun(run.Id).Run.Attempts[0].Events[0].Event).ObservedValues);
        Store.AppendEvents(run.Id, run.Attempts[0].Id, [item]);
        Assert.Single(Store.LoadJournal(run.Id, run.Attempts[0].Id).Events);
    }

    private static CombatEventEnvelope Envelope(long sequence, CombatEvent value, long offsetTicks = 0)
        => new(sequence, offsetTicks, Start.AddTicks(offsetTicks), Source, value);

    private static CombatRunDocument CreateRun(CombatEventEnvelope[]? events = null, DateTimeOffset? start = null)
    {
        var time = start ?? Start;
        return new()
        {
            Id = Guid.NewGuid(), DungeonKey = "dungeon-a", InstanceKey = "instance-a", StartedAtUtc = time,
            EndedAtUtc = time.AddSeconds(10),
            Attempts = [new()
            {
                Id = Guid.NewGuid(), BossKey = "boss-a", EncounterVariantKey = "normal", BossTargets = [Boss], StartedAtUtc = time,
                DurationTicks = TimeSpan.FromSeconds(10).Ticks, EndReason = CombatEndReason.Victory,
                Capabilities = CombatCapabilities.Damage | CombatCapabilities.SkillCasts | CombatCapabilities.Healing |
                    CombatCapabilities.Deaths | CombatCapabilities.Buffs,
                MeasurementProfile = new("boss-damage-v1", "synthetic-build", "synthetic-decoder-v1", "synthetic-catalog-v1"),
                Participants = [new("member-a", "Player A", "class-a", 12345), new("member-b", "Player B")],
                Events = events ?? []
            }]
        };
    }

    private string RunDirectory(CombatRunDocument run) => Path.Combine(_directory, "runs", run.Id.ToString("N"));
    private string Metadata(CombatRunDocument run) => Path.Combine(RunDirectory(run), "run.json");
    private string Journal(CombatRunDocument run) => Path.Combine(RunDirectory(run), "journals", run.Attempts[0].Id.ToString("N") + ".jsonl");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
