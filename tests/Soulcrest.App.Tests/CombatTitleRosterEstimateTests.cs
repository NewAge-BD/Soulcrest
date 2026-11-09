using System.Text.Json;
using Soulcrest.App.Components;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatTitleRosterEstimateTests
{
    private const string Groups = """
        [{"key":"alpha","baseIds":[100,101,102],"conditionalOwnedIds":[190,191]},
         {"key":"beta","baseIds":[200,201,102],"conditionalOwnedIds":[290,291]}]
        """;
    private static string Document(string groups = Groups) =>
        "{\"schema\":1,\"metadataBuild\":42,\"qualification\":\"Candidate\","
        + "\"establishesNativeAvailability\":false,\"groups\":" + groups + "}";
    private static CombatTitleRosterEstimate Fixture() => CombatTitleRosterEstimate.FromJson(Document());

    [Fact]
    public void OneCompleteUnambiguousOwnershipListProducesOnlyACandidateRoster()
    {
        var result = Fixture().Estimate([100, 190], ownershipComplete: true);

        Assert.Equal(CombatTitleRosterEstimateStatus.Estimated, result.Status);
        Assert.True(result.IsEstimated);
        Assert.Equal(CombatTitleRosterEstimateReason.CandidateReconstruction, result.Reason);
        Assert.Equal("alpha", result.GroupKey);
        Assert.Equal(4, result.TotalCount);
        Assert.True(result.RosterIds.SetEquals([100, 101, 102, 190]));
        Assert.True(result.MissingIds.SetEquals([101, 102]));
        Assert.DoesNotContain(191, result.RosterIds);
    }

    [Fact]
    public void AddingANormalOwnedTitleReducesMissingWithoutChangingTheRoster()
    {
        var estimator = Fixture();
        var before = estimator.Estimate([100, 190], true);
        var after = estimator.Estimate([100, 101, 190], true);

        Assert.True(after.RosterIds.SetEquals(before.RosterIds));
        Assert.True(after.MissingIds.SetEquals([102]));
    }

    [Fact]
    public void ConditionalTitlesEnterTheRosterOnlyWhenObservedAsOwned()
    {
        var estimator = Fixture();
        var before = estimator.Estimate([100, 190], true);
        var after = estimator.Estimate([100, 190, 191], true);

        Assert.Equal(before.TotalCount + 1, after.TotalCount);
        Assert.Contains(191, after.RosterIds);
        Assert.True(after.MissingIds.SetEquals(before.MissingIds));
        Assert.DoesNotContain(191, after.MissingIds);
    }

    [Fact]
    public void NativeIdsStayDistinctAndTheResultIsIndependentOfMutableOwnershipInput()
    {
        var owned = new List<int> { 100, 100, 190 };
        var result = Fixture().Estimate(owned, true);
        owned.Add(101);
        owned.Clear();

        Assert.Equal(4, result.TotalCount);
        Assert.True(result.MissingIds.SetEquals([101, 102]));
        Assert.Equal(4, result.RosterIds.Count);
    }

    [Fact]
    public void ACompleteCandidateRosterCanHaveNoMissingTitlesAndStillStaysEstimated()
    {
        var result = Fixture().Estimate([100, 101, 102], true);

        Assert.True(result.IsEstimated);
        Assert.Equal(3, result.TotalCount);
        Assert.Empty(result.MissingIds);
    }

    [Theory]
    [InlineData("unknown", CombatTitleRosterEstimateReason.UnknownOwnership)]
    [InlineData("partial", CombatTitleRosterEstimateReason.IncompleteOwnership)]
    [InlineData("empty", CombatTitleRosterEstimateReason.EmptyOwnership)]
    [InlineData("invalid", CombatTitleRosterEstimateReason.InvalidOwnership)]
    [InlineData("foreign", CombatTitleRosterEstimateReason.UnknownTitleId)]
    [InlineData("mixed", CombatTitleRosterEstimateReason.NoMatchingGroup)]
    [InlineData("ambiguous", CombatTitleRosterEstimateReason.AmbiguousGroup)]
    public void InsufficientOrContradictoryOwnershipCannotProduceNumericalMissingCounts(
        string variant, CombatTitleRosterEstimateReason expected)
    {
        int[]? owned = variant switch
        {
            "unknown" => null,
            "partial" => [100],
            "empty" => [],
            "invalid" => [100, 0],
            "foreign" => [100, 999],
            "mixed" => [100, 200],
            "ambiguous" => [102],
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var result = Fixture().Estimate(owned, variant != "partial");

        Assert.Equal(CombatTitleRosterEstimateStatus.Unavailable, result.Status);
        Assert.False(result.IsEstimated);
        Assert.Equal(expected, result.Reason);
        Assert.Null(result.TotalCount);
        Assert.Null(result.GroupKey);
        Assert.Empty(result.RosterIds);
        Assert.Empty(result.MissingIds);
    }

    [Theory]
    [InlineData("not-candidate")]
    [InlineData("grants-availability")]
    [InlineData("schema")]
    [InlineData("missing-build")]
    [InlineData("extra-field")]
    [InlineData("no-groups")]
    [InlineData("duplicate-key")]
    [InlineData("invalid-key")]
    [InlineData("null-group")]
    [InlineData("null-ids")]
    [InlineData("empty-base")]
    [InlineData("duplicate-id")]
    [InlineData("overlap")]
    [InlineData("zero-id")]
    [InlineData("negative-id")]
    public void MalformedOrOverclaimingMetadataIsRejected(string variant)
    {
        var json = Document();
        json = variant switch
        {
            "not-candidate" => json.Replace("\"Candidate\"", "\"Qualified\"", StringComparison.Ordinal),
            "grants-availability" => json.Replace("\"establishesNativeAvailability\":false", "\"establishesNativeAvailability\":true", StringComparison.Ordinal),
            "schema" => json.Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal),
            "missing-build" => json.Replace("\"metadataBuild\":42,", "", StringComparison.Ordinal),
            "extra-field" => json.Replace("\"schema\":1", "\"currentFaction\":\"alpha\",\"schema\":1", StringComparison.Ordinal),
            "no-groups" => Document("[]"),
            "duplicate-key" => json.Replace("\"beta\"", "\"alpha\"", StringComparison.Ordinal),
            "invalid-key" => json.Replace("\"alpha\"", "\"<alpha>\"", StringComparison.Ordinal),
            "null-group" => Document("[null]"),
            "null-ids" => json.Replace("[100,101,102]", "null", StringComparison.Ordinal),
            "empty-base" => json.Replace("[100,101,102]", "[]", StringComparison.Ordinal),
            "duplicate-id" => json.Replace("[100,101,102]", "[100,100,102]", StringComparison.Ordinal),
            "overlap" => json.Replace("[190,191]", "[100,191]", StringComparison.Ordinal),
            "zero-id" => json.Replace("[190,191]", "[0,191]", StringComparison.Ordinal),
            "negative-id" => json.Replace("[190,191]", "[-1,191]", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };

        Assert.Throws<InvalidDataException>(() => CombatTitleRosterEstimate.FromJson(json));
    }

    [Fact]
    public void EmbeddedCandidateIdsAllResolveInTheSameVersionedPresentationCatalog()
    {
        var catalog = CombatTitleDisplayCatalog.Default;
        Assert.Equal(catalog.MetadataBuild, CombatTitleRosterEstimate.Default.MetadataBuild);
        using var stream = typeof(CombatTitleRosterEstimate).Assembly
            .GetManifestResourceStream("Soulcrest.CombatTitleRosterEstimate.json");
        Assert.NotNull(stream);
        using var json = JsonDocument.Parse(stream!);
        var ids = json.RootElement.GetProperty("groups").EnumerateArray()
            .SelectMany(group => group.GetProperty("baseIds").EnumerateArray()
                .Concat(group.GetProperty("conditionalOwnedIds").EnumerateArray()))
            .Select(id => id.GetInt32()).Distinct().ToArray();

        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.NotNull(catalog.Find(id)));
    }
}
