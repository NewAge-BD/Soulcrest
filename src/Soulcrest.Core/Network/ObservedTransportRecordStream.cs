using System.Buffers.Binary;

namespace Soulcrest.Core.Network;

/// <summary>
/// Reads only the outer record boundaries observed in the two local Krao captures (2026-10-18).
/// A record has type byte 1 and a little-endian UInt16 at offset 1; its complete size is that value
/// plus four. Its internal content remains opaque. This reader neither removes a game header nor
/// establishes game-message, identity or combat-event authority, and is not automatically selected
/// for live traffic.
/// </summary>
public sealed class ObservedTransportRecordStream
{
    public const int MinimumRecordBytes = 31;
    public const int MaximumRecordBytes = ushort.MaxValue + 4;
    private const int LengthHeaderBytes = 3;
    private readonly byte[] _buffer = new byte[MaximumRecordBytes];
    private int _buffered;
    private int _expected;

    /// <summary>An invalid type or size blocks further input until the caller explicitly calls Break.</summary>
    public bool IsBlocked { get; private set; }

    /// <summary>Bytes retained for one incomplete record; never exceeds MaximumRecordBytes.</summary>
    public int BufferedBytes => _buffered;

    /// <summary>
    /// Discards an incomplete record and clears a blocked state. The caller must independently know
    /// that subsequent input begins at a record boundary; this operation does not find that boundary.
    /// </summary>
    public void Break()
    {
        Array.Clear(_buffer);
        _buffered = 0;
        _expected = 0;
        IsBlocked = false;
    }

    /// <summary>
    /// Appends ordered, contiguous bytes and returns independent copies of complete records,
    /// including their full headers. At most one bounded incomplete record is retained internally.
    /// An invalid header discards this append's entire result and blocks further input; no bytes are
    /// skipped or searched for a later apparent boundary. Returned records carry no game semantics.
    /// </summary>
    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> data)
    {
        if (IsBlocked || data.IsEmpty)
            return [];

        var records = new List<byte[]>();
        while (!data.IsEmpty)
        {
            if (_buffered == 0 && data[0] != 1)
                return Block();

            if (_buffered < LengthHeaderBytes)
            {
                var headerBytes = Math.Min(LengthHeaderBytes - _buffered, data.Length);
                data[..headerBytes].CopyTo(_buffer.AsSpan(_buffered));
                _buffered += headerBytes;
                data = data[headerBytes..];
                if (_buffered < LengthHeaderBytes)
                    break;

                _expected = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(1, 2)) + 4;
                // The 31-byte minimum belongs to this explicit observed profile. Do not accept
                // smaller, unobserved control records merely because their UInt16 is representable.
                if (_expected < MinimumRecordBytes || _expected > MaximumRecordBytes)
                    return Block();
            }

            var bodyBytes = Math.Min(_expected - _buffered, data.Length);
            data[..bodyBytes].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += bodyBytes;
            data = data[bodyBytes..];
            if (_buffered == _expected)
            {
                records.Add(_buffer.AsSpan(0, _expected).ToArray());
                _buffered = 0;
                _expected = 0;
            }
        }
        return records;
    }

    private IReadOnlyList<byte[]> Block()
    {
        Array.Clear(_buffer);
        _buffered = 0;
        _expected = 0;
        IsBlocked = true;
        return [];
    }
}
