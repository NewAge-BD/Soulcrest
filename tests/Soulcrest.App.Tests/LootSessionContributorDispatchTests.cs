using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Fact]
    public async Task LootSessionDiagnosticDispatchProjectsTheExistingRecorderFaultWithoutConstructingLazyServices()
    {
        await using var f = new Fixture();
        await f.Ready();
        var registry = new DiagnosticServiceRegistry(f.Service, f.Settings);
        var lazyResolutions = 0;
        var services = new ServiceCollection().AddSingleton(registry)
            .AddSingleton<UnifiedLootTrackerService>(_ =>
            {
                lazyResolutions++;
                throw new InvalidOperationException("Diagnosis must use the existing recorder.");
            })
            .AddSingleton<NetworkLootService>(_ =>
            {
                lazyResolutions++;
                throw new InvalidOperationException("Diagnosis must not construct a network source.");
            });
        registry.ObserveSingletons(services);
        await using var provider = services.BuildServiceProvider();
        var contributor = Assert.Single(DiagnosticContributors.Create(provider.GetRequiredService<DiagnosticServiceRegistry>()),
            item => item.FeatureIds.Contains("loot-sessions"));
        var before = contributor.Collect("loot-sessions");
        Assert.Equal(false, before.Facts["recordingFaultPresent"]);
        Assert.Null(before.Facts["recordingFaultStage"]);
        Assert.Equal(0, lazyResolutions);

        f.FailAppend = true;
        f.Source.Harvest(1, new NetworkGatheringItem(610620013, 1));
        await f.Ready();
        var fault = Assert.IsType<LootRecordingFaultSnapshot>(f.Service.RecordingFault);
        var snapshot = f.Service.Snapshot;
        var journalCount = f.Journal.Count;
        var result = contributor.Collect("loot-sessions");

        Assert.Equal("loot-sessions", result.FeatureId);
        Assert.Equal(DiagnosticStatus.Error, result.Status);
        Assert.Equal(true, result.Facts["recordingFaultPresent"]);
        Assert.Equal(fault.At.ToUniversalTime(), result.Facts["recordingFaultAtUtc"]);
        Assert.Equal("JournalAppend", result.Facts["recordingFaultStage"]);
        Assert.Equal("Io", result.Facts["recordingFaultKind"]);
        Assert.Equal(nameof(IOException), result.Facts["recordingFaultExceptionType"]);
        Assert.Equal(fault.HResult, result.Facts["recordingFaultHResult"]);
        Assert.Equal(0, lazyResolutions);
        Assert.Same(fault, f.Service.RecordingFault);
        Assert.Same(snapshot, f.Service.Snapshot);
        Assert.Equal(journalCount, f.Journal.Count);
        var json = JsonSerializer.Serialize(result);
        foreach (var privateValue in new[] { f.Root, "Synthetic character", "Synthetic append failure", "ProfileId", "SessionId" })
            Assert.DoesNotContain(privateValue, json);
        Assert.All(result.Facts.Values, value => Assert.True(value is null or bool or int or long or string or DateTimeOffset));
    }
}
