using System.Runtime.CompilerServices;
using Soulcrest.App.Services;
using Xunit;
using System.Drawing;
using System.Runtime.InteropServices;
using Soulcrest.App.Capture;

// All tests share one temporary data folder (AppPaths.DataDirectory is fixed per process).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Soulcrest.App.Tests;

internal static class TestData
{
    /// <summary>Redirects AppPaths to a temp folder before anything touches it.</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("SOULCREST_DATA", directory);
    }
}

public sealed class PetScanServiceTests
{
    [Fact]
    public async Task BusyReadbackPreservesScannedPetsAndRecoversOnTheNextFrame()
    {
        var settings = new SettingsService();
        var progress = new ProgressService();
        Assert.NotNull(progress.MapDataDirectory);
        using var scan = new PetScanService(settings, progress);
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-all.png");
        await scan.ScanFileAsync(fixture);
        var before = scan.Entries.Select(e => (e.Key, e.Seen, e.Reading)).ToArray();
        var frames = scan.Frames;
        for (var i = 0; i < 3; i++)
            Assert.False(await scan.ReadFrameAsync(() => throw new COMException("busy", CaptureReadback.WasStillDrawing), CancellationToken.None));
        Assert.Equal(before, scan.Entries.Select(e => (e.Key, e.Seen, e.Reading)).ToArray());
        Assert.Equal(frames, scan.Frames);
        Assert.False(scan.StatusIsError);
        Assert.Equal(CaptureReadback.Waiting, scan.Status);
        Assert.True(await scan.ReadFrameAsync(() => new Bitmap(fixture), CancellationToken.None));
        Assert.Equal(frames + 1, scan.Frames);
        Assert.False(scan.StatusIsError);
        Assert.NotEqual(CaptureReadback.Waiting, scan.Status);

        var recoveredStatus = scan.Status;
        using var duringRead = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.ReadFrameAsync(() =>
        {
            duringRead.Cancel();
            throw new COMException("busy", CaptureReadback.WasStillDrawing);
        }, duringRead.Token));
        Assert.Equal(recoveredStatus, scan.Status);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.ReadFrameAsync(
            () => throw new Xunit.Sdk.XunitException("Capture must not run after cancellation."), stopped.Token));
    }

    [Fact]
    public async Task ScreenshotIsMergedLearnedAndApplied()
    {
        Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        var settings = new SettingsService();
        var progress = new ProgressService();
        if (progress.MapDataDirectory is null || !Soulcrest.Ocr.WindowsOcrLineReader.AvailableLanguages().Contains("en-US"))
            return; // needs the generated map data package and Windows OCR (see CLAUDE.md)
        using var scan = new PetScanService(settings, progress);

        await scan.ScanFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-all.png"));
        var entries = scan.Entries;

        Assert.Equal(15, entries.Count);
        Assert.Equal((94, 200), scan.Collection);
        var swarm = Assert.Single(entries, e => e.PetId == "swarm");
        Assert.Equal(ScanConfidence.Confirmed, swarm.Confidence); // selected card + name in the info panel
        Assert.Equal((3, 0), swarm.Reading);
        Assert.Equal((2, 42), Assert.Single(entries, e => e.PetId == "magic-gravi").Reading);
        Assert.Equal((2, 20), Assert.Single(entries, e => e.PetId == "lesser-fire-spirit").Reading);
        var pagati = Assert.Single(entries, e => e.PetId == "pagati");
        Assert.True(pagati.Apply);
        Assert.DoesNotContain(entries, e => e.PetId is null);

        var applied = scan.ApplySelected();
        Assert.Equal(15, applied);
        Assert.True(progress.Thresholds.IsMax(progress.Souls("swarm")));
        Assert.Equal("Lv. 2 · 42/75", progress.LevelText("magic-gravi"));
        Assert.Equal("Lv. 2 · 17/75", progress.LevelText("skyray"));

        // A pet not in the map data is learned from the panel name and gets its own catalog entry.
        var learned = progress.LearnPet("Test Bird", pagati.PortraitPng);
        scan.Assign(pagati, learned.Id);
        Assert.True(progress.IsLearned("test-bird"));
        Assert.NotNull(new ProgressService().Catalog.Find("test-bird")); // persisted
    }
}
