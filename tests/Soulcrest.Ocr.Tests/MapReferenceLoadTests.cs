using System.Text;
using OpenCvSharp;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.Ocr.Tests;

public sealed class MapReferenceLoadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-mapref-load-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_directory, "reference.bin");

    public MapReferenceLoadTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void BuiltReferenceRoundTripsEveryPointAndDescriptor()
    {
        var pixels = new byte[256 * 256];
        new Random(415).NextBytes(pixels);
        using var gray = Mat.FromPixelData(256, 256, MatType.CV_8UC1, pixels);
        using var reference = MapReference.Build("synthetic-map", gray, 2.5);
        reference.Save(CachePath);

        using var loaded = MapReference.Load(CachePath);
        Assert.NotNull(loaded);
        Assert.Equal(reference.MapId, loaded.MapId);
        Assert.Equal(reference.Width, loaded.Width);
        Assert.Equal(reference.Height, loaded.Height);
        Assert.Equal(reference.WorldPerPixel, loaded.WorldPerPixel);
        Assert.Equal(reference.Points, loaded.Points);
        Assert.Equal(reference.DescriptorData, loaded.DescriptorData);
    }

    [Fact]
    public void MissingTruncatedAndExtendedCachesAreMisses()
    {
        Assert.Null(MapReference.Load(CachePath));
        var valid = Cache();
        foreach (var length in Enumerable.Range(0, 64).Append(valid.Length - 1))
        {
            File.WriteAllBytes(CachePath, valid[..length]);
            Assert.Null(MapReference.Load(CachePath));
        }
        File.WriteAllBytes(CachePath, valid.Concat(new byte[] { 0 }).ToArray());
        Assert.Null(MapReference.Load(CachePath));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(2_000_000)]
    [InlineData(4_194_303)]
    [InlineData(4_194_304)]
    [InlineData(int.MaxValue)]
    public void InvalidOrUnsupportedPointCountsAreRejectedBeforeAllocatingPayload(int count)
    {
        File.WriteAllBytes(CachePath, Cache(count: count));
        Assert.Null(MapReference.Load(CachePath));
    }

    [Theory]
    [InlineData(0, 256)]
    [InlineData(256, 0)]
    [InlineData(-1, 256)]
    [InlineData(256, -1)]
    [InlineData(int.MaxValue, 256)]
    [InlineData(256, int.MaxValue)]
    [InlineData(100_000, 100_000)]
    public void InvalidImageDimensionsAreMisses(int width, int height)
    {
        File.WriteAllBytes(CachePath, Cache(width: width, height: height));
        Assert.Null(MapReference.Load(CachePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidScalingIsAMiss(double scale)
    {
        File.WriteAllBytes(CachePath, Cache(scale: scale));
        Assert.Null(MapReference.Load(CachePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("synthetic\0map")]
    public void EmptyOrControlCharacterMapIdsAreMisses(string mapId)
    {
        File.WriteAllBytes(CachePath, Cache(mapId: mapId));
        Assert.Null(MapReference.Load(CachePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOrOversizedStringPrefixesAreBounded(bool mapId)
    {
        foreach (var prefix in new byte[][]
        {
            [0xff, 0xff, 0xff, 0xff, 0x07], // Int32.MaxValue bytes, not a string to allocate.
            [0xff, 0xff, 0xff, 0xff, 0xff], // Invalid 7-bit length.
            [1, 0xff], // Invalid UTF-8, not a replacement-character identifier.
        })
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (mapId) writer.Write("SCMAPREF3");
                writer.Write(prefix);
            }
            File.WriteAllBytes(CachePath, stream.ToArray());
            Assert.Null(MapReference.Load(CachePath));
        }
        File.WriteAllBytes(CachePath, Cache(mapId: new string('m', 257)));
        Assert.Null(MapReference.Load(CachePath));
    }

    [Theory]
    [InlineData(float.NaN, 1)]
    [InlineData(float.PositiveInfinity, 1)]
    [InlineData(-1, 1)]
    [InlineData(256, 1)]
    [InlineData(1, float.NaN)]
    [InlineData(1, 256)]
    public void InvalidKeypointsAreMisses(float x, float y)
    {
        File.WriteAllBytes(CachePath, Cache(firstX: x, firstY: y));
        Assert.Null(MapReference.Load(CachePath));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidDescriptorsNeverReachTheNativeMatcher(float descriptor)
    {
        File.WriteAllBytes(CachePath, Cache(firstDescriptor: descriptor));
        Assert.Null(MapReference.Load(CachePath));
    }

    private static byte[] Cache(string mapId = "synthetic-map", int width = 256, int height = 256,
        double scale = 1, int count = 32, float firstX = 1, float firstY = 1, float firstDescriptor = 1)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("SCMAPREF3"); writer.Write(mapId); writer.Write(width); writer.Write(height);
            writer.Write(scale); writer.Write(count);
            // Deliberately small payload even for a corrupt huge count: the loader must inspect length first.
            for (var i = 0; i < 32; i++) { writer.Write(i == 0 ? firstX : i); writer.Write(i == 0 ? firstY : i); }
            for (var i = 0; i < 32 * MapReference.DescriptorLength; i++) writer.Write(i == 0 ? firstDescriptor : (float)(i % 128));
        }
        return stream.ToArray();
    }
}
