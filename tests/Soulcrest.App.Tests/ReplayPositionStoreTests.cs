using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ReplayPositionStoreTests
{
    private static ReplayPositionSample Sample(int frame, double seconds, long segment = 1) => new(TimeSpan.FromSeconds(seconds),
        new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds), "sample-map", "Sample map",
        123.123456789 + frame, 456.987654321, frame % 2 == 0 ? ReplayPositionProvenance.Flow : ReplayPositionProvenance.Matched, segment, frame);

    [Fact]
    public async Task LosslessChunksPreserveEveryDoubleAndFrameAndCutOffTheUnproductiveTail()
    {
        using var f = new Fixture(); var writer = ReplayPositionStore.Create(f.Root, f.Id);
        var expected = Enumerable.Range(1, 180).Select(frame => Sample(frame, frame / 60d)).ToArray();
        foreach (var sample in expected) writer.Append(sample);
        await writer.SealAsync(TimeSpan.FromSeconds(2));
        await ReplayPositionStore.FlushAsync(f.Root, f.Id);
        var result = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(2));
        Assert.True(result.Exists); Assert.False(result.Truncated); Assert.Empty(result.Gaps);
        Assert.Equal(expected.Where(row => row.Elapsed <= TimeSpan.FromSeconds(2)).Select(row => row.X), result.Samples.Select(row => row.X));
        Assert.Equal(expected.Take(120).Select(row => row.FrameId), result.Samples.Select(row => row.FrameId));
        Assert.All(result.Samples, row => Assert.Equal(456.987654321, row.Y));
        Assert.Equal(64, result.SourceFingerprint.Length);
        Assert.Empty(ReplayPositionStore.Read(f.Root, "synthetic-session", TimeSpan.Zero).Samples);
    }

    [Fact]
    public async Task ACorruptOrTruncatedChunkKeepsOnlyTheLastCompleteCrcValidatedPrefix()
    {
        using var f = new Fixture(); var writer = ReplayPositionStore.Create(f.Root, f.Id);
        writer.Append(Sample(1, 0)); writer.Append(Sample(2, 1)); await writer.SealAsync(TimeSpan.FromSeconds(1));
        var path = ReplayPositionStore.PathFor(f.Root, f.Id); var bytes = File.ReadAllBytes(path);
        var firstLength = BitConverter.ToInt32(bytes, 20); var secondHeader = 12 + 16 + firstLength;
        bytes[secondHeader + 16 + 3] ^= 0x40; File.WriteAllBytes(path, bytes);
        var damaged = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(10));
        Assert.True(damaged.Truncated); Assert.Equal(1, Assert.Single(damaged.Samples).FrameId);
        Assert.Contains(damaged.Gaps, row => row.Reason == ReplayPositionGapReason.IncompleteTail);
        File.WriteAllBytes(path, bytes[..(secondHeader + 8)]);
        var truncated = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(10));
        Assert.Single(truncated.Samples); Assert.True(truncated.Truncated);
        Assert.NotEqual(damaged.SourceFingerprint, truncated.SourceFingerprint);
    }

    [Fact]
    public async Task ABlockedWorkerBoundsItsQueueAndRecordsAPositionGapWithoutThrowing()
    {
        using var f = new Fixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var calls = 0;
        var writer = ReplayPositionStore.Create(f.Root, f.Id, (stream, bytes) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Synthetic writer timeout"); }
            stream.Write(bytes);
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        for (var frame = 1; frame <= 25; frame++) writer.Append(Sample(frame, frame));
        var sealedTask = writer.SealAsync(TimeSpan.FromSeconds(25)); release.Set(); await sealedTask;
        var result = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(25));
        Assert.False(result.Truncated); Assert.True(result.Samples.Length <= 10);
        Assert.Contains(result.Gaps, row => row.Reason == ReplayPositionGapReason.QueueOverflow);
    }

    [Fact]
    public async Task PositionIoFailureCompletesIndependentlyAndLeavesAnExplicitIncompletePrefix()
    {
        using var f = new Fixture();
        var writes = 0;
        var writer = ReplayPositionStore.Create(f.Root, f.Id, (stream, bytes) =>
        { if (++writes > 1) throw new IOException("Synthetic positions-only failure"); stream.Write(bytes); });
        writer.Append(Sample(1, 0)); await writer.SealAsync(TimeSpan.FromSeconds(1));
        var result = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(1));
        Assert.True(result.Exists); Assert.True(result.Truncated); Assert.Empty(result.Samples);
        Assert.Contains(result.Gaps, row => row.Reason == ReplayPositionGapReason.IncompleteTail);
    }

    [Theory]
    [InlineData("enum")]
    [InlineData("negative-time")]
    [InlineData("count")]
    [InlineData("sequence")]
    public async Task ValidCrcDoesNotAllowInvalidPayloadsToEscapeTheValidatedPrefix(string invalid)
    {
        using var f = new Fixture(); var writer = ReplayPositionStore.Create(f.Root, f.Id);
        writer.Append(Sample(1, 0)); writer.Append(Sample(2, 1)); await writer.SealAsync(TimeSpan.FromSeconds(1));
        var path = ReplayPositionStore.PathFor(f.Root, f.Id); var bytes = File.ReadAllBytes(path);
        var offset = 12 + 16 + BitConverter.ToInt32(bytes, 20);
        var length = BitConverter.ToInt32(bytes, offset + 8);
        var payload = bytes.AsSpan(offset + 16, length).ToArray();
        if (invalid == "count") BitConverter.GetBytes(129).CopyTo(payload, 1);
        else if (invalid == "negative-time") BitConverter.GetBytes(-1L).CopyTo(payload, 5);
        else if (invalid == "sequence") BitConverter.GetBytes(99L).CopyTo(bytes, offset);
        else
        {
            using var input = new MemoryStream(payload); using var reader = new BinaryReader(input);
            input.Position = 5 + 16; _ = reader.ReadString(); _ = reader.ReadString(); input.Position += 16;
            payload[(int)input.Position] = 255;
        }
        payload.CopyTo(bytes, offset + 16);
        BitConverter.GetBytes(ReplayPositionStore.Crc(payload)).CopyTo(bytes, offset + 12);
        File.WriteAllBytes(path, bytes);
        var result = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(2));
        Assert.True(result.Truncated); Assert.Equal(1, Assert.Single(result.Samples).FrameId);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), result.SourceFingerprint);
        bytes[^1] ^= 0x40; File.WriteAllBytes(path, bytes);
        var changedTail = ReplayPositionStore.Read(f.Root, f.Id, TimeSpan.FromSeconds(2));
        Assert.Equal(result.Samples.ToArray(), changedTail.Samples.ToArray());
        Assert.NotEqual(result.SourceFingerprint, changedTail.SourceFingerprint);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), changedTail.SourceFingerprint);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-position-store-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public void Dispose() { ReplayPositionStore.Forget(Root, Id); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
