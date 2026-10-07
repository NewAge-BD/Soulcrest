using OpenCvSharp;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.Ocr.Tests;

/// <summary>
/// Colleague's scan 2026-10-07: Stone Spirit was taken for Odyle Stone Spirit. Both portraits are one
/// picture, blue and green; the shape matching works on grey (a real Stone Spirit card recoloured green
/// still scored 71 to 3 for Stone Spirit), so among colour variants the hue decides.
/// </summary>
public sealed class PortraitColourVariantTests
{
    private const string Folder = "icons/gamingtools/ui/resource/texture/portrait/portrait_vehicle";
    private static readonly string Card = Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "card-portrait-stone-spirit.png");

    [Theory]
    [InlineData(0, "stone-spirit", "odyle-stone-spirit")]   // the real card, blue
    [InlineData(35, "odyle-stone-spirit", "stone-spirit")]  // the same card turned green (hue 95 → 60)
    public void AmongColourVariantsTheHueDecides(int hueShift, string expected, string other)
    {
        if (Icons() is not { } icons || !File.Exists(Card))
            return; // map data package not built here
        using var matcher = new PortraitMatcher();
        matcher.AddReference("stone-spirit", Path.Combine(icons, "ut_vehicle_portrait_eartheleod_01.png"));
        matcher.AddReference("odyle-stone-spirit", Path.Combine(icons, "ut_vehicle_portrait_eartheleod_01_cv01.png"));
        using var card = Recoloured(Cv2.ImRead(Card), hueShift);

        var match = matcher.Match(card);

        Assert.Equal(expected, match.PetId);
        Assert.Equal(other, match.SecondId);
        Assert.True(match.IsConfident, $"{match}");
    }

    [Fact]
    public void GreyPicturesHaveNoHue()
    {
        using var grey = new Mat(64, 64, MatType.CV_8UC3, new Scalar(90, 90, 90));
        Assert.Null(PortraitMatcher.ColourSignature(grey)); // greyed-out locked pets keep the shape's result
    }

    [Fact]
    public void ArtKeyJoinsColourVariants() =>
        Assert.Equal(PortraitMatcher.ArtKey("a/ut_vehicle_portrait_eartheleod_01.png"), PortraitMatcher.ArtKey("a/ut_vehicle_portrait_eartheleod_01_cv01.png"));

    private static Mat Recoloured(Mat image, int hueShift)
    {
        if (hueShift == 0)
            return image;
        using var hsv = new Mat();
        Cv2.CvtColor(image, hsv, ColorConversionCodes.BGR2HSV);
        var channels = Cv2.Split(hsv);
        Cv2.Subtract(channels[0], new Scalar(hueShift), channels[0]);
        Cv2.Merge(channels, hsv);
        var result = new Mat();
        Cv2.CvtColor(hsv, result, ColorConversionCodes.HSV2BGR);
        foreach (var c in channels) c.Dispose();
        image.Dispose();
        return result;
    }

    private static string? Icons()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "imports", "generated", "mapdata", Folder);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
