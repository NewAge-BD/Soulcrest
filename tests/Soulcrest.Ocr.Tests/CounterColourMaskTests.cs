using OpenCvSharp;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.Ocr.Tests;

public sealed class CounterColourMaskTests
{
    // Native text bounds from existing original captures; values checked in the fixture/test ground truth.
    [Theory]
    [InlineData("en-2026-10-03-level2-live.png", 358, 774, 58, 24, "9/75")]
    [InlineData("en-2026-10-03-level2-live.png", 199, 995, 58, 21, "3/75")]
    [InlineData("en-2026-10-03-level2-live.png", 540, 992, 34, 24, "2/75")]
    public async Task NativeColourAndBaselineSeparateDigitsFromBrightPortraits(string file, int x, int y, int width, int height, string expected)
    {
        Assert.Contains("en-US", WindowsOcrLineReader.AvailableLanguages());
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", file));
        using var mask = CounterColourMask.Create(image, new Rect(x, y, width, height));
        Assert.NotNull(mask);
        Assert.Equal(MatType.CV_8UC1, mask.Type());
        Assert.Equal(expected, await Read(mask, 4));
        Assert.Equal(expected, await Read(mask, 6));
    }

    [Fact]
    public async Task ReferenceAdaptsToDimmerTintedCaptureInsteadOfRequiringRgb255()
    {
        Assert.Contains("en-US", WindowsOcrLineReader.AvailableLanguages());
        using var image = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-level2-live.png"));
        var channels = Cv2.Split(image);
        try
        {
            // A controlled colour perturbation of a real crop, not a claim of measured HDR coverage.
            for (var i = 0; i < channels.Length; i++) channels[i].ConvertTo(channels[i], MatType.CV_8U, .8 + i * .02);
            Cv2.Merge(channels, image);
            using var mask = CounterColourMask.Create(image, new Rect(358, 774, 58, 24));
            Assert.NotNull(mask);
            Assert.Equal("9/75", await Read(mask, 4));
        }
        finally
        {
            foreach (var channel in channels) channel.Dispose();
        }
    }

    [Fact]
    public void FlatWhiteAndDarkAreasAreNotText()
    {
        using var flat = new Mat(60, 120, MatType.CV_8UC3, Scalar.White);
        Assert.Null(CounterColourMask.Create(flat, new Rect(20, 20, 60, 20)));
        flat.SetTo(Scalar.Black);
        Assert.Null(CounterColourMask.Create(flat, new Rect(20, 20, 60, 20)));
        Assert.Null(CounterColourMask.Create(flat, new Rect(-100, -100, 5, 5)));
    }

    private static async Task<string> Read(Mat mask, int scale)
    {
        using var padded = new Mat();
        Cv2.CopyMakeBorder(mask, padded, 12, 12, 24, 24, BorderTypes.Constant, Scalar.White);
        using var color = new Mat();
        Cv2.CvtColor(padded, color, ColorConversionCodes.GRAY2BGR);
        using var bitmap = BitmapMat.ToBitmap(color);
        var reader = WindowsOcrLineReader.Create("en-US", scale);
        return string.Join(" ", (await reader.ReadAsync(bitmap)).Select(l => l.Text));
    }
}
