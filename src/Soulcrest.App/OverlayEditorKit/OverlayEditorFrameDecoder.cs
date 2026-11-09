using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Soulcrest.App.OverlayEditorKit;

internal static class OverlayEditorFrameDecoder
{
    private const string InvalidFrameMessage = "The editor frame response is invalid.";

    internal static Bitmap Decode(string response, Size expected)
    {
        if (expected.Width <= 0 || expected.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(expected), "Expected dimensions must be positive.");
        long maximumBytes, maximumBase64Length;
        try
        {
            maximumBytes = checked((long)expected.Width * expected.Height * 8 + 65_536);
            maximumBase64Length = checked((maximumBytes + 2) / 3 * 4);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(expected), "Expected dimensions exceed the frame size arithmetic.");
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
                throw InvalidFrame();
            var encoded = data.GetString();
            if (string.IsNullOrEmpty(encoded) || encoded.Length > maximumBase64Length) throw InvalidFrame();
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.LongLength > maximumBytes || bytes.Length < 33
                || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13
                || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) != (uint)expected.Width
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) != (uint)expected.Height)
                throw InvalidFrame();

            using var stream = new MemoryStream(bytes, writable: false);
            using var decoded = new Bitmap(stream);
            if (decoded.Size != expected) throw InvalidFrame();
            var result = new Bitmap(expected.Width, expected.Height, PixelFormat.Format32bppPArgb);
            try
            {
                using var graphics = Graphics.FromImage(result);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(decoded, Point.Empty);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException
            or ExternalException or OutOfMemoryException)
        {
            throw InvalidFrame(error);
        }
    }

    private static InvalidOperationException InvalidFrame(Exception? inner = null) => new(InvalidFrameMessage, inner);
}
