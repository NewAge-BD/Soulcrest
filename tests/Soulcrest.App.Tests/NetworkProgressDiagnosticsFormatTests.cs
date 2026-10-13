using System.Globalization;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkProgressDiagnosticsFormatTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 2, 55, 34, TimeSpan.FromHours(2));
    private static NetworkProgressDiagnostics Snapshot => new(true, 2, 3, "stream-gap", At, 4, 0x8D09, At,
        5, 7, At, 6, 120, false);

    [Fact]
    public void FormatsCountersOpcodesAndUtcTimesIndependentlyOfTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var text = DiagnosticsService.FormatNetworkProgressDiagnostics(Snapshot);
            Assert.Contains("IdentityBound: true; IdentityBindings: 2", text);
            Assert.Contains("ContextResets: 3; LastResetReason: stream-gap", text);
            Assert.Contains("RejectedProgressMessages: 4; LastRejectedOpcode: 0x8D09", text);
            Assert.Contains("AcceptedSoulMessages: 5; AcceptedSoulPoints: 7", text);
            Assert.Contains("PendingMessages: 6; PendingBytes: 120; PendingOverflow: false", text);
            Assert.Contains("LastResetAtUtc: 2026-10-13T00:55:34.0000000+00:00", text);
            Assert.Contains("LastRejectedAtUtc: 2026-10-13T00:55:34.0000000+00:00", text);
            Assert.Contains("LastSoulAtUtc: 2026-10-13T00:55:34.0000000+00:00", text);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("manual")]
    [InlineData("capture-closed")]
    [InlineData("capture-stopped")]
    [InlineData("stream-gap")]
    [InlineData("world-entry-preparation")]
    [InlineData("world-entry")]
    [InlineData("unsupported-own-entry")]
    [InlineData("identity-bind-rejected")]
    public void OnlyFixedResetReasonsAreIncluded(string reason)
    {
        var text = DiagnosticsService.FormatNetworkProgressDiagnostics(Snapshot with { LastResetReason = reason });
        Assert.Contains("LastResetReason: " + reason + ";", text);
    }

    [Fact]
    public void UnknownResetReasonsCannotExportArbitraryText()
    {
        const string privateText = "Synthetic private character 9999999";
        var text = DiagnosticsService.FormatNetworkProgressDiagnostics(Snapshot with { LastResetReason = privateText });
        Assert.Contains("LastResetReason: unknown;", text);
        Assert.DoesNotContain(privateText, text);
    }

    [Fact]
    public void NullSnapshotAndMissingEventTimesRemainExplicit()
    {
        Assert.Equal("[Anonyme Netzwerkdiagnose]\nNicht verfügbar.\n", DiagnosticsService.FormatNetworkProgressDiagnostics(null));
        var text = DiagnosticsService.FormatNetworkProgressDiagnostics(Snapshot with
        {
            LastResetAt = null, LastRejectedOpcode = null, LastRejectedAt = null, LastSoulAt = null,
            IdentityBound = false, PendingOverflow = true,
        });
        Assert.Contains("IdentityBound: false", text);
        Assert.Contains("LastResetAtUtc: none", text);
        Assert.Contains("LastRejectedOpcode: none", text);
        Assert.Contains("LastRejectedAtUtc: none", text);
        Assert.Contains("LastSoulAtUtc: none", text);
        Assert.Contains("PendingOverflow: true", text);
    }

    [Fact]
    public void ZeroCountersNeverClaimAnIdentityResetRejectionOrPickup()
    {
        var text = DiagnosticsService.FormatNetworkProgressDiagnostics(new(false, 0, 0, "none", null,
            0, null, null, 0, 0, null, 0, 0, false));
        Assert.Contains("IdentityBound: false; IdentityBindings: 0", text);
        Assert.Contains("ContextResets: 0; LastResetReason: none", text);
        Assert.Contains("RejectedProgressMessages: 0; LastRejectedOpcode: none", text);
        Assert.Contains("AcceptedSoulMessages: 0; AcceptedSoulPoints: 0", text);
        Assert.Contains("PendingMessages: 0; PendingBytes: 0; PendingOverflow: false", text);
    }

    [Fact]
    public void SnapshotHasNoFieldsForNamesProfileIdsEndpointsOrPacketContent()
    {
        var properties = typeof(NetworkProgressDiagnostics).GetProperties();
        Assert.Equal(14, properties.Length);
        Assert.Equal(nameof(NetworkProgressDiagnostics.LastResetReason), Assert.Single(properties, property => property.PropertyType == typeof(string)).Name);
        Assert.All(properties, property => Assert.Contains(property.PropertyType,
            new[] { typeof(bool), typeof(long), typeof(string), typeof(DateTimeOffset?), typeof(ushort?), typeof(int) }));
    }
}
