using System.Collections;
using System.Reflection;
using System.Text.Json;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class MapReferenceCacheTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-map")]
    [InlineData("wrong-scale")]
    [InlineData("wrong-size")]
    [InlineData("truncated")]
    public void CacheIdentityAndGeometryMustMatchBeforeTheTrackerUsesIt(string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-mapref-cache-" + Guid.NewGuid().ToString("N"));
        var version = "test" + Guid.NewGuid().ToString("N");
        var cache = Path.Combine(AppPaths.MapReferenceDirectory, $"synthetic-cache-z0-{version}.bin");
        var previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        Directory.CreateDirectory(directory);
        try
        {
            var pixels = new byte[256 * 256];
            new Random(837).NextBytes(pixels);
            using var gray = Mat.FromPixelData(256, 256, MatType.CV_8UC1, pixels);
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
            {
                version, maps = new[] { new { id = "synthetic-cache", label = "Synthetic", tiles = "{z}_{x}_{y}.png",
                    refZoom = 0, size = 256, maxNativeZoom = 0, tileSize = 256 } }
            }));
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", directory);
            if (kind == "truncated")
            {
                Directory.CreateDirectory(AppPaths.MapReferenceDirectory);
                File.WriteAllBytes(cache, [0xff]);
            }
            else
            {
                using var wrongSize = new Mat();
                if (kind == "wrong-size") Cv2.Resize(gray, wrongSize, new OpenCvSharp.Size(512, 512));
                using var reference = MapReference.Build(kind == "wrong-map" ? "other-map" : "synthetic-cache",
                    kind == "wrong-size" ? wrongSize : gray, kind == "wrong-scale" ? 2 : 1);
                reference.Save(cache);
            }
            // A valid cache must work without tiles. Every rejected cache is rebuilt from this synthetic tile.
            if (kind != "valid") Cv2.ImWrite(Path.Combine(directory, "0_0_0.png"), gray);
            using var capture = new GameCaptureService();
            using var tracking = new MapTrackingService(new SettingsService(), new ProgressService(), capture);
            var definition = Assert.Single(((IEnumerable)typeof(MapTrackingService)
                .GetMethod("LoadMaps", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tracking, null)!).Cast<object>());
            var references = (Dictionary<string, MapLocator>)typeof(MapTrackingService)
                .GetField("_coarse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracking)!;
            var locator = Assert.IsType<MapLocator>(typeof(MapTrackingService)
                .GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(tracking, [definition, 0, references, CancellationToken.None]));

            Assert.Equal("synthetic-cache", locator.Reference.MapId);
            Assert.Equal(1, locator.Reference.WorldPerPixel);
            Assert.Equal(256, locator.Reference.Width);
            Assert.Equal(256, locator.Reference.Height);
            Assert.True(locator.Reference.Points.Length >= 32);
            Assert.Equal(0, capture.Diagnostics.Grabs);
            using var saved = MapReference.Load(cache);
            Assert.NotNull(saved);
            Assert.Equal(locator.Reference.MapId, saved.MapId);
            Assert.Equal(locator.Reference.WorldPerPixel, saved.WorldPerPixel);
            Assert.Equal(locator.Reference.Width, saved.Width);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", previousMapData);
            File.Delete(cache);
            Directory.Delete(directory, true);
        }
    }
}
