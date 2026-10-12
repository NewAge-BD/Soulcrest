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
    private const int MaxBundleDepth = 8;
    private const int MaxExpandedBytes = 16 * 1024 * 1024;
    private const int MaxDecodedMessages = 65536;
    private readonly List<byte> _buffer = [];
    private readonly bool _requireVerifiedSync;
    private int _verifiedScan;
    private bool _synced;

    /// <summary>
    /// Live progress requires a validated bundle, own-character message or exact server clock to
    /// establish framing. The default retains the generic chain mode for transport-only consumers.
    /// </summary>
    public GameMessageStream(bool requireVerifiedSync = false) => _requireVerifiedSync = requireVerifiedSync;

    /// <summary>Messages lost to resynchronisation (diagnosis).</summary>
    public int Resyncs { get; private set; }

    /// <summary>
    /// Continuity lost while decoding, reported immediately even if resynchronisation needs more bytes.
    /// The caller must invalidate any live identity/baseline before processing subsequent messages.
    /// </summary>
    public long ContinuityBreaks { get; private set; }

    /// <summary>A gap in the captured stream: the next bytes do not continue the current message.</summary>
    public void Break()
    {
        _buffer.Clear();
        _synced = false;
        _verifiedScan = 0;
    }

    /// <summary>
    /// Appends stream bytes and returns complete messages (type + body, without the length).
    /// A damaged complete frame/bundle discards this append's entire result and increments ContinuityBreaks.
    /// </summary>
    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            _buffer.Add(b);
        var messages = new List<byte[]>();
        var bytes = _buffer.ToArray();
        var pos = 0;
        if (!_synced)
        {
            var start = _requireVerifiedSync ? FindVerifiedSync(bytes) : FindSync(bytes);
            if (start < 0)
            {
                // Keep only the tail that could still begin a chain.
                if (_buffer.Count > MaxMessage * 2)
                {
                    _verifiedScan = Math.Max(0, _verifiedScan - (_buffer.Count - MaxMessage));
                    _buffer.RemoveRange(0, _buffer.Count - MaxMessage);
                    ContinuityBreaks++;
                }
                return messages;
            }
            if (start > 0)
            {
                Resyncs++;
                ContinuityBreaks++;
            }
            pos = start;
            _synced = true;
        }
        var expandedBytes = 0;
        while (Frame(bytes, pos) is { } frame)
        {
            if (frame.End > bytes.Length)
                break;
            if (!Expand(bytes.AsSpan(frame.BodyStart, frame.End - frame.BodyStart), messages, 0, ref expandedBytes))
            {
                // A complete outer frame with a damaged bundle lost messages. Do not deliver a
                // partial batch (it could contain identity or gains before the damaged part).
                ContinuityBreaks++;
                _synced = false;
                _verifiedScan = 0;
                _buffer.RemoveRange(0, frame.End);
                return [];
            }
            pos = frame.End;
        }
        if (Frame(bytes, pos) is null && pos < bytes.Length && !CouldStartFrame(bytes, pos))
        {
            // Garbage where a message should start: lost sync.
            _synced = false;
            _verifiedScan = 0;
            ContinuityBreaks++;
            messages.Clear();
        }
        _buffer.RemoveRange(0, pos);
        return messages;
    }

    private static bool Expand(ReadOnlySpan<byte> message, List<byte[]> into, int depth, ref int expandedBytes)
    {
        if (message.Length >= 2 && message[0] == 0xff && message[1] == 0xff)
        {
            if (message.Length < 6 || depth >= MaxBundleDepth) return false;
            var size = message[2] | (message[3] << 8) | (message[4] << 16) | (message[5] << 24);
            if (size < 0 || size > MaxExpandedBytes - expandedBytes) return false;
            expandedBytes += size;
            if (Lz4Block.Decode(message[6..], size) is not { } inner) return false;
            var pos = 0;
            while (pos < inner.Length)
            {
                if (Frame(inner, pos) is not { } frame || frame.End > inner.Length) return false;
                if (!Expand(inner.AsSpan(frame.BodyStart, frame.End - frame.BodyStart), into, depth + 1, ref expandedBytes)) return false;
                pos = frame.End;
            }
            return true;
        }
        if (into.Count >= MaxDecodedMessages) return false;
        into.Add(message.ToArray());
        return true;
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
    /// An arbitrary four-frame chain can occur inside compressed bytes. Keep unconfirmed bytes until
    /// a complete protocol anchor is validated; in particular, never scan inside an arriving anchor.
    /// The cursor revisits only a partial candidate or the last five bytes after an unsuccessful scan.
    /// </summary>
    private int FindVerifiedSync(byte[] bytes)
    {
        for (var start = _verifiedScan; start < bytes.Length; start++)
        {
            if (Frame(bytes, start) is not { } frame || frame.BodyStart + 2 > bytes.Length) continue;
            var opcode = bytes[frame.BodyStart] | bytes[frame.BodyStart + 1] << 8;
            if (opcode is not (0xffff or 0x3633 or 0x3600)) continue;
            if (opcode == 0x3600 && frame.End - frame.BodyStart != 10) continue;
            if (frame.End > bytes.Length) { _verifiedScan = start; return -1; }
            var message = bytes.AsSpan(frame.BodyStart, frame.End - frame.BodyStart);
            if (opcode == 0x3600 && BossSpawnMessage.ReadServerClock(message) is not null)
            { _verifiedScan = 0; return start; }
            if (opcode == 0x3633 && OwnIdentityMessage.TryParse(message) is not null)
            { _verifiedScan = 0; return start; }
            if (opcode == 0xffff)
            {
                var inner = new List<byte[]>();
                var expanded = 0;
                if (Expand(message, inner, 0, ref expanded) &&
                    (inner.Count >= ChainForSync || inner.Any(m => OwnIdentityMessage.TryParse(m) is not null)))
                { _verifiedScan = 0; return start; }
            }
        }
        // A four-byte length and two-byte opcode can straddle the next TCP segment.
        _verifiedScan = Math.Max(0, bytes.Length - 5);
        return -1;
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
