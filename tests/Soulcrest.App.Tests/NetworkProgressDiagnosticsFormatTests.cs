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
    [InlineData("pet-continuity-expired")]
    [InlineData("pet-continuity-unread")]
    [InlineData("world-entry-candidate")]
    [InlineData("entry-candidate-expired")]
    [InlineData("duplicate-entry-baseline")]
    [InlineData("unsupported-entry-baseline")]
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
        Assert.Equal(17, properties.Length);
        Assert.Equal(nameof(NetworkProgressDiagnostics.LastResetReason), Assert.Single(properties, property => property.PropertyType == typeof(string)).Name);
        Assert.All(properties, property => Assert.Contains(property.PropertyType,
            new[] { typeof(bool), typeof(long), typeof(string), typeof(DateTimeOffset?), typeof(ushort?), typeof(int),
                typeof(IReadOnlyList<NetworkProgressDiagnosticEvent>) }));
    }

    [Fact]
    public void RecentEventsIncludePublicPetNumbersAndOnlyFixedReasonsInUtc()
    {
        const string privateText = "Synthetic private character 9999999";
        var snapshot = Snapshot with
        {
            UnboundSoulMessages = 2, UnboundSoulPoints = 3,
            Events = [new(At, NetworkProgressEventKind.Acquired, 0x9009, 1135, 2),
                new(At.AddSeconds(1), NetworkProgressEventKind.Gained, 0x900D, 1163, Souls: 2),
                new(At.AddSeconds(2), NetworkProgressEventKind.Reset, ResetReason: privateText),
                new(At.AddSeconds(3), (NetworkProgressEventKind)999)],
        };
        var text = DiagnosticsService.FormatNetworkProgressDiagnostics(snapshot);
        Assert.Contains("UnboundSoulMessages: 2; UnboundSoulPoints: 3", text);
        Assert.Contains("[Letzte anonyme Netzwerkereignisse: 4]", text);
        Assert.Contains("2026-10-13T00:55:34.0000000+00:00 Kind:Acquired; Opcode:0x9009; Pet:1135; Level:2;", text);
        Assert.Contains("Kind:Gained; Opcode:0x900D; Pet:1163; Level:none; Souls:2;", text);
        Assert.Contains("Kind:Reset; Opcode:none; Pet:none; Level:none; Souls:none; Count:none; Reason:unknown", text);
        Assert.Contains("Kind:unknown;", text);
        Assert.DoesNotContain(privateText, text);
    }

    [Fact]
    public void ExportIsBoundedEvenIfAnExternalSnapshotContainsTooManyEvents()
    {
        var snapshot = Snapshot with { Events = Enumerable.Range(0, 200)
            .Select(i => new NetworkProgressDiagnosticEvent(At.AddSeconds(i), NetworkProgressEventKind.Gained,
                0x900D, 1135, Souls: 1)).ToArray() };
        var text = DiagnosticsService.FormatNetworkProgressDiagnostics(snapshot);
        Assert.Contains("[Letzte anonyme Netzwerkereignisse: 128]", text);
        Assert.Equal(128, text.Split("Kind:Gained;").Length - 1);
        Assert.DoesNotContain("2026-10-13T00:55:34.0000000+00:00 Kind:", text);
        Assert.Contains("2026-10-13T00:58:53.0000000+00:00 Kind:", text);
    }

    [Fact]
    public void EventSchemaHasNoIdentityEndpointPayloadOrArbitraryMessageFields()
    {
        var properties = typeof(NetworkProgressDiagnosticEvent).GetProperties();
        Assert.Equal(new[] { "At", "Kind", "Opcode", "PetNumber", "Level", "Souls", "ResetReason", "Count" },
            properties.Select(property => property.Name));
        Assert.Equal("ResetReason", Assert.Single(properties, property => property.PropertyType == typeof(string)).Name);
    }
}
