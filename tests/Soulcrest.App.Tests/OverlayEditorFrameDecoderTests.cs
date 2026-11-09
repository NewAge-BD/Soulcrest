using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorFrameDecoderTests
{
    [Fact]
    public void PngAlphaAndColorsSurviveTheIndependentPremultipliedBitmap()
    {
        var colors = new[] { Color.FromArgb(0, 0, 0, 0), Color.FromArgb(128, 100, 150, 200), Color.FromArgb(255, 17, 43, 101) };
        using var source = new Bitmap(3, 1, PixelFormat.Format32bppArgb);
        for (var x = 0; x < colors.Length; x++) source.SetPixel(x, 0, colors[x]);
        using var result = OverlayEditorFrameDecoder.Decode(Response(Png(source)), source.Size);
        source.Dispose();
        Assert.Equal(PixelFormat.Format32bppPArgb, result.PixelFormat);
        Assert.Equal(new Size(3, 1), result.Size);
        for (var x = 0; x < colors.Length; x++)
        {
            var actual = result.GetPixel(x, 0);
            Assert.Equal(colors[x].A, actual.A);
            Assert.InRange(Math.Abs(colors[x].R - actual.R), 0, 1);
            Assert.InRange(Math.Abs(colors[x].G - actual.G), 0, 1);
            Assert.InRange(Math.Abs(colors[x].B - actual.B), 0, 1);
        }
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void AValidPngWithDifferentDimensionsIsRejected(int width, int height)
    {
        using var source = new Bitmap(1, 1);
        Invalid(Response(Png(source)), new Size(width, height));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":17}")]
    [InlineData("{\"data\":\"\"}")]
    [InlineData("{\"data\":\"@@@invalid@@@\"}")]
    public void InvalidJsonDataOrBase64IsRejectedWithAStableInternalMessage(string response) => Invalid(response, new(1, 1));

    [Fact]
    public void ADecodedNonPngPayloadIsRejected() => Invalid(Response(Encoding.UTF8.GetBytes("not a PNG image")), new(1, 1));

    [Fact]
    public void AClaimedHugeIhdrIsRejectedBeforeAnImageCanBeAllocated()
    {
        var png = Header(1_000_000, 1_000_000);
        Invalid(Response(png), new(1, 1));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(14)]
    public void AnIhdrWithTheWrongChunkLengthIsRejected(int length)
    {
        var png = Header(1, 1);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8, 4), (uint)length);
        Invalid(Response(png), new(1, 1));
    }

    [Fact]
    public void AMatchingHeaderWithoutValidPngImageDataIsRejected() => Invalid(Response(Header(1, 1)), new(1, 1));

    [Fact]
    public void EncodedPayloadCannotExceedTheExpectedPixelBudget() => Invalid(Response(new byte[65_545]), new(1, 1));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void ExpectedDimensionsMustBePositive(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayEditorFrameDecoder.Decode("{}", new(width, height)));

    private static void Invalid(string response, Size expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => OverlayEditorFrameDecoder.Decode(response, expected));
        Assert.Equal("The editor frame response is invalid.", error.Message);
    }

    private static string Response(byte[] bytes) => JsonSerializer.Serialize(new { data = Convert.ToBase64String(bytes) });
    private static byte[] Png(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] Header(uint width, uint height)
    {
        var bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }
}
