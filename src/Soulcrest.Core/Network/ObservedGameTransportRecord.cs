using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Soulcrest.Core.Network;

public enum ObservedGameTransportRecordKind { Unknown, Data, Control }

/// <summary>
/// An observed outer-record shape, not game ownership, identity, encounter, or event authority.
/// The opaque context and counter are never exposed. This reader is for an explicitly selected
/// offline transport scope; it does not detect, strip, resynchronize, or enable live capture.
/// </summary>
public sealed record ObservedGameTransportRecord(ObservedGameTransportRecordKind Kind,
    ReadOnlyMemory<byte> Body)
{
    public int BodyOffset { get; private init; }
    public int BodyLength => Body.Length;
    public byte RawKind { get; private init; }

    /// <summary>
    /// Reads one complete record. Confirmed data fields yield a slice of the caller's memory.
    /// Confirmed controls expose no body. Remaining kinds stay unknown. Neither kind implies a
    /// game, encounter or connection lifecycle event.
    /// Malformed or unsupported data forms return false without any partial body.
    /// </summary>
    public static bool TryRead(ReadOnlyMemory<byte> record,
        [NotNullWhen(true)] out ObservedGameTransportRecord? observation)
    {
        observation = null;
        var raw = record.Span;
        if (raw.Length < 31 || raw[0] != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(raw[1..]) + 4 != raw.Length)
            return false;

        if (raw[7] is 10 or 11)
        {
            if (raw[7] == 11 ? raw.Length != 31 : !TryReadControl(raw)) return false;
            observation = new(ObservedGameTransportRecordKind.Control, ReadOnlyMemory<byte>.Empty)
            { RawKind = raw[7] };
            return true;
        }

        if (raw[7] != 9)
        {
            observation = new(ObservedGameTransportRecordKind.Unknown, ReadOnlyMemory<byte>.Empty)
            { RawKind = raw[7] };
            return true;
        }

        // Private capture evidence: both Krao runs, all early/late directed data-record scopes.
        // Only static field tags are retained; bytes 3..30 except RawKind at 7 and the counter
        // remain opaque. None of this context joins an own identity, group, map or encounter.
        if (raw.Length < 43 || raw[31] != 5 || raw[32] != 34) return false;
        var counterWidth = raw[33];
        if (counterWidth is not (1 or 2)) return false;
        var position = 34 + counterWidth;
        if (counterWidth == 2 && BinaryPrimitives.ReadUInt16LittleEndian(raw[34..]) <= byte.MaxValue)
            return false;

        var lengthWidth = raw[position] switch { 5 => 1, 9 => 2, _ => 0 };
        if (lengthWidth == 0 || raw[position + 1] != 35) return false;
        position += 2;
        var bodyLength = lengthWidth == 1 ? raw[position] :
            BinaryPrimitives.ReadUInt16LittleEndian(raw[position..]);
        if (bodyLength == 0 || lengthWidth == 2 && bodyLength <= byte.MaxValue) return false;
        position += lengthWidth;
        if (bodyLength != raw.Length - position - 4 ||
            raw[^4] != 5 || raw[^3] != 37 || raw[^2] != 1 || raw[^1] != 1) return false;

        observation = new(ObservedGameTransportRecordKind.Data, record.Slice(position, bodyLength))
        { BodyOffset = position, RawKind = raw[7] };
        return true;
    }

    private static bool TryReadControl(ReadOnlySpan<byte> record)
    {
        var position = 31;
        if (position + 2 > record.Length || record[position] != 5) return false;
        var firstKey = record[position + 1];
        if (firstKey is not (43 or 44) || !ReadControlField(record, ref position, firstKey)) return false;
        if (firstKey == 44 && !ReadControlField(record, ref position, 43)) return false;
        return position == record.Length;
    }

    private static bool ReadControlField(ReadOnlySpan<byte> record, ref int position, byte key)
    {
        if (record.Length - position < 3 || record[position++] != 5 || record[position++] != key)
            return false;
        var width = record[position++];
        if (width is not (1 or 2) || record.Length - position < width ||
            width == 2 && record[position + 1] == 0) return false;
        position += width;
        return true;
    }
}
