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
    public async Task FreshGridMovementDiscardsOcrWithoutChangingEntriesAndThenRecovers()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window");
        var first = Path.Combine(fixtures, "en-2026-10-03-all.png");
        await scan.ScanFileAsync(first);
        await scan.ScanFileAsync(first);
        Assert.True(scan.Page!.Stable);
        var frames = scan.Frames;
        var before = scan.Entries.Select(e => (e.Key, e.PetId, e.Seen, e.Reading)).ToArray();
        await scan.ReadFrameAsync(() => new Bitmap(first), CancellationToken.None,
            () => new Bitmap(Path.Combine(fixtures, "en-2026-10-03-live-2560.png")));
        Assert.False(scan.Page!.Stable);
        Assert.Equal(frames, scan.Frames);
        Assert.Equal(before, scan.Entries.Select(e => (e.Key, e.PetId, e.Seen, e.Reading)).ToArray());
        await scan.ReadFrameAsync(() => new Bitmap(first), CancellationToken.None, () => new Bitmap(first));
        Assert.False(scan.Page!.Stable);
        await scan.ReadFrameAsync(() => new Bitmap(first), CancellationToken.None, () => new Bitmap(first));
        Assert.True(scan.Page!.Stable);
        Assert.Equal(15, scan.Entries.Count);
    }

    [Fact]
    public async Task LiveFramesCreateAndUpdateEntriesOnlyWhenTheGridHasSettled()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window");
        var first = Path.Combine(fixtures, "en-2026-10-03-all.png");
        // A single-picture import remains an explicit confirmation; then start a clean live sequence.
        await scan.ScanFileAsync(first);
        Assert.Equal(15, scan.Entries.Count);
        scan.Clear();
        await scan.ReadFrameAsync(() => new Bitmap(first), CancellationToken.None);
        Assert.Empty(scan.Entries);
        Assert.False(scan.Page!.Stable);
        await scan.ReadFrameAsync(() => new Bitmap(first), CancellationToken.None);
        Assert.Equal(15, scan.Entries.Count);
        Assert.True(scan.Page!.Stable);
        var before = scan.Entries.Select(e => (e.Key, e.PetId, e.Seen, e.Reading)).ToArray();
        await scan.ReadFrameAsync(() => new Bitmap(Path.Combine(fixtures, "en-2026-10-03-live-2560.png")), CancellationToken.None);
        Assert.False(scan.Page!.Stable);
        Assert.Equal(before, scan.Entries.Select(e => (e.Key, e.PetId, e.Seen, e.Reading)).ToArray());
    }

    [Fact]
    public async Task MovingGridInvalidatesLabelsBeforeTheNextOcrCompletes()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window");
        await scan.ScanFileAsync(Path.Combine(fixtures, "en-2026-10-03-all.png"));
        await scan.ScanFileAsync(Path.Combine(fixtures, "en-2026-10-03-all.png"));
        Assert.True(scan.Page!.Stable);
        var frames = scan.Frames;
        var hiddenBeforeOcrFinished = false;
        scan.Changed += () => hiddenBeforeOcrFinished |= scan.Frames == frames && scan.Page is { Stable: false };
        await scan.ScanFileAsync(Path.Combine(fixtures, "en-2026-10-03-live-2560.png"));
        Assert.True(hiddenBeforeOcrFinished);
        Assert.False(scan.Page!.Stable);
    }

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

        Assert.False(scan.CorrectValue(pagati.Key, "75/75"));
        Assert.True(scan.CorrectValue(pagati.Key, "7/75"));
        // A later frame/panel cannot overwrite the user's value, and only the captured target changes.
        await scan.ScanFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-all.png"));
        Assert.Equal((2, 7), Assert.Single(scan.Entries, e => e.Key == pagati.Key).Reading);
        Assert.Equal((3, 0), Assert.Single(scan.Entries, e => e.PetId == "swarm").Reading);
        var correctedMark = Assert.Single(scan.Page!.Marks, m => m.EntryKey == pagati.Key);
        Assert.Equal("Manuell", correctedMark.Source);
        Assert.Equal("7/75", correctedMark.Value);
        Assert.Equal("Pagati", correctedMark.PetName);
        Assert.All(scan.Page.Marks, mark =>
        {
            Assert.NotNull(mark.ProgressBounds);
            Assert.True(Soulcrest.App.Overlay.ScanMarkerOverlayForm.ValueTag(mark).Bottom < mark.ProgressBounds.Value.Top);
            Assert.False(string.IsNullOrWhiteSpace(mark.PetName));
        });
        if (Environment.GetEnvironmentVariable("SOULCREST_SCANPAGE_PNG") is { Length: > 0 } preview)
        {
            using var picture = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-all.png"));
            using var labels = Soulcrest.App.Overlay.ScanMarkerOverlayForm.RenderMarks(scan.Page.Marks, new Rectangle(Point.Empty, picture.Size));
            using (var graphics = Graphics.FromImage(picture)) graphics.DrawImageUnscaled(labels, Point.Empty);
            picture.Save(preview, System.Drawing.Imaging.ImageFormat.Png);
        }
        scan.ApplySelected();
        Assert.Equal("Lv. 2 · 7/75", progress.LevelText("pagati"));

        // A pet not in the map data is learned from the panel name and gets its own catalog entry.
        var learned = progress.LearnPet("Test Bird", pagati.PortraitPng);
        scan.Assign(pagati, learned.Id);
        Assert.True(progress.IsLearned("test-bird"));
        Assert.NotNull(new ProgressService().Catalog.Find("test-bird")); // persisted
    }
}
