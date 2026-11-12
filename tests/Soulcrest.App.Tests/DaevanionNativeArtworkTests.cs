using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionNativeArtworkTests
{
    [Theory]
    [InlineData("stat-background", 112, 112, "0a3746e64f6ac01bc6537fae82c6fcf0e524553c33d36294c8f6562de3886c5a")]
    [InlineData("dot", 34, 34, "3e2f40ade06a7390b4f8e27a4b13c57c7240c96816a2d2b4e0998fa93f918953")]
    [InlineData("button", 308, 66, "d1e1104ada51b1fbb5602333ae182225c066f86983e8e9878a0356747c3f15a9")]
    [InlineData("checkbox-on", 42, 42, "cef42428fd162082d7240a037fe246d542956147b11b988a9bba4d0d254aa877")]
    [InlineData("checkbox-off", 42, 42, "4d147b45ab187d4b5461bb534685dcead656d7a90066c5bd7bbb28d01997df68")]
    public void ProductionSpritesAreEmbeddedAndUnmodified(string name, int width, int height, string sha256)
    {
        using var resource = typeof(DaevanionOverlayRenderer).Assembly
            .GetManifestResourceStream($"Soulcrest.Daevanion.{name}.png");
        Assert.NotNull(resource);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(resource)));
        resource.Position = 0;
        using var image = new Bitmap(resource);
        Assert.Equal(new Size(width, height), image.Size);
    }

    [Fact]
    public void SharedSpritesPaintIdenticallyAcrossParallelRenderers()
    {
        var sprites = new[] { DaevanionNativeArtwork.StatBackground, DaevanionNativeArtwork.Dot,
            DaevanionNativeArtwork.Button, DaevanionNativeArtwork.CheckboxOn, DaevanionNativeArtwork.CheckboxOff };
        var expected = sprites.Select(RenderHash).ToArray();
        Parallel.For(0, 60, index => Assert.Equal(expected[index % sprites.Length], RenderHash(sprites[index % sprites.Length])));
    }

    private static string RenderHash(DaevanionNativeArtwork.NativeImage sprite)
    {
        using var image = new Bitmap(sprite.Width, sprite.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(image)) sprite.Paint(graphics, new Rectangle(Point.Empty, image.Size));
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
