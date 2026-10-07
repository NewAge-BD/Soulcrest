using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Server stream parsing for the network loot source (docs/NETWORK_LOOT.md).</summary>
public sealed class NetworkLootTests
{
    // Real loot messages of a capture 2026-10-03: Kailin + Young Kailin, and Decomposed Lupyllini.
    private static readonly byte[] KailinPair = Convert.FromHexString("0d9002f7030000010000006004000001000000");
    private static readonly byte[] Lupyllini = Convert.FromHexString("0d9001b204000001000000");
    // Ordinary messages of the same capture (types 0036 and 1b20).
    private static readonly byte[] Ping = Convert.FromHexString("003615513c03a1010000");
    private static readonly byte[] Other = Convert.FromHexString("2028370908070306132547386803c85f55f2457b9eec435b");

    /// <summary>Frames a message body like the server: varint L with total length = L − 4 + varint bytes.</summary>
    private static byte[] Framed(byte[] body)
    {
        for (var n = 1; n <= 3; n++)
        {
            var value = body.Length + 4;
            var varint = new List<byte>();
            var v = value;
            do
            {
                var b = (byte)(v & 0x7f);
                v >>= 7;
                varint.Add(v > 0 ? (byte)(b | 0x80) : b);
            }
            while (v > 0);
            if (varint.Count == n)
                return [.. varint, .. body];
        }
        throw new InvalidOperationException();
    }

    /// <summary>An "ffff" bundle with a literal-only LZ4 block.</summary>
    private static byte[] Bundle(params byte[][] messages)
    {
        var inner = messages.SelectMany(Framed).ToArray();
        var block = new List<byte>();
        var length = inner.Length;
        block.Add(0xF0);
        var rest = length - 15;
        while (rest >= 255)
        {
            block.Add(255);
            rest -= 255;
        }
        block.Add((byte)rest);
        block.AddRange(inner);
        return [0xff, 0xff, .. BitConverter.GetBytes(length), .. block];
    }

    [Fact]
    public void LootMessageReadsPetIdsAndSouls()
    {
        Assert.Equal([new SoulGain(1015, 1), new SoulGain(1120, 1)], LootMessage.TryParse(KailinPair));
        Assert.Equal([new SoulGain(1202, 1)], LootMessage.TryParse(Lupyllini));
        Assert.Null(LootMessage.TryParse(Ping));
    }

    [Fact]
    public void PetIdsAreTheIdsOfTheCapturedLootMessages()
    {
        Assert.Equal("Kailin", PetIdTable.Find(1015)?.En);
        Assert.Equal("Young Kailin", PetIdTable.Find(1120)?.En);
        Assert.Equal("decomposed-lupyllini", PetIdTable.Find(1202)?.CatalogId);
        Assert.Equal(207, PetIdTable.All.Count);
    }

    [Fact]
    public void Lz4DecodesLiteralsAndOverlappingCopies()
    {
        // "abc" then copy 6 bytes from 3 back: "abcabcabc".
        byte[] block = [0x32, (byte)'a', (byte)'b', (byte)'c', 0x03, 0x00, 0x00];
        Assert.Equal("abcabcabc"u8.ToArray(), Lz4Block.Decode(block, 9));
        Assert.Null(Lz4Block.Decode(block, 10));
    }

    [Fact]
    public void StreamSplitsMessagesAcrossSegmentsAndUnpacksBundles()
    {
        var stream = new GameMessageStream();
        var bytes = new[] { Framed(Ping), Framed(Other), Framed(Ping), Framed(Other), Framed(KailinPair), Framed(Bundle(Ping, Lupyllini)), Framed(Other) }
            .SelectMany(b => b).ToArray();
        var messages = new List<byte[]>();
        // Odd segment sizes, as TCP delivers them.
        for (var i = 0; i < bytes.Length; i += 7)
            messages.AddRange(stream.Append(bytes.AsSpan(i, Math.Min(7, bytes.Length - i))));

        var loot = messages.Select(m => LootMessage.TryParse(m)).OfType<IReadOnlyList<SoulGain>>().SelectMany(g => g).ToList();
        Assert.Equal([new SoulGain(1015, 1), new SoulGain(1120, 1), new SoulGain(1202, 1)], loot);
        Assert.Equal(8, messages.Count);
    }

    [Fact]
    public void StreamFindsTheMessageBoundariesWhenCaptureStartsMidMessage()
    {
        var stream = new GameMessageStream();
        var whole = new[] { Framed(Other), Framed(Ping), Framed(Other), Framed(Ping), Framed(Other), Framed(Lupyllini), Framed(Ping) }
            .SelectMany(b => b).ToArray();
        // The capture begins 5 bytes into the first message.
        var messages = stream.Append(whole.AsSpan(5));
        Assert.Contains(messages, m => LootMessage.TryParse(m) is [{ PetNumber: 1202 }]);
    }

    [Fact]
    public void ReassemblerOrdersSegmentsAndTrimsRetransmissions()
    {
        var tcp = new TcpReassembler();
        var data = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        var output = new List<byte>();
        void Feed(uint seq, int from, int count)
        {
            foreach (var chunk in tcp.Add(seq, data.AsSpan(from, count)))
                output.AddRange(chunk.Data);
        }
        Feed(1000, 0, 10);
        Feed(1020, 20, 10); // early
        Feed(1005, 5, 10);  // overlaps the first, fills up to 15
        Feed(1010, 10, 10); // closes the gap
        Feed(1000, 0, 10);  // retransmission
        Assert.Equal(data, output);
    }
}
