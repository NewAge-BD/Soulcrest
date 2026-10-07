using System.Text;

namespace Soulcrest.Core.Network;

/// <summary>
/// The server's message <c>33 36</c> on entering the world: sent once after every loading screen (login,
/// character change, teleport) and only for the own character (user capture 2026-10-06: four loading screens,
/// two characters; other players never appear in it). Four varints, then the name: length byte + UTF-8.
/// Only the name is read (docs/SAFETY.md: server → client, own character only).
/// </summary>
public static class CharacterMessage
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string? TryParseName(ReadOnlySpan<byte> message)
    {
        // Seen at 1718-2300 bytes; the name sits right at the start.
        if (message.Length < 600 || message[0] != 0x33 || message[1] != 0x36)
            return null;
        var position = 2;
        for (var i = 0; i < 4; i++)
        {
            if (!SkipVarint(message, ref position))
                return null;
        }
        if (position >= message.Length)
            return null;
        int length = message[position++];
        if (length is < 2 or > 32 || position + length > message.Length)
            return null;
        string name;
        try
        {
            name = Strict.GetString(message.Slice(position, length));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        return name.All(char.IsLetterOrDigit) ? name : null;
    }

    private static bool SkipVarint(ReadOnlySpan<byte> data, ref int position)
    {
        for (var shift = 0; shift < 35 && position < data.Length; shift += 7)
        {
            if ((data[position++] & 0x80) == 0)
                return true;
        }
        return false;
    }
}
