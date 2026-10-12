using System.Buffers.Binary;
using System.Text;

namespace Soulcrest.Core.Network;

/// <summary>Bounded little-endian reader. Boolean bits survive intervening scalar and list reads.</summary>
internal ref struct ProgressPacketReader(ReadOnlySpan<byte> data)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;
    private byte _boolByte;
    private int _boolBit = 8;
    public readonly int Remaining => _data.Length - _position;
    public readonly int Position => _position;

    public ReadOnlySpan<byte> Take(int length)
    {
        if (length < 0 || length > Remaining) throw new InvalidDataException("Truncated progress packet.");
        var result = _data.Slice(_position, length);
        _position += length;
        return result;
    }

    public void Skip(int length) => Take(length);
    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public uint V32()
    {
        uint value = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = U8();
            if (i == 4 && b > 15) throw new InvalidDataException("Progress varint overflow.");
            value |= (uint)(b & 127) << (i * 7);
            if (b >= 128) continue;
            if (i > 0 && b == 0) throw new InvalidDataException("Noncanonical progress varint.");
            return value;
        }
        throw new InvalidDataException("Unterminated progress varint.");
    }

    public int Vi32() => unchecked((int)V32());

    public ulong V64()
    {
        ulong value = 0;
        for (var i = 0; i < 10; i++)
        {
            var b = U8();
            if (i == 9 && b > 1) throw new InvalidDataException("Progress varint overflow.");
            value |= (ulong)(b & 127) << (i * 7);
            if (b >= 128) continue;
            if (i > 0 && b == 0) throw new InvalidDataException("Noncanonical progress varint.");
            return value;
        }
        throw new InvalidDataException("Unterminated progress varint.");
    }

    public bool Bool()
    {
        if (_boolBit == 8) { _boolByte = U8(); _boolBit = 0; }
        return (_boolByte & (1 << _boolBit++)) != 0;
    }

    public int Count(int minimumBytes = 1, int maximum = 10000)
    {
        var count = V32();
        if (count > maximum || (long)count * minimumBytes > Remaining)
            throw new InvalidDataException("Progress count exceeds packet bounds.");
        return (int)count;
    }

    public string String(int maximumBytes = 1024)
    {
        var length = Count(1, maximumBytes);
        return StrictUtf8.GetString(Take(length));
    }

    public void SkipString(int maximumBytes = 65536) => Skip(Count(1, maximumBytes));

    public readonly void Exact()
    {
        if (Remaining != 0) throw new InvalidDataException("Trailing progress bytes.");
    }
}
