using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssProgressStoreTests
{
    private const string ProfileA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ProfileB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset At = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnknownCacheDoesNotCreateStorageAndKeepsValuesIndependentPerCharacter()
    {
        using var f = new Fixture(); Assert.Null(f.Store.Read(ProfileA)); Assert.False(File.Exists(f.Path));
        f.Store.Save(new(ProfileA, new(100, At), new(1900000, At), null));
        f.Store.Save(new(ProfileB, null, new(500, At.AddDays(1)), new(12, At.AddDays(1))));
        var restarted = new AbyssProgressStore(f.Path);
        Assert.Equal(1900000, restarted.Read(ProfileA)!.FarmedAbyssPoints!.Value);
        Assert.Null(restarted.Read(ProfileA)!.RankPoints); Assert.Null(restarted.Read(ProfileB)!.AbyssPointBalance);
        Assert.Equal(12, restarted.Read(ProfileB)!.RankPoints!.Value);
    }

    [Fact]
    public void LowerAuthoritativeValuesReplaceOldValuesWithoutResettingOtherCharacters()
    {
        using var f = new Fixture();
        f.Store.Save(new(ProfileA, new(100, At), new(1_900_000, At), new(500, At)));
        f.Store.Save(new(ProfileB, null, new(200, At), null));
        f.Store.Save(new(ProfileA, new(10, At.AddHours(1)), new(50, At.AddHours(1)), new(0, At.AddHours(1))));
        Assert.Equal(50, f.Store.Read(ProfileA)!.FarmedAbyssPoints!.Value);
        Assert.Equal(200, f.Store.Read(ProfileB)!.FarmedAbyssPoints!.Value);
    }

    [Fact]
    public void IndependentRankAndApEvidenceTimesRoundTripInTheExistingSchema()
    {
        using var f = new Fixture();
        var expected = new SavedAbyssProgress(ProfileA, new(100, At.AddMinutes(1)),
            new(1000, At.AddMinutes(2)), new(953_177, At));
        f.Store.Save(expected);
        Assert.Equal(expected, new AbyssProgressStore(f.Path).Read(ProfileA));
        using var json = JsonDocument.Parse(File.ReadAllText(f.Path));
        Assert.Equal(1, json.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public void CorruptDuplicateAndUnknownFieldsAreRejectedRatherThanBecomingZero()
    {
        using var f = new Fixture(); f.Store.Save(new(ProfileA, null, new(1, At), null));
        var original = File.ReadAllText(f.Path);
        File.WriteAllText(f.Path, original.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 1, \"SchemaVersion\": 1", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => f.Store.Read(ProfileA));
        File.WriteAllText(f.Path, original.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 1, \"Unexpected\": 1", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => f.Store.Read(ProfileA));
    }

    [Fact]
    public void CacheRejectsNativeIdentityKeysNegativeValuesAndFailedReplacementKeepsOriginal()
    {
        using var f = new Fixture();
        Assert.Throws<ArgumentException>(() => f.Store.Read("12345"));
        Assert.Throws<InvalidDataException>(() => f.Store.Save(new(ProfileA, new(-1, At), null, null)));
        f.Store.Save(new(ProfileA, null, new(5, At), null));
        var previous = File.ReadAllBytes(f.Path);
        var failing = new AbyssProgressStore(f.Path, (_, _) => throw new IOException("Synthetic replacement failure"));
        Assert.Throws<IOException>(() => failing.Save(new(ProfileA, null, new(10, At), null)));
        Assert.Equal(previous, File.ReadAllBytes(f.Path));
        Assert.Empty(Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(f.Path)!, "*.tmp"));
    }

    [Fact]
    public void CapChangesOnlyAtWednesdaySevenUtcAndSkippedWeeksAccumulate()
    {
        var reset = new DateTimeOffset(2026, 10, 28, 7, 0, 0, TimeSpan.Zero);
        Assert.Null(AbyssCapSchedule.CapAt(AbyssCapSchedule.Anchor.AddTicks(-1)));
        Assert.Equal(2_000_000, AbyssCapSchedule.CapAt(reset.AddTicks(-1)));
        Assert.Equal(2_500_000, AbyssCapSchedule.CapAt(reset));
        Assert.Equal(4_000_000, AbyssCapSchedule.CapAt(reset.AddDays(21)));
        Assert.Equal(reset, AbyssCapSchedule.NextReset(reset.AddTicks(-1)));
        Assert.Equal(reset.AddDays(7), AbyssCapSchedule.NextReset(reset));
        Assert.Equal(AbyssCapSchedule.CapAt(reset), AbyssCapSchedule.CapAt(reset.ToOffset(TimeSpan.FromHours(-4))));
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-abyss-progress-" + Guid.NewGuid().ToString("N"), "cache.json");
        public AbyssProgressStore Store { get; }
        public Fixture() => Store = new(Path);
        public void Dispose()
        {
            var root = System.IO.Path.GetDirectoryName(Path)!;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
