using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Synthetic transport damage must invalidate progress before any partial batch is delivered.</summary>
public sealed class GameMessageContinuityTests
{
    private static readonly byte[] Ping = [0x00, 0x36];
    private static readonly byte[] Gain = [0x0d, 0x90, 1, 0xec, 3, 0, 0, 2, 0, 0, 0];

    internal static byte[] Frame(byte[] message)
    {
        var header = new List<byte>();
        var value = (uint)message.Length + 4;
        do
        {
            var next = (byte)(value & 127);
            value >>= 7;
            header.Add(value == 0 ? next : (byte)(next | 128));
        } while (value != 0);
        return [.. header, .. message];
    }

    internal static byte[] Bundle(byte[] inner)
    {
        var block = new List<byte> { (byte)(Math.Min(inner.Length, 15) << 4) };
        if (inner.Length >= 15)
        {
            var rest = inner.Length - 15;
            while (rest >= 255) { block.Add(255); rest -= 255; }
            block.Add((byte)rest);
        }
        block.AddRange(inner);
        return [0xff, 0xff, .. BitConverter.GetBytes(inner.Length), .. block];
    }

    private static GameMessageStream Synced()
    {
        var stream = new GameMessageStream();
        Assert.Equal(4, stream.Append([.. Frame(Ping), .. Frame(Ping), .. Frame(Ping), .. Frame(Ping)]).Count);
        Assert.Equal(0, stream.ContinuityBreaks);
        return stream;
    }

    [Fact]
    public void InvalidLz4DiscardsTheWholeAppendAndReportsTheBreakImmediately()
    {
        var stream = Synced();
        byte[] damagedBundle = [0xff, 0xff, 3, 0, 0, 0, 0x30]; // claims three missing literal bytes
        Assert.Empty(stream.Append([.. Frame(Gain), .. Frame(damagedBundle), .. Frame(Ping)]));
        Assert.Equal(1, stream.ContinuityBreaks);
        Assert.Equal(0, stream.Resyncs); // no later packet was needed to report the break

        var recovered = stream.Append([.. Frame(Ping), .. Frame(Ping), .. Frame(Ping), .. Frame(Ping)]);
        Assert.Equal(5, recovered.Count); // includes the complete frame after the damaged bundle
        Assert.DoesNotContain(recovered, m => LootMessage.TryParse(m) is not null);
    }

    [Fact]
    public void CompleteBundlesRequireEveryInnerFrameAndTailToBeValid()
    {
        byte[][] badMessages =
        [
            [0xff, 0xff], // incomplete bundle header in an otherwise complete outer frame
            Bundle([.. Frame(Gain), 0x80]), // incomplete inner length
            Bundle([.. Frame(Gain), .. Frame(Ping)[..^1]]), // incomplete inner body
            Bundle([.. Frame(Gain), 0]), // impossible inner length
        ];
        foreach (var bad in badMessages)
        {
            var stream = Synced();
            Assert.Empty(stream.Append(Frame(bad)));
            Assert.Equal(1, stream.ContinuityBreaks);
        }
    }

    [Fact]
    public void InvalidTopLevelHeaderDropsAlreadyDecodedUpdatesWithoutWaitingForResync()
    {
        var stream = Synced();
        Assert.Empty(stream.Append([.. Frame(Gain), 0xff, 0xff, 0xff, 0xff]));
        Assert.Equal(1, stream.ContinuityBreaks);
        Assert.Equal(0, stream.Resyncs);
        Assert.Equal(4, stream.Append([.. Frame(Ping), .. Frame(Ping), .. Frame(Ping), .. Frame(Ping)]).Count);
        Assert.Equal(1, stream.Resyncs);
    }

    [Fact]
    public void OrdinaryTcpFragmentationDoesNotBreakContinuityOrDiscardMessages()
    {
        var stream = Synced();
        var framed = Frame(new byte[256]); // two-byte length header
        Assert.Empty(stream.Append(framed.AsSpan(0, 1)));
        Assert.Empty(stream.Append(framed.AsSpan(1, 50)));
        Assert.Equal(new byte[256], Assert.Single(stream.Append(framed.AsSpan(51))));
        Assert.Equal(0, stream.ContinuityBreaks);

        var gain = Frame(Gain);
        Assert.Empty(stream.Append(gain.AsSpan(0, gain.Length - 1)));
        Assert.Equal(Gain, Assert.Single(stream.Append(gain.AsSpan(gain.Length - 1))));
        Assert.Equal(0, stream.ContinuityBreaks);
    }

    [Fact]
    public void BundleRecursionIsBoundedAndAnOrdinaryNestedBundleStillWorks()
    {
        var nested = Gain;
        for (var i = 0; i < 8; i++) nested = Bundle(Frame(nested));
        var stream = Synced();
        Assert.Equal(Gain, Assert.Single(stream.Append(Frame(nested))));
        Assert.Equal(0, stream.ContinuityBreaks);
        Assert.Empty(stream.Append(Frame(Bundle(Frame(nested)))));
        Assert.Equal(1, stream.ContinuityBreaks);
    }

    [Fact]
    public void ExpansionBytesAndDecodedMessageCountsHaveHardLimits()
    {
        var stream = Synced();
        byte[] excessiveSize = [0xff, 0xff, .. BitConverter.GetBytes(16 * 1024 * 1024 + 1)];
        Assert.Empty(stream.Append(Frame(excessiveSize)));
        Assert.Equal(1, stream.ContinuityBreaks);

        stream = Synced();
        var tooMany = Enumerable.Repeat(Frame(Ping), 65537).SelectMany(frame => frame).ToArray();
        Assert.Empty(stream.Append(tooMany));
        Assert.Equal(1, stream.ContinuityBreaks);
    }
}
