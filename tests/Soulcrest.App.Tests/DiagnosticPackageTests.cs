using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DiagnosticPackageTests
{
    [Fact]
    public async Task ExportContainsOnlyTheSelectionAndRequiredSourcesWithoutCopyingPrivateFiles()
    {
        await using var fixture = new Fixture(new Contributor(id => Result(id)));
        var log = Path.Combine(AppPaths.LogsDirectory, Guid.NewGuid() + ".log");
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        await File.WriteAllTextAsync(log, "Synthetic private name and endpoint 192.0.2.1");
        var settings = File.Exists(AppPaths.SettingsFile) ? await File.ReadAllBytesAsync(AppPaths.SettingsFile) : null;
        try
        {
            var file = await fixture.Diagnostics.CreatePackageAtAsync(["updates"], fixture.Directory);
            using var zip = ZipFile.OpenRead(file);
            var entries = zip.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("diagnose.txt", entries);
            Assert.Contains("manifest.json", entries);
            Assert.Contains("features/system.json", entries);
            Assert.Contains("features/updates.json", entries);
            Assert.DoesNotContain(entries, entry => entry.StartsWith("logs/") || entry.StartsWith("network/") || entry.StartsWith("bilder/"));
            Assert.DoesNotContain("features/pets.json", entries);
            using var manifest = JsonDocument.Parse(await Read(zip, "manifest.json"));
            Assert.Equal(1, manifest.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(new[] { "updates" }, manifest.RootElement.GetProperty("SelectedFeatures").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(new[] { "system" }, manifest.RootElement.GetProperty("AdditionalSources").EnumerateArray().Select(value => value.GetString()));
            foreach (var entry in zip.Entries)
                Assert.DoesNotContain("Synthetic private", await Read(zip, entry.FullName));
            Assert.DoesNotContain("LootTrackingEnabled", await Read(zip, "settings.json"));
            Assert.Equal(settings, File.Exists(AppPaths.SettingsFile) ? await File.ReadAllBytesAsync(AppPaths.SettingsFile) : null);
            Assert.Equal(0, fixture.Capture.Diagnostics.Grabs);
            Assert.False(fixture.Network.Running);
            Assert.Equal(NetworkDiagnosticRecorderState.Idle, fixture.Diagnostics.Recording.State);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public async Task BlockedContributorCannotBlockOtherResultsOrCreateRepeatedWorkers()
    {
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var contributor = new Contributor(id =>
        {
            if (id != "updates") return Result(id);
            Interlocked.Increment(ref calls);
            try { release.Wait(); return Result(id); }
            finally { finished.TrySetResult(); }
        });
        await using var fixture = new Fixture(contributor, TimeSpan.FromMilliseconds(100));
        try
        {
            await fixture.Diagnostics.CheckAsync(["updates"]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DiagnosticStatus.Healthy, fixture.Diagnostics.Results.Single(result => result.FeatureId == "system").Status);
            Assert.Equal(DiagnosticStatus.Unavailable, fixture.Diagnostics.Results.Single(result => result.FeatureId == "updates").Status);
            var file = await fixture.Diagnostics.CreatePackageAtAsync(["updates"], fixture.Directory).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(file));
            Assert.Equal(1, calls);
            Assert.Contains("timeout", File.ReadAllText(fixture.Diagnostics.ReportPath));
        }
        finally { release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task ContributorFailureDoesNotExportExceptionTextOrReuseAnEarlierSelection()
    {
        await using var fixture = new Fixture(new Contributor(id => id == "updates"
            ? throw new IOException("Synthetic private name 192.0.2.2") : Result(id)));
        await fixture.Diagnostics.CheckAsync(["updates"]);
        Assert.Equal(DiagnosticStatus.Error, fixture.Diagnostics.Results.Single(result => result.FeatureId == "updates").Status);
        Assert.DoesNotContain("Synthetic private", File.ReadAllText(fixture.Diagnostics.ReportPath));
        var file = await fixture.Diagnostics.CreatePackageAtAsync(["system"], fixture.Directory);
        using var zip = ZipFile.OpenRead(file);
        Assert.Null(zip.GetEntry("features/updates.json"));
        Assert.DoesNotContain("contributor-failed", await Read(zip, "diagnose.txt"));
    }

    [Fact]
    public async Task BlockedCaptureSupportProbeStillExportsTheIndependentCaptureStage()
    {
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(new Contributor(id =>
        {
            if (id != "capture") return Result(id);
            try { release.Wait(); return Result(id); }
            finally { finished.TrySetResult(); }
        }), TimeSpan.FromMilliseconds(100));
        try
        {
            var file = await fixture.Diagnostics.CreatePackageAtAsync(["capture"], fixture.Directory);
            using var zip = ZipFile.OpenRead(file);
            using var capture = JsonDocument.Parse(await Read(zip, "features/capture.json"));
            Assert.Equal("Unavailable", capture.RootElement.GetProperty("Status").GetString());
            Assert.Equal("ready", capture.RootElement.GetProperty("Facts").GetProperty("stage").GetString());
            Assert.Equal("timeout", capture.RootElement.GetProperty("Facts").GetProperty("Reason").GetString());
            Assert.Equal(0, fixture.Capture.Diagnostics.Grabs);
        }
        finally { release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task MerchantOverlaySelectionPackagesItsSourceAndAnonymousCurrentGateWithoutCapture()
    {
        var health = new MerchantOverlayHealth();
        health.SetWorkerState("running");
        health.ObserveOffers(10, null, null);
        health.Observe("layout-missing", System.Diagnostics.Stopwatch.GetTimestamp());
        await using var fixture = new Fixture(new Contributor(id => id == "overlay-merchant"
            ? DiagnosticContributors.MerchantOverlaySnapshot(true, new(false, true, false, DateTimeOffset.UtcNow),
                health.Snapshot(), DateTimeOffset.UtcNow) : Result(id)));
        var file = await fixture.Diagnostics.CreatePackageAtAsync(["overlay-merchant"], fixture.Directory);
        using var zip = ZipFile.OpenRead(file);
        Assert.NotNull(zip.GetEntry("features/merchant.json"));
        Assert.NotNull(zip.GetEntry("features/network.json"));
        Assert.NotNull(zip.GetEntry("features/capture.json"));
        Assert.Null(zip.GetEntry("features/pets.json"));
        using var overlay = JsonDocument.Parse(await Read(zip, "features/overlay-merchant.json"));
        Assert.Equal("Waiting", overlay.RootElement.GetProperty("Status").GetString());
        var facts = overlay.RootElement.GetProperty("Facts");
        Assert.Equal("layout-missing", facts.GetProperty("analysisReason").GetString());
        Assert.False(facts.GetProperty("nativeVisible").GetBoolean());
        Assert.Equal(10, facts.GetProperty("totalOffers").GetInt32());
        Assert.Equal(JsonValueKind.Null, facts.GetProperty("petRows").ValueKind);
        Assert.Equal(1, facts.GetProperty("layoutMisses").GetInt64());
        var text = await Read(zip, "diagnose.txt");
        Assert.Contains("analysisReason: layout-missing", text);
        Assert.DoesNotContain("SourceGeneration", text);
        Assert.DoesNotContain("ServerId", text);
        Assert.DoesNotContain("GoodsId", text);
        Assert.Equal(0, fixture.Capture.Diagnostics.Grabs);
        Assert.False(fixture.Network.Running);
        Assert.Equal(NetworkDiagnosticRecorderState.Idle, fixture.Diagnostics.Recording.State);
    }

    [Fact]
    public async Task AnExplicitRecordingAddsItsNetworkSourceEvenForANonNetworkSelection()
    {
        await using var fixture = new Fixture(new Contributor(Result), syntheticRecorder: true);
        var recorder = fixture.Network.DiagnosticRecorder;
        Assert.True(fixture.Diagnostics.StartRecording());
        recorder.TryRecord(NetworkDiagnosticRecorderTests.Raw([1, 2, 3]), 1);
        await fixture.Diagnostics.StopRecordingAsync();
        var file = await fixture.Diagnostics.CreatePackageAtAsync(["updates"], fixture.Directory);
        using var zip = ZipFile.OpenRead(file);
        Assert.NotNull(zip.GetEntry("network/game-001.pcap"));
        Assert.NotNull(zip.GetEntry("features/network.json"));
        using var manifest = JsonDocument.Parse(await Read(zip, "manifest.json"));
        Assert.Equal(new[] { "updates" }, manifest.RootElement.GetProperty("SelectedFeatures").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("network", manifest.RootElement.GetProperty("AdditionalSources").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("network/game-001.pcap", manifest.RootElement.GetProperty("Attachments").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task ASlowEarlierRequestCannotReplaceTheNewerSelectionOrReport()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(new Contributor(id =>
        {
            if (id == "updates") { entered.TrySetResult(); release.Wait(); }
            return Result(id);
        }), TimeSpan.FromSeconds(5));
        var earlier = fixture.Diagnostics.CheckAsync(["updates"]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Diagnostics.CheckAsync(["system"]);
        }
        finally { release.Set(); await earlier; }
        Assert.Equal("system", Assert.Single(fixture.Diagnostics.Results).FeatureId);
        Assert.DoesNotContain("[updates]", File.ReadAllText(fixture.Diagnostics.ReportPath));
    }

    [Fact]
    public async Task EmptyUnknownOrCancelledRequestsDoNotProduceAnArchive()
    {
        await using var fixture = new Fixture(new Contributor(Result));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Diagnostics.CreatePackageAtAsync([], fixture.Directory));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Diagnostics.CreatePackageAtAsync(["unknown"], fixture.Directory));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Diagnostics.CreatePackageAtAsync(["system"], fixture.Directory, new CancellationToken(true)));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory));
        Assert.Empty(fixture.Diagnostics.Results);
    }

    [Fact]
    public void PassiveRegistryNeverConstructsLazyServicesAndObservesNormalCreation()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddSingleton(_ => { calls++; return new LazyProbe(); });
        var registry = new DiagnosticServiceRegistry();
        registry.ObserveSingletons(services);
        using var provider = services.BuildServiceProvider();
        Assert.Null(registry.GetService(typeof(LazyProbe)));
        Assert.Equal(0, calls);
        var probe = provider.GetRequiredService<LazyProbe>();
        Assert.Same(probe, registry.GetService(typeof(LazyProbe)));
        Assert.Equal(1, calls);
    }

    private static DiagnosticResult Result(string id) => new(id, DiagnosticStatus.Healthy, DateTimeOffset.UtcNow,
        "Zustand verfügbar, keine Auffälligkeit erkannt.", new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { ["available"] = true }));

    private static async Task<string> Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return await reader.ReadToEndAsync();
    }

    private sealed class LazyProbe;

    private sealed class Contributor(Func<string, DiagnosticResult> collect) : IDiagnosticContributor
    {
        public IReadOnlySet<string> FeatureIds { get; } = DiagnosticFeatureCatalog.All.Select(feature => feature.Id).ToHashSet();
        public DiagnosticResult Collect(string featureId) => collect(featureId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-package-" + Guid.NewGuid().ToString("N"));
        public GameCaptureService Capture { get; } = new();
        public NetworkLootService Network { get; }
        public DiagnosticsService Diagnostics { get; }
        private readonly TrackerService _loot;
        private readonly MapTrackingService _map;
        private readonly byte[]? _reportBefore;

        public Fixture(IDiagnosticContributor contributor, TimeSpan? timeout = null, bool syntheticRecorder = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            _reportBefore = File.Exists(Path.Combine(AppPaths.LogsDirectory, "diagnose.txt")) ? File.ReadAllBytes(Path.Combine(AppPaths.LogsDirectory, "diagnose.txt")) : null;
            var settings = new SettingsService();
            var progress = new ProgressService();
            _loot = new TrackerService(settings);
            _map = new MapTrackingService(settings, progress, Capture);
            Network = new NetworkLootService(progress);
            if (syntheticRecorder)
                typeof(NetworkLootService).GetField("_diagnosticRecorder", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(Network, new NetworkDiagnosticRecorder(begin => begin(), Path.Combine(Directory, "captures")));
            Diagnostics = new(settings, Capture, _loot, _map, Network, [contributor], runTimer: false, contributorTimeout: timeout);
        }

        public ValueTask DisposeAsync()
        {
            Diagnostics.Dispose(); Network.Dispose(); _map.Dispose(); _loot.Dispose(); Capture.Dispose();
            if (_reportBefore is null) { if (File.Exists(Diagnostics.ReportPath)) File.Delete(Diagnostics.ReportPath); }
            else File.WriteAllBytes(Diagnostics.ReportPath, _reportBefore);
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
