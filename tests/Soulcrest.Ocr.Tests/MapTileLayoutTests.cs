using OpenCvSharp;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.Ocr.Tests;

public sealed class MapTileLayoutTests
{
    [Fact]
    public void OriginalAndSplitTilesProduceIdenticalTrackingReferencePixels()
    {
        var root = Path.Combine(Path.GetTempPath(), "soulcrest-tile-layout-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            using var source = new Mat(512, 512, MatType.CV_8UC3);
            Cv2.Randu(source, Scalar.All(0), Scalar.All(255));
            var original = Path.Combine(root, "original");
            var split = Path.Combine(root, "split");
            Directory.CreateDirectory(original);
            Directory.CreateDirectory(split);
            Cv2.ImWrite(Path.Combine(original, "1_0_0.png"), source);
            for (var y = 0; y < 2; y++)
                for (var x = 0; x < 2; x++)
                {
                    using var tile = new Mat(source, new Rect(x * 256, y * 256, 256, 256));
                    Cv2.ImWrite(Path.Combine(split, $"1_{x}_{y}.png"), tile);
                }
            using var oldReference = MapReference.ComposeTiles(Path.Combine(split, "{z}_{x}_{y}.png"), 1, 256);
            using var newReference = MapReference.ComposeTiles(Path.Combine(original, "{z}_{x}_{y}.png"), 1, 512);
            using var detected = MapReference.ComposeTiles(Path.Combine(original, "{z}_{x}_{y}.png"), 1);
            Assert.Equal(512, newReference.Width);
            Assert.Equal(0, Cv2.Norm(oldReference, newReference, NormTypes.INF));
            Assert.Equal(0, Cv2.Norm(newReference, detected, NormTypes.INF));
        }
        finally { Directory.Delete(root, true); }
    }
}
