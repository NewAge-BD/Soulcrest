using System.Security.Cryptography;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatHistoryReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-history-reader-" + Guid.NewGuid().ToString("N"));
    private CombatHistoryStore Store => new(_directory);
    private CombatHistoryReader Reader => new(Store);

    [Fact]
    public async Task EmptyBrowserDoesNotCreateFakeRunsOrAnIndex()
    {
        var overview = await Reader.LoadOverviewAsync();
        Assert.Empty(overview.Index.Runs);
        Assert.Equal(0, overview.UnpublishedRunCount);
        Assert.False(File.Exists(Path.Combine(_directory, "index.json")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "runs")));
    }

    [Fact]
    public async Task MissingIndexIsDerivedWithoutWritingAndHistoricalDetailsRemainUnchanged()
    {
        var run = DpsHistoryFixture.Run();
        Store.SaveRun(run);
        File.Delete(Path.Combine(_directory, "index.json"));
        var before = Hashes();
        var overview = await Reader.LoadOverviewAsync();
        var loaded = await Reader.LoadRunAsync(run.Id);
        Assert.Equal(run.Id, Assert.Single(overview.Index.Runs).Id);
        Assert.False(File.Exists(Path.Combine(_directory, "index.json")));
        Assert.Equal(before, Hashes());
        var attempt = Assert.Single(loaded.Attempts);
        Assert.Equal(4, attempt.Players.Count); // Former members and their replacements are separate people.
        Assert.Equal(123, attempt.Players.Single(p => p.Participant.ParticipantId == "member-a").Damage);
        Assert.Equal(456, attempt.Players.Single(p => p.Participant.ParticipantId == "member-c").Damage);
        Assert.Null(attempt.Dps);
        Assert.Null(attempt.Players[0].Skills[0].Casts);
        Assert.Null(Assert.Single(attempt.HpPoints).Maximum);
    }

    [Fact]
    public async Task UnsupportedIndexIsAnExplicitFailureAndIsNeverOverwritten()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "index.json");
        const string invalid = "{\"SchemaVersion\":99,\"Runs\":[]}";
        File.WriteAllText(path, invalid);
        var error = await Assert.ThrowsAsync<CombatHistoryReadException>(() => Reader.LoadOverviewAsync());
        Assert.Equal(CombatHistoryReadFailure.InvalidData, error.Failure);
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    [Fact]
    public async Task IncompleteJournalIsKeptAndItsWarningReachesTheProjection()
    {
        var run = DpsHistoryFixture.Run();
        Store.SaveRun(run);
        var journal = Path.Combine(_directory, "runs", run.Id.ToString("N"), "journals", run.Attempts[0].Id.ToString("N") + ".jsonl");
        File.AppendAllText(journal, "{\"Sequence\":999");
        var before = File.ReadAllBytes(journal);
        var loaded = await Reader.LoadRunAsync(run.Id);
        Assert.True(loaded.Run.Attempts[0].HasIncompleteJournal);
        Assert.Contains(loaded.Attempts[0].QualityIssues, issue => issue.Code == "incomplete-journal");
        Assert.False(loaded.Attempts[0].IsComplete);
        Assert.Equal(before, File.ReadAllBytes(journal));
    }

    [Fact]
    public async Task AnActiveWriterInAnotherStorePreventsAnInconsistentBrowserRead()
    {
        var run = DpsHistoryFixture.Run();
        Store.SaveRun(run);
        using var writer = new FileStream(Path.Combine(_directory, "writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var overviewError = await Assert.ThrowsAsync<CombatHistoryReadException>(() => Reader.LoadOverviewAsync());
        var runError = await Assert.ThrowsAsync<CombatHistoryReadException>(() => Reader.LoadRunAsync(run.Id));
        Assert.Equal(CombatHistoryReadFailure.Io, overviewError.Failure);
        Assert.Equal(CombatHistoryReadFailure.Io, runError.Failure);
    }

    [Fact]
    public async Task UnpublishedSavesAreReportedWithoutPublishingOrDiscardingThem()
    {
        var staged = Path.Combine(_directory, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        var overview = await Reader.LoadOverviewAsync();
        Assert.Empty(overview.Index.Runs);
        Assert.Equal(1, overview.UnpublishedRunCount);
        Assert.True(Directory.Exists(staged));
        Assert.False(File.Exists(Path.Combine(_directory, "index.json")));
    }

    [Fact]
    public async Task CancellationBeforeLoadingDoesNotTouchTheDataDirectory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader.LoadOverviewAsync(cancellation.Token));
        Assert.False(Directory.Exists(_directory));
    }

    private string[] Hashes() => Directory.GetFiles(_directory, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "writer.lock").Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(_directory, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}

internal static class DpsHistoryFixture
{
    internal static CombatRunDocument Run()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var source = new CombatSourceContext("fixture-stream", 1);
        var boss = new CombatEntityReference("fixture-boss", 1, source.StreamId);
        CombatParticipant[] initial = [new("member-a", "Member A", IsSelf: true), new("member-b", "Member B")];
        CombatParticipant[] replacements = [new("member-c", "Member C"), new("member-d", "Member D")];
        return new()
        {
            Id = Guid.NewGuid(), DungeonKey = "600001", StartedAtUtc = start, EndedAtUtc = start.AddSeconds(11),
            Attempts = [new()
            {
                Id = Guid.NewGuid(), BossKey = "npc:2300104", BossTargets = [boss], StartedAtUtc = start,
                DurationTicks = TimeSpan.FromSeconds(10).Ticks, EndReason = CombatEndReason.Victory,
                Capabilities = CombatCapabilities.None, Participants = initial,
                Events =
                [
                    Envelope(1, 1, new CombatDamageEvent("member-a", boss, "fixture-skill-a", 123)),
                    Envelope(2, 2, new CombatBuffEvent("fixture-buff", CombatBuffAction.Apply, "member-a", SourceParticipantId: "member-b")),
                    Envelope(3, 3, new CombatRosterEvent(replacements)),
                    Envelope(4, 4, new CombatDamageEvent("member-c", boss, "fixture-skill-c", 456)),
                    Envelope(5, 5, new CombatHpEvent(boss, 5000))
                ]
            }]
        };

        CombatEventEnvelope Envelope(long sequence, int seconds, CombatEvent item) =>
            new(sequence, TimeSpan.FromSeconds(seconds).Ticks, start.AddSeconds(seconds), source, item);
    }
}
