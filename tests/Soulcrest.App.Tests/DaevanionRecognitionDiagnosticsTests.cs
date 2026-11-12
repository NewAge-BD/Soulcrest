using System.Text.Json;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DaevanionRecognitionDiagnosticsTests
{
    private static readonly string[] RecognitionKeys =
    [
        "recognitionSearches", "recognitionPreferredSearches", "recognitionMatches", "recognitionMisses",
        "recognitionExpiredAfterSearch", "recognitionPath", "recognitionElapsedMs", "recognitionComputedScores",
        "recognitionReusedScores", "recognitionMaximumMs"
    ];

    [Fact]
    public void MissingObservationUsesUnknownValuesInsteadOfHealthyZeroCounters()
    {
        var facts = Project(null);
        Assert.Equal(RecognitionKeys, facts.Keys);
        Assert.All(facts.Values, Assert.Null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(facts));
        Assert.All(json.RootElement.EnumerateObject(), item => Assert.Equal(JsonValueKind.Null, item.Value.ValueKind));
    }

    [Fact]
    public void ExistingHealthKeepsKnownCountersWithoutInventingALastSearch()
    {
        var facts = Project(new(0, 0, 0, 0, 0, null, 0));
        Assert.Equal(0L, facts["recognitionSearches"]);
        Assert.Equal(0L, facts["recognitionPreferredSearches"]);
        Assert.Equal(0L, facts["recognitionMatches"]);
        Assert.Equal(0L, facts["recognitionMisses"]);
        Assert.Equal(0L, facts["recognitionExpiredAfterSearch"]);
        Assert.Equal(0d, facts["recognitionMaximumMs"]);
        Assert.Null(facts["recognitionPath"]);
        Assert.Null(facts["recognitionElapsedMs"]);
        Assert.Null(facts["recognitionComputedScores"]);
        Assert.Null(facts["recognitionReusedScores"]);
    }

    [Theory]
    [InlineData(DaevanionRecognitionPath.InvalidInput)]
    [InlineData(DaevanionRecognitionPath.GlobalSearch)]
    [InlineData(DaevanionRecognitionPath.PreferredRaster)]
    public void MeasuredWorkUsesFlatPrimitiveValuesAndARecognizedPath(DaevanionRecognitionPath path)
    {
        var last = new DaevanionRecognitionMetrics(path, 12.75, 412, 1657);
        var health = new DaevanionRecognitionHealth(17, 13, 14, 3, 2, last, 31.25);
        var facts = Project(health);
        Assert.Equal(17L, facts["recognitionSearches"]);
        Assert.Equal(13L, facts["recognitionPreferredSearches"]);
        Assert.Equal(14L, facts["recognitionMatches"]);
        Assert.Equal(3L, facts["recognitionMisses"]);
        Assert.Equal(2L, facts["recognitionExpiredAfterSearch"]);
        Assert.Equal(path.ToString(), facts["recognitionPath"]);
        Assert.Equal(12.75, facts["recognitionElapsedMs"]);
        Assert.Equal(412, facts["recognitionComputedScores"]);
        Assert.Equal(1657, facts["recognitionReusedScores"]);
        Assert.Equal(31.25, facts["recognitionMaximumMs"]);
        Assert.All(facts.Values, value => Assert.True(value is long or int or double or string));
        Assert.Same(last, health.Last);
    }

    [Fact]
    public void SerializedFactsCannotExportSnapshotsIdentitiesLocationsOrPixels()
    {
        var facts = Project(new(7, 5, 6, 1, 0,
            new(DaevanionRecognitionPath.PreferredRaster, 4.5, 228, 1011), 20));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(facts));
        Assert.Equal(RecognitionKeys, json.RootElement.EnumerateObject().Select(item => item.Name));
        Assert.All(json.RootElement.EnumerateObject(), item =>
            Assert.True(item.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String));
        var text = json.RootElement.GetRawText();
        foreach (var forbidden in new[] { "character", "server", "entity", "identity", "profile", "payload",
            "pixel", "screenshot", "bounds", "origin", "frameAt", "generation", "endpoint" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteTimingsRemainSerializableAndUnknown(double timing)
    {
        var facts = Project(new(1, 0, 0, 1, 1, new(DaevanionRecognitionPath.GlobalSearch, timing, 10, 2), timing));
        Assert.Null(facts["recognitionElapsedMs"]);
        Assert.Null(facts["recognitionMaximumMs"]);
        JsonSerializer.Serialize(facts);
    }

    [Fact]
    public void UnknownPathCannotIntroduceArbitraryNumericLabels()
    {
        var facts = Project(new(1, 0, 0, 1, 0, new((DaevanionRecognitionPath)123456, 2, 1, 0), 2));
        Assert.Equal("Unknown", facts["recognitionPath"]);
    }

    [Fact]
    public void AppendingRecognitionFactsPreservesExistingContributorFacts()
    {
        var facts = new List<(string, object?)> { ("existing", true) };
        DaevanionRecognitionDiagnosticFacts.Append(facts, null);
        Assert.Equal("existing", facts[0].Item1);
        Assert.Equal(true, facts[0].Item2);
        Assert.Equal(RecognitionKeys.Length + 1, facts.Count);
        Assert.Equal(facts.Count, facts.Select(item => item.Item1).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ActualOverlayContributorKeepsMissingRecognitionUnknownWithoutStartingSources()
    {
        var settings = new SettingsService();
        settings.Current.DaevanionOverlayEnabled = true;
        using var capture = new GameCaptureService();
        var services = new DiagnosticServiceRegistry(settings, capture);
        var before = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var contribution = Assert.Single(DiagnosticContributors.Create(services), item => item.FeatureIds.Contains("overlay-daevanion"));
        var result = contribution.Collect("overlay-daevanion");
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(false, result.Facts["boardObservationAvailable"]);
        foreach (var key in RecognitionKeys)
        {
            Assert.True(result.Facts.ContainsKey(key), "Missing recognition diagnostic fact: " + key);
            Assert.Null(result.Facts[key]);
        }
        Assert.Null(services.GetService(typeof(DaevanionService)));
        Assert.Equal(0, capture.Diagnostics.Grabs);
        Assert.Equal(before, File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null);
    }

    private static Dictionary<string, object?> Project(DaevanionRecognitionHealth? health)
    {
        var facts = new List<(string, object?)>();
        DaevanionRecognitionDiagnosticFacts.Append(facts, health);
        return facts.ToDictionary(item => item.Item1, item => item.Item2, StringComparer.Ordinal);
    }
}
