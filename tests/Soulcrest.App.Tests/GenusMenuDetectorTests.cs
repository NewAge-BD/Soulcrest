using System.Drawing;
using System.Diagnostics;
using OpenCvSharp;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GenusMenuDetectorTests
{
    private static readonly string[] Genera = ["cogni", "fera", "natura", "varian", "special"];
    private static readonly Point2d[] Symbols = [new(94, -343), new(31, -172), new(0, 0), new(31, 172), new(94, 344)];
    private static readonly Point2d[] Wheel = [new(823, -362), new(657, -268), new(560, -104), new(560, 88),
        new(657, 250), new(823, 348), new(1011, 348), new(1175, 250), new(1270, 88)];

    [Theory]
    [InlineData("cogni", .6)] [InlineData("fera", .8)] [InlineData("natura", 1)]
    [InlineData("varian", 1.2)] [InlineData("special", 1.4)]
    public void FiveGlyphGeometryAndUniqueSelectedRingIdentifyTheCategoryAcrossUiScales(string genus, double scale)
    {
        using var frame = Menu(genus, scale);
        var found = new GenusMenuDetector().Detect(frame);
        Assert.NotNull(found); Assert.Equal(genus, found.Genus);
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)] [InlineData((byte)3)]
    public void SelectedPresetIsReadFromItsSquareFrameWithoutAnyDigit(byte preset)
    {
        using var frame = Menu("natura", preset: preset);
        Assert.Equal(preset, new GenusMenuDetector().Detect(frame)?.ViewedPreset);
        using var ambiguous = Menu("natura", preset: preset, secondPreset: (byte)(preset % 3 + 1));
        Assert.Null(new GenusMenuDetector().Detect(ambiguous)?.ViewedPreset);
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, false)]
    public void AGenusRailOrWheelAloneDoesNotProveAnInsightMenu(bool symbols, bool wheel)
    {
        using var frame = Menu("fera", symbols: symbols, wheel: wheel);
        Assert.Null(new GenusMenuDetector().Detect(frame));
    }

    [Fact]
    public void AmbiguousSelectedRingsNeverChooseACategory()
    {
        using var frame = Menu("cogni", secondSelected: "varian");
        var found = new GenusMenuDetector().Detect(frame);
        Assert.NotNull(found); Assert.Null(found.Genus);
    }

    [Theory]
    [InlineData("map-tracking", "live-2026-10-03-screen.png")]
    [InlineData("esc-menu", "user-2026-10-10.png")]
    [InlineData("pet-window", "en-2026-10-03-uiscale-medium.png")]
    public void ExistingGameMapEscAndPetCollectionImagesDoNotActivateInsight(string folder, string file)
    {
        using var frame = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", folder, file));
        Assert.False(frame.Empty()); Assert.Null(new GenusMenuDetector().Detect(frame));
    }

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void ProvidedFivePrivateScreenshotsSelectTheirActualCategoriesWithoutReadingText()
    {
        var paths = TestPrerequisites.RequiredValue("SOULCREST_GENUS_MENU_IMAGES").Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, paths.Length);
        for (var index = 0; index < paths.Length; index++)
        {
            using var frame = Cv2.ImRead(Path.GetFullPath(paths[index])); Assert.False(frame.Empty());
            Assert.Equal(Genera[index], new GenusMenuDetector().Detect(frame)?.Genus);
            Assert.Equal((byte)1, new GenusMenuDetector().Detect(frame)?.ViewedPreset);
            // Currencies, action labels and the entire stat sidebar are not recognition evidence.
            Cv2.Rectangle(frame, new Rect(0, 0, frame.Width, (int)(frame.Height * .15)), Scalar.All(0), -1);
            Cv2.Rectangle(frame, new Rect((int)(frame.Width * .77), 0, frame.Width - (int)(frame.Width * .77), frame.Height), Scalar.All(0), -1);
            Assert.Equal(Genera[index], new GenusMenuDetector().Detect(frame)?.Genus);
        }
    }

    private static Mat Menu(string selected, double scale = 1, bool symbols = true, bool wheel = true, string? secondSelected = null,
        byte? preset = 1, byte? secondPreset = null)
    {
        var frame = new Mat(1440, Math.Max(2560, (int)Math.Ceiling(450 + 1850 * scale)), MatType.CV_8UC3, Scalar.All(4));
        var origin = new Point2d(450, 720);
        if (symbols)
        {
            using var strip = GenusMenuDetector.ReadSymbols();
            for (var index = 0; index < 5; index++)
            {
                var center = new OpenCvSharp.Point((int)Math.Round(origin.X + Symbols[index].X * scale), (int)Math.Round(origin.Y + Symbols[index].Y * scale));
                Cv2.Circle(frame, center, (int)Math.Round(72 * scale), Genera[index] == selected || Genera[index] == secondSelected
                    ? new Scalar(240, 240, 220) : new Scalar(90, 90, 90), Math.Max(2, (int)Math.Round(5 * scale)));
                using var glyph = new Mat(strip, new Rect(index * 56, 0, 56, 56));
                using var resized = new Mat(); using var color = new Mat();
                Cv2.Resize(glyph, resized, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
                Cv2.CvtColor(resized, color, ColorConversionCodes.GRAY2BGR);
                using var destination = new Mat(frame, new Rect(center.X - color.Width / 2, center.Y - color.Height / 2, color.Width, color.Height));
                color.CopyTo(destination);
            }
        }
        if (wheel)
            foreach (var offset in Wheel)
            {
                var center = new OpenCvSharp.Point((int)Math.Round(origin.X + offset.X * scale), (int)Math.Round(origin.Y + offset.Y * scale));
                Cv2.Circle(frame, center, (int)Math.Round(38 * scale), new Scalar(180, 210, 230), Math.Max(3, (int)Math.Round(8 * scale)));
            }
        for (byte index = 1; index <= 3; index++)
        {
            if (index != preset && index != secondPreset) continue;
            var x = (int)Math.Round(origin.X + (1689 + (index - 1) * 56) * scale);
            var y = (int)Math.Round(origin.Y - 616 * scale); var half = (int)Math.Round(20 * scale);
            if (x + half < frame.Width && y - half >= 0)
                Cv2.Rectangle(frame, new Rect(x - half, y - half, half * 2, half * 2), new Scalar(225, 235, 245), Math.Max(2, (int)Math.Round(3 * scale)));
        }
        return frame;
    }
}

public sealed class GenusMenuObservationTests
{
    [Fact]
    public void ChangingViewedPresetRequiresFreshConfirmationAndNeverKeepsTheFormerBoardAuthority()
    {
        var now = Stopwatch.GetTimestamp(); var bounds = new Rectangle(0, 0, 1280, 720);
        using var source = new GenusMenuObservationService(() => null, () => bounds, () => now);
        using var lease = source.Acquire(); var revision = source.Revision;
        source.Observe(new("fera", .99, 1), bounds, now, 1, 1, revision);
        now += Stopwatch.Frequency / 5; source.Observe(new("fera", .99, 1), bounds, now, 1, 2, revision);
        Assert.Equal((byte)1, source.Snapshot.ViewedPreset);
        now += Stopwatch.Frequency / 5; source.Observe(new("fera", .99, 2), bounds, now, 1, 3, revision);
        Assert.Null(source.Snapshot.ViewedPreset); Assert.Null(source.Snapshot.Genus);
        now += Stopwatch.Frequency / 5; source.Observe(new("fera", .99, 2), bounds, now, 1, 4, revision);
        Assert.Equal((byte)2, source.Snapshot.ViewedPreset);
    }
    [Fact]
    public void StableSelectionRequiresTwoDistinctFreshFramesAndNegativeEvidenceHidesImmediately()
    {
        var now = Stopwatch.GetTimestamp(); var bounds = new Rectangle(0, 0, 1280, 720);
        using var source = new GenusMenuObservationService(() => throw new InvalidOperationException("No capture in tests."), () => bounds, () => now);
        using var lease = source.Acquire(); var revision = source.Revision;
        Assert.True(source.Observe(new("cogni", .99), bounds, now, 1, 1, revision));
        Assert.True(source.Snapshot.MenuVisible); Assert.Null(source.Snapshot.Genus);
        Assert.False(source.Observe(new("cogni", .99), bounds, now, 1, 1, revision));
        now += Stopwatch.Frequency / 5;
        Assert.True(source.Observe(new("cogni", .99), bounds, now, 1, 2, revision));
        Assert.Equal("cogni", source.Snapshot.Genus);
        now += Stopwatch.Frequency / 5;
        Assert.True(source.Observe(null, bounds, now, 1, 3, revision));
        Assert.False(source.Snapshot.MenuVisible); Assert.Null(source.Snapshot.Genus);
    }

    [Fact]
    public void ExpiredFutureOrReboundFramesCannotSupplyASelectedCategory()
    {
        var now = Stopwatch.GetTimestamp(); Rectangle? bounds = new(0, 0, 1280, 720);
        using var source = new GenusMenuObservationService(() => null, () => bounds, () => now);
        using var lease = source.Acquire(); var revision = source.Revision;
        Assert.False(source.Observe(new("fera", .99), bounds.Value, now + Stopwatch.Frequency, 1, 1, revision));
        Assert.True(source.Observe(new("fera", .99), bounds.Value, now, 1, 1, revision));
        now += Stopwatch.Frequency / 5; source.Observe(new("fera", .99), bounds.Value, now, 1, 2, revision);
        Assert.Equal("fera", source.Snapshot.Genus);
        var generation = source.Snapshot.SourceGeneration;
        now += Stopwatch.Frequency / 5; source.Observe(new("fera", .99), bounds.Value, now, 2, 3, revision);
        Assert.Null(source.Snapshot.Genus); Assert.True(source.Snapshot.SourceGeneration > generation);
        now += Stopwatch.Frequency / 5; source.Observe(new("fera", .99), bounds.Value, now, 2, 4, revision);
        Assert.Equal("fera", source.Snapshot.Genus);
        now += Stopwatch.Frequency;
        Assert.Null(source.Snapshot.MenuVisible); Assert.Null(source.Snapshot.Genus);
        bounds = null;
        Assert.Null(source.Snapshot.MenuVisible);
    }

    [Fact]
    public void LastLeaseRevokesPendingWorkAndAdditionalConsumersShareTheObservation()
    {
        var now = Stopwatch.GetTimestamp(); var bounds = new Rectangle(0, 0, 1280, 720);
        using var source = new GenusMenuObservationService(() => throw new InvalidOperationException("No capture in tests."), () => bounds, () => now);
        var first = source.Acquire(); var second = source.Acquire(); var revision = source.Revision;
        source.Observe(new("varian", .99), bounds, now, 1, 1, revision);
        now += Stopwatch.Frequency / 5; source.Observe(new("varian", .99), bounds, now, 1, 2, revision);
        first.Dispose(); Assert.Equal("varian", source.Snapshot.Genus);
        second.Dispose(); Assert.Null(source.Snapshot.MenuVisible);
        now += Stopwatch.Frequency / 5;
        Assert.False(source.Observe(new("varian", .99), bounds, now, 1, 3, revision));
        using var third = source.Acquire();
        Assert.True(source.Revision > revision); Assert.Null(source.Snapshot.Genus);
    }
}
