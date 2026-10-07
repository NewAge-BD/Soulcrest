namespace Soulcrest.Core.Network;

/// <summary>
/// Splits the server → client byte stream of Aion 2 into messages (docs/NETWORK_LOOT.md): each message
/// starts with a varint L, its total length is L − 4 + (bytes of the varint); then a 2-byte type.
/// "ffff" bundles (u32 LE size + LZ4 block) are unpacked into their messages. Capture can start mid-
/// stream and lose segments, so the reader finds the message boundaries again by requiring a chain
/// of well-formed messages.
/// </summary>
public sealed class GameMessageStream
{
    private const int MaxMessage = 1 << 20;
    private const int ChainForSync = 4;
    private readonly List<byte> _buffer = [];
    private bool _synced;

    /// <summary>Messages lost to resynchronisation (diagnosis).</summary>
    public int Resyncs { get; private set; }

    /// <summary>A gap in the captured stream: the next bytes do not continue the current message.</summary>
    public void Break()
    {
        _buffer.Clear();
        _synced = false;
    }

    /// <summary>Appends stream bytes and returns the complete messages (type + body, without the length).</summary>
    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            _buffer.Add(b);
        var messages = new List<byte[]>();
        var bytes = _buffer.ToArray();
        var pos = 0;
        if (!_synced)
        {
            var start = FindSync(bytes);
            if (start < 0)
            {
                // Keep only the tail that could still begin a chain.
                if (_buffer.Count > MaxMessage * 2)
                    _buffer.RemoveRange(0, _buffer.Count - MaxMessage);
                return messages;
            }
            if (start > 0)
                Resyncs++;
            pos = start;
            _synced = true;
        }
        while (Frame(bytes, pos) is { } frame)
        {
            if (frame.End > bytes.Length)
                break;
            Expand(bytes.AsSpan(frame.BodyStart, frame.End - frame.BodyStart), messages);
            pos = frame.End;
        }
        if (Frame(bytes, pos) is null && pos < bytes.Length && !CouldStartFrame(bytes, pos))
        {
            // Garbage where a message should start: lost sync.
            _synced = false;
        }
        _buffer.RemoveRange(0, pos);
        return messages;
    }

    private static void Expand(ReadOnlySpan<byte> message, List<byte[]> into)
    {
        if (message.Length >= 6 && message[0] == 0xff && message[1] == 0xff)
        {
            var size = message[2] | (message[3] << 8) | (message[4] << 16) | (message[5] << 24);
            if (Lz4Block.Decode(message[6..], size) is { } inner)
            {
                var pos = 0;
                while (Frame(inner, pos) is { } frame && frame.End <= inner.Length)
                {
                    Expand(inner.AsSpan(frame.BodyStart, frame.End - frame.BodyStart), into);
                    pos = frame.End;
                }
            }
            return;
        }
        into.Add(message.ToArray());
    }

    private readonly record struct FrameInfo(int BodyStart, int End);

    /// <summary>The message starting at <paramref name="pos"/>, if its header is complete and plausible.</summary>
    private static FrameInfo? Frame(byte[] bytes, int pos)
    {
        int value = 0, shift = 0, n = 0;
        while (true)
        {
            if (pos + n >= bytes.Length || n >= 4)
                return null;
            var b = bytes[pos + n];
            value |= (b & 0x7f) << shift;
            shift += 7;
            n++;
            if (b < 0x80)
                break;
        }
        var size = value - 4 + n;
        if (size < n + 2 || size > MaxMessage)
            return null;
        return new FrameInfo(pos + n, pos + size);
    }

    private static bool CouldStartFrame(byte[] bytes, int pos)
    {
        // Header not complete yet (varint cut off) or a complete header waiting for its body.
        for (var n = 0; n < 4; n++)
        {
            if (pos + n >= bytes.Length)
                return true;
            if (bytes[pos + n] < 0x80)
                return Frame(bytes, pos) is not null;
        }
        return false;
    }

    /// <summary>
    /// First position from which at least <see cref="ChainForSync"/> complete messages follow each other
    /// without a break up to the end of the data (only a message still arriving may stick out). Random
    /// bytes often form a few plausible headers, but hardly a chain that ends exactly with the data.
    /// </summary>
    private static int FindSync(byte[] bytes)
    {
        const int MaxSyncMessage = 64 * 1024;
        for (var start = 0; start < bytes.Length; start++)
        {
            var pos = start;
            var chain = 0;
            var valid = false;
            while (true)
            {
                if (pos == bytes.Length)
                {
                    valid = true;
                    break;
                }
                if (Frame(bytes, pos) is not { } frame)
                {
                    valid = CouldStartFrame(bytes, pos) && Frame(bytes, pos) is null && bytes.Length - pos < 4;
                    break;
                }
                if (frame.End - pos > MaxSyncMessage)
                    break;
                if (frame.End > bytes.Length)
                {
                    valid = true; // the last message is still arriving
                    break;
                }
                pos = frame.End;
                chain++;
            }
            if (valid && chain >= ChainForSync)
                return start;
        }
        return -1;
    }
}
