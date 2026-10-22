using OpenCvSharp;
using Soulcrest.Ocr.Daevanion;
using Xunit;
using Size = OpenCvSharp.Size;

namespace Soulcrest.Ocr.Tests;

public sealed class DaevanionAppearanceTests
{
    [Theory]
    [InlineData("common", false)]
    [InlineData("common", true)]
    [InlineData("rare", false)]
    [InlineData("rare", true)]
    [InlineData("legend", false)]
    [InlineData("legend", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    public void OriginalActiveAndDisabledArtworkIsDistinguishedAcrossScaleAndBrightness(string quality, bool inactive)
    {
        var expected = inactive ? DaevanionNodeAppearance.Inactive : DaevanionNodeAppearance.Active;
        foreach (var size in new[] { 48, 70, 100 })
        foreach (var brightness in new[] { .55, 1d, 1.25 })
        {
            using var frame = OriginalFrame(quality, inactive, size);
            frame.ConvertTo(frame, MatType.CV_8UC3, brightness);
            using var gray = new Mat(); Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            using var red = new Mat(); Cv2.ExtractChannel(frame, red, 2);
            Assert.Equal(expected, DaevanionBoardLocator.ClassifyAppearance(gray, red, new(110, 110), size));
            Assert.Equal(expected, DaevanionBoardLocator.ClassifyAppearance(gray, null, new(110, 110), size));
        }
    }

    [Fact]
    public void FlatOrMissingArtworkDoesNotInventAnActivationState()
    {
        using var gray = new Mat(220, 220, MatType.CV_8UC1, new Scalar(42));
        Assert.Equal(DaevanionNodeAppearance.Unknown, DaevanionBoardLocator.ClassifyAppearance(gray, null, new(110, 110), 70));
        Assert.Equal(DaevanionNodeAppearance.Unknown, new DaevanionNodeAnchor(3, 3, new(0, 0, 70, 70), .9).Appearance);
    }

    private static Mat OriginalFrame(string quality, bool inactive, int size)
    {
        var suffix = "ut_fwindow_daevanion_node_" + quality + (inactive ? "_disabled_sprite.png" : "_sprite.png");
        var assembly = typeof(DaevanionBoardLocator).Assembly;
        var name = assembly.GetManifestResourceNames().Single(value => value.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        using var original = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Unchanged);
        using var alpha = new Mat(); Cv2.ExtractChannel(original, alpha, 3);
        using var mask = new Mat(); Cv2.Threshold(alpha, mask, 180, 255, ThresholdTypes.Binary);
        var bounds = Cv2.BoundingRect(mask);
        var sx = size / (bounds.Width - 1d); var sy = size / (bounds.Height - 1d);
        using var transform = Mat.FromArray(new double[,]
        {
            { sx, 0, 110 - (bounds.X + (bounds.Width - 1d) / 2) * sx },
            { 0, sy, 110 - (bounds.Y + (bounds.Height - 1d) / 2) * sy }
        });
        using var warped = new Mat();
        Cv2.WarpAffine(original, warped, transform, new Size(220, 220), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        var result = new Mat(220, 220, MatType.CV_8UC3, new Scalar(29, 33, 34));
        var height = result.Height; var width = result.Width;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = warped.At<Vec4b>(y, x); var weight = color.Item3 / 255d;
            var background = result.At<Vec3b>(y, x);
            result.Set(y, x, new Vec3b((byte)Math.Round(color.Item0 * weight + background.Item0 * (1 - weight)),
                (byte)Math.Round(color.Item1 * weight + background.Item1 * (1 - weight)),
                (byte)Math.Round(color.Item2 * weight + background.Item2 * (1 - weight))));
        }
        return result;
    }
}
