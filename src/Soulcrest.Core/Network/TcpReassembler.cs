namespace Soulcrest.Core.Network;

/// <summary>
/// Puts the TCP segments of one direction back in order (by sequence number). Retransmissions and
/// overlaps are trimmed, early segments wait for the gap to close; a gap that stays open (lost in
/// capture) is skipped and reported, so the message reader can resynchronise.
/// </summary>
public sealed class TcpReassembler
{
    private const int MaxPending = 256;
    private readonly Dictionary<uint, byte[]> _pending = [];
    private uint? _next;

    /// <summary>Ordered bytes, or a gap (Gap = true, then the data after it).</summary>
    public readonly record struct Chunk(byte[] Data, bool Gap);

    public IReadOnlyList<Chunk> Add(uint sequence, ReadOnlySpan<byte> payload)
    {
        var output = new List<Chunk>();
        if (payload.Length == 0)
            return output;
        if (_next is null)
        {
            // First segment seen: the stream starts here (mid-message; the reader resynchronises).
            _next = sequence;
            output.Add(new Chunk([], true));
        }
        var offset = Distance(sequence, _next.Value);
        if (offset < 0)
        {
            // Retransmission or overlap: keep only what is new.
            if (-(long)offset >= payload.Length)
                return output;
            payload = payload[-offset..];
            sequence = _next.Value;
            offset = 0;
        }
        if (offset > 0)
        {
            if (!_pending.TryGetValue(sequence, out var existing) || payload.Length > existing.Length)
            {
                var retained = payload.ToArray();
                // A retransmission can cover a longer or shorter part of the same TCP interval.
                // Keep all received bytes and retain the first copy of the shared prefix.
                existing?.CopyTo(retained, 0);
                _pending[sequence] = retained;
            }
            if (_pending.Count > MaxPending)
            {
                // The missing segment will not come (dropped by the capture): skip the gap.
                var first = FirstPending();
                _next = first.Key;
                output.Add(new Chunk([], true));
                Drain(output);
            }
            return output;
        }
        output.Add(new Chunk(payload.ToArray(), false));
        _next = unchecked(sequence + (uint)payload.Length);
        Drain(output);
        return output;
    }

    private void Drain(List<Chunk> output)
    {
        while (_pending.Count > 0)
        {
            var (sequence, data) = FirstPending();
            var offset = Distance(sequence, _next!.Value);
            if (offset > 0)
                break;
            _pending.Remove(sequence);
            if (-(long)offset >= data.Length)
                continue;
            var fresh = data[-offset..];
            output.Add(new Chunk(fresh, false));
            _next = unchecked(_next.Value + (uint)fresh.Length);
        }
    }

    // uint's numeric order changes at wrap-around; TCP order is relative to the next byte.
    // Scan the bounded queue using signed distances, including intervals that cross zero.
    private KeyValuePair<uint, byte[]> FirstPending() =>
        _pending.OrderBy(p => Distance(p.Key, _next!.Value)).First();

    private static int Distance(uint sequence, uint next) => unchecked((int)(sequence - next));
}
