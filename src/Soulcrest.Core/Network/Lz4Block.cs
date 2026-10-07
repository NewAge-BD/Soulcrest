namespace Soulcrest.Core.Network;

/// <summary>
/// LZ4 block decoder (no frame header): the "ffff" bundles of the server stream carry their messages
/// compressed this way (docs/NETWORK_LOOT.md). Compression only, nothing is decrypted.
/// </summary>
public static class Lz4Block
{
    /// <summary>Decodes <paramref name="source"/> into exactly <paramref name="size"/> bytes, or null if it does not fit.</summary>
    public static byte[]? Decode(ReadOnlySpan<byte> source, int size)
    {
        if (size < 0 || size > 16 * 1024 * 1024)
            return null;
        var target = new byte[size];
        int i = 0, o = 0;
        while (i < source.Length)
        {
            var token = source[i++];
            var literals = token >> 4;
            if (literals == 15)
            {
                byte b;
                do
                {
                    if (i >= source.Length)
                        return null;
                    b = source[i++];
                    literals += b;
                }
                while (b == 255);
            }
            if (i + literals > source.Length || o + literals > size)
                return null;
            source.Slice(i, literals).CopyTo(target.AsSpan(o));
            i += literals;
            o += literals;
            if (i >= source.Length)
                break; // the last sequence has literals only
            if (i + 2 > source.Length)
                return null;
            var offset = source[i] | (source[i + 1] << 8);
            i += 2;
            var length = token & 15;
            if (length == 15)
            {
                byte b;
                do
                {
                    if (i >= source.Length)
                        return null;
                    b = source[i++];
                    length += b;
                }
                while (b == 255);
            }
            length += 4;
            var from = o - offset;
            if (offset == 0 || from < 0 || o + length > size)
                return null;
            // Byte by byte: the copy may overlap itself (offset < length repeats a pattern).
            for (var k = 0; k < length; k++)
                target[o++] = target[from + k];
        }
        return o == size ? target : null;
    }
}
