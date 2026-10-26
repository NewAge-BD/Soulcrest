using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DailyMonsterKinaStoreTests
{
    private const string ProfileA = "11111111111111111111111111111111";
    private const string ProfileB = "22222222222222222222222222222222";
    private static readonly DateTimeOffset At = new(2026, 10, 25, 23, 59, 0, TimeSpan.FromHours(1));

    [Fact]
    public void MissingFileIsUnknownAndReadDoesNotCreateStorage()
    {
        using var f = new Fixture();
        Assert.Null(f.Store.Read(ProfileA));
        Assert.False(File.Exists(f.Path));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(f.Path)));
    }

    [Fact]
    public void TwoProfilesSurviveRestartWithoutMixingTheirAbsoluteTotals()
    {
        using var f = new Fixture();
        f.Store.Save(new(ProfileA, 1234, At));
        f.Store.Save(new(ProfileB, 9876, At.AddMinutes(1)));
        var restarted = new DailyMonsterKinaStore(f.Path);
        Assert.Equal(new(ProfileA, 1234, At), restarted.Read(ProfileA));
        Assert.Equal(new(ProfileB, 9876, At.AddMinutes(1)), restarted.Read(ProfileB));
        Assert.Null(restarted.Read("33333333333333333333333333333333"));
    }

    [Fact]
    public void LowerAuthoritativeObservationReplacesThePreviousHigherTotal()
    {
        using var f = new Fixture();
        f.Store.Save(new(ProfileA, 900000, At));
        f.Store.Save(new(ProfileA, 25, At.AddMinutes(2)));
        Assert.Equal(25L, new DailyMonsterKinaStore(f.Path).Read(ProfileA)!.Total);
        Assert.Equal(At.AddMinutes(2), f.Store.Read(ProfileA)!.ObservedAt);
    }

    [Fact]
    public void CacheIsNotLocallyResetAtMidnightOrOnRestart()
    {
        using var f = new Fixture();
        f.Store.Save(new(ProfileA, 1234, At));
        var value = new DailyMonsterKinaStore(f.Path).Read(ProfileA);
        Assert.Equal(1234L, value!.Total);
        Assert.Equal(At, value.ObservedAt);
    }

    [Fact]
    public void ExplicitZeroAfterServerResetIsStoredRatherThanTreatedAsMissing()
    {
        using var f = new Fixture(); f.Store.Save(new(ProfileA, 100, At));
        f.Store.Save(new(ProfileA, 0, At.AddHours(1)));
        Assert.Equal(0L, f.Store.Read(ProfileA)!.Total);
        Assert.Equal(1_000_000L, f.Store.Read(ProfileA)!.Limit);
    }

    [Fact]
    public void GuidRepresentationsResolveToOneCanonicalLocalProfile()
    {
        using var f = new Fixture();
        var formatted = Guid.Parse(ProfileA).ToString("D").ToUpperInvariant();
        f.Store.Save(new(formatted, 100, At));
        Assert.Equal(ProfileA, f.Store.Read(formatted)!.ProfileId);
        Assert.Equal(ProfileA, f.Store.Read(ProfileA)!.ProfileId);
        Assert.Single(JsonDocument.Parse(File.ReadAllText(f.Path)).RootElement.GetProperty("Characters").EnumerateObject());
    }

    [Theory]
    [InlineData("")]
    [InlineData("synthetic-native-id")]
    [InlineData("../profile")]
    [InlineData("00000000000000000000000000000000")]
    public void InvalidLocalGuidIsRejectedBeforeCreatingOrReadingStorage(string profileId)
    {
        using var f = new Fixture();
        Assert.Throws<ArgumentException>(() => f.Store.Read(profileId));
        Assert.Throws<ArgumentException>(() => f.Store.Save(new(profileId, 100, At)));
        Assert.False(File.Exists(f.Path));
    }

    [Fact]
    public void NegativeTotalAndNonpositiveLimitCannotReplaceAnExistingRecord()
    {
        using var f = new Fixture(); f.Store.Save(new(ProfileA, 100, At));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Store.Save(new(ProfileA, -1, At)));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Store.Save(new(ProfileA, 100, At, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Store.Save(new(ProfileA, 100, At, -1)));
        Assert.Equal(100L, f.Store.Read(ProfileA)!.Total);
    }

    [Fact]
    public void ObservedAbsoluteValueIsNotClampedToALocallyAssumedLimit()
    {
        using var f = new Fixture();
        f.Store.Save(new(ProfileA, 1_000_001, At));
        Assert.Equal(1_000_001L, f.Store.Read(ProfileA)!.Total);
    }

    [Fact]
    public void FailedAtomicReplacementLeavesTheOldDocumentAndRecordUntouched()
    {
        using var f = new Fixture(); f.Store.Save(new(ProfileA, 100, At));
        var before = File.ReadAllBytes(f.Path);
        var failing = new DailyMonsterKinaStore(f.Path, (_, _) => throw new IOException("Synthetic commit failure"));
        Assert.Throws<IOException>(() => failing.Save(new(ProfileA, 200, At.AddMinutes(1))));
        Assert.Equal(before, File.ReadAllBytes(f.Path));
        Assert.Equal(100L, failing.Read(ProfileA)!.Total);
        Assert.Equal(100L, new DailyMonsterKinaStore(f.Path).Read(ProfileA)!.Total);
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"SchemaVersion\":2,\"Characters\":{}}")]
    [InlineData("{\"SchemaVersion\":1,\"SchemaVersion\":1,\"Characters\":{}}")]
    [InlineData("{\"SchemaVersion\":1,\"Characters\":{},\"CharacterId\":7}")]
    public void InvalidOrUnknownDocumentFailsClosedWithoutRewritingIt(string json)
    {
        using var f = new Fixture(); f.Write(json);
        Assert.Throws<InvalidDataException>(() => f.Store.Read(ProfileA));
        Assert.Throws<InvalidDataException>(() => f.Store.Save(new(ProfileA, 100, At)));
        Assert.Equal(json, File.ReadAllText(f.Path));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("1.5")]
    [InlineData("\"100\"")]
    public void NegativeOverflowFractionAndStringFileTotalsAreRejected(string encodedTotal)
    {
        using var f = new Fixture();
        f.Write(Document().Replace("\"Total\":100", "\"Total\":" + encodedTotal, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => f.Store.Read(ProfileA));
    }

    [Theory]
    [InlineData("\"Limit\":1000000", "\"Limit\":0")]
    [InlineData("\"Limit\":1000000", "\"Limit\":9223372036854775808")]
    [InlineData("\"ObservedAt\":\"2026-10-25T23:59:00+01:00\"", "\"ObservedAt\":\"invalid\"")]
    [InlineData("\"Total\":100", "\"Total\":100,\"Total\":200")]
    public void InvalidLimitTimeAndDuplicateRecordFieldsAreRejected(string before, string after)
    {
        using var f = new Fixture();
        var json = Document(); Assert.Contains(before, json);
        f.Write(json.Replace(before, after, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => f.Store.Read(ProfileA));
    }

    [Fact]
    public void InvalidGuidKeyOrMismatchedRecordCannotLeakAnotherProfilesData()
    {
        using var f = new Fixture();
        f.Write(Document(key: "synthetic-network-character"));
        Assert.Throws<InvalidDataException>(() => f.Store.Read(ProfileA));
        f.Write(Document(recordProfile: ProfileB));
        Assert.Throws<InvalidDataException>(() => f.Store.Read(ProfileA));
    }

    [Fact]
    public void DuplicateEquivalentGuidKeysAreRejectedInsteadOfPickingTheLastRecord()
    {
        using var f = new Fixture();
        var record = JsonSerializer.Serialize(new SavedDailyMonsterKina(ProfileA, 100, At));
        f.Write("{\"SchemaVersion\":1,\"Characters\":{\"" + ProfileA + "\":" + record + ",\"" + Guid.Parse(ProfileA).ToString("D") + "\":" + record + "}}");
        Assert.Throws<InvalidDataException>(() => f.Store.Read(ProfileA));
    }

    [Fact]
    public void OversizedCacheIsRejectedBeforeDeserializingOrReplacingIt()
    {
        using var f = new Fixture(); f.Write(new string(' ', 1024 * 1024 + 1));
        Assert.Throws<InvalidDataException>(() => f.Store.Read(ProfileA));
        Assert.Throws<InvalidDataException>(() => f.Store.Save(new(ProfileA, 100, At)));
        Assert.Equal(1024 * 1024 + 1, new FileInfo(f.Path).Length);
    }

    [Fact]
    public void PersistenceDoesNotModifyPetProgressOrPetEventFiles()
    {
        using var f = new Fixture();
        var petProfile = System.IO.Path.Combine(f.Root, "progress.json");
        var petEvents = System.IO.Path.Combine(f.Root, "events.jsonl");
        File.WriteAllText(petProfile, "synthetic pet profile\n");
        File.WriteAllText(petEvents, "synthetic pet events\n");
        var profileBytes = File.ReadAllBytes(petProfile); var eventBytes = File.ReadAllBytes(petEvents);
        f.Store.Save(new(ProfileA, 100, At)); f.Store.Read(ProfileA);
        Assert.Equal(profileBytes, File.ReadAllBytes(petProfile));
        Assert.Equal(eventBytes, File.ReadAllBytes(petEvents));
    }

    [Fact]
    public void SerializedDocumentContainsLocalProfileKeysAndNoNativeIdentityOrLiveConfirmation()
    {
        using var f = new Fixture(); f.Store.Save(new(ProfileA, 100, At));
        var json = File.ReadAllText(f.Path);
        Assert.Contains(ProfileA, json);
        Assert.DoesNotContain("CharacterId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServerId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EntityKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsLive", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Confirmed", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UncPathsAreRejectedWithoutOpeningANetworkShare()
    {
        Assert.Throws<InvalidDataException>(() => new DailyMonsterKinaStore(@"\\synthetic-host\synthetic-share\daily.json"));
    }

    private static string Document(string key = ProfileA, string recordProfile = ProfileA) => JsonSerializer.Serialize(new
    {
        SchemaVersion = 1,
        Characters = new Dictionary<string, SavedDailyMonsterKina> { [key] = new(recordProfile, 100, At) }
    });

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-daily-kina-" + Guid.NewGuid().ToString("N"));
        internal string Path { get; }
        internal DailyMonsterKinaStore Store { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Path = System.IO.Path.Combine(Root, "loot-tracker", "daily-monster-kina.json");
            Store = new(Path);
        }
        internal void Write(string json) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!); File.WriteAllText(Path, json); }
        public void Dispose() { Directory.Delete(Root, recursive: true); }
    }
}
