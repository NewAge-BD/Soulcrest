using System.IO.Compression;
using System.Text.Json;
using PacketDotNet;
using SharpPcap;
using Soulcrest.App.Network;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Private temporary PCAPs with synthetic bytes; no Npcap, sockets, desktop or real profile.</summary>
public sealed class NetworkDiagnosticRecorderTests
{
    [Fact]
    public async Task InactiveSourceDoesNotCreateDirectoriesOrCaptureFiles()
    {
        using var folder = new TemporaryFolder();
        var recorder = new NetworkDiagnosticRecorder(_ => false, folder.Root);
        Assert.False(recorder.Start());
        Assert.Equal(NetworkDiagnosticRecorderState.Unavailable, recorder.Snapshot.State);
        Assert.Equal("source-unavailable", recorder.Snapshot.StopReason);
        Assert.False(Directory.Exists(folder.Root));
        Assert.Empty(await Package(recorder));
    }

    [Fact]
    public async Task PcapPreservesOriginalBytesTimestampLengthAndSeparatesLinkTypesAndGenerations()
    {
        using var folder = new TemporaryFolder();
        var recorder = new NetworkDiagnosticRecorder(begin => begin(), folder.Root);
        Assert.True(recorder.Start());
        var original = Raw([1, 2, 3]);
        original.PacketLength = 91;
        recorder.TryRecord(original, 4);
        original.Data[0] = 99; // Copy at capture time; a native callback buffer may be reused.
        recorder.TryRecord(Raw([4, 5], LinkLayers.Null), 4);
        recorder.TryRecord(Raw([6]), 5);
        await recorder.StopAsync();
        var snapshot = recorder.Snapshot;
        Assert.Equal(NetworkDiagnosticRecorderState.Completed, snapshot.State);
        Assert.Equal(3, snapshot.Packets);
        Assert.Equal(3, snapshot.Artifacts.Count);
        var files = await Package(recorder);
        Assert.Equal(3, files.Count);
        var first = Read(files["network/game-001.pcap"]);
        Assert.Equal((uint)LinkLayers.Ethernet, first.LinkType);
        var frame = Assert.Single(first.Frames);
        Assert.Equal(1791849600u, frame.Seconds);
        Assert.Equal(123456u, frame.Microseconds);
        Assert.Equal(91u, frame.OriginalLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Data);
        Assert.Equal(snapshot.Bytes, files.Values.Sum(bytes => (long)bytes.Length));
        var safe = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(folder.Root, safe);
        Assert.DoesNotContain("192.0.", safe);
        Assert.Contains("completeness unknown", snapshot.Coverage);
    }

    [Fact]
    public async Task ExportIncludesOnlyCompletedCurrentSessionAndNotOtherFiles()
    {
        using var folder = new TemporaryFolder();
        Directory.CreateDirectory(folder.Root);
        File.WriteAllText(Path.Combine(folder.Root, "unrelated.pcap"), "private unrelated input");
        var recorder = new NetworkDiagnosticRecorder(begin => begin(), folder.Root);
        Assert.True(recorder.Start()); recorder.TryRecord(Raw([11]), 1); await recorder.StopAsync();
        Assert.True(recorder.Start()); recorder.TryRecord(Raw([22]), 2); await recorder.StopAsync();
        var files = await Package(recorder);
        Assert.Equal(new byte[] { 22 }, Assert.Single(Read(Assert.Single(files).Value).Frames).Data);
    }

    [Fact]
    public async Task AFailedNewRecordingCannotAttachTheEarlierCompletedSession()
    {
        using var folder = new TemporaryFolder();
        var sourceAvailable = true;
        var recorder = new NetworkDiagnosticRecorder(begin => sourceAvailable && begin(), folder.Root);
        Assert.True(recorder.Start());
        recorder.TryRecord(Raw([11]), 1);
        await recorder.StopAsync();
        Assert.Single(await Package(recorder));
        sourceAvailable = false;
        Assert.False(recorder.Start());
        Assert.Equal(NetworkDiagnosticRecorderState.Unavailable, recorder.Snapshot.State);
        Assert.Empty(recorder.Snapshot.Artifacts);
        Assert.Empty(await Package(recorder));
    }

    [Fact]
    public async Task SizeLimitBoundsWholePcapIncludingHeadersAndDoesNotRecordTheOverflowFrame()
    {
        using var folder = new TemporaryFolder();
        var recorder = new NetworkDiagnosticRecorder(begin => begin(), folder.Root, maxBytes: 43);
        Assert.True(recorder.Start());
        recorder.TryRecord(Raw([1, 2, 3]), 1);
        recorder.TryRecord(Raw([4]), 1);
        await recorder.StopAsync();
        Assert.Equal("size-limit", recorder.Snapshot.StopReason);
        Assert.Equal(43, recorder.Snapshot.Bytes);
        Assert.Equal(1, recorder.Snapshot.Packets);
        Assert.Equal(1, recorder.Snapshot.DroppedPackets);
    }

    [Fact]
    public async Task TimerStopsWithoutAnotherPacket()
    {
        using var folder = new TemporaryFolder();
        var recorder = new NetworkDiagnosticRecorder(begin => begin(), folder.Root, duration: TimeSpan.FromMilliseconds(20));
        Assert.True(recorder.Start());
        await WaitUntil(() => recorder.Snapshot.State == NetworkDiagnosticRecorderState.Completed);
        Assert.Equal("time-limit", recorder.Snapshot.StopReason);
        Assert.Empty(recorder.Snapshot.Artifacts);
    }

    [Fact]
    public async Task ABlockedWriterDropsAtCapacityWithoutBlockingTheCaptureCaller()
    {
        using var folder = new TemporaryFolder();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var recorder = new NetworkDiagnosticRecorder(begin => begin(), folder.Root, capacity: 1, createFile: path =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Synthetic writer timeout");
            return File.Create(path);
        });
        Assert.True(recorder.Start());
        recorder.TryRecord(Raw([1]), 1);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                recorder.TryRecord(Raw([2]), 1);
                recorder.TryRecord(Raw([3]), 1);
            }).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, recorder.Snapshot.DroppedPackets);
        }
        finally { release.Set(); }
        await recorder.StopAsync();
        Assert.Equal(2, recorder.Snapshot.Packets);
    }

    [Fact]
    public async Task WriterErrorUsesFixedReasonAndExportsNoPartialFiles()
    {
        using var folder = new TemporaryFolder();
        var recorder = new NetworkDiagnosticRecorder(begin => begin(), folder.Root,
            createFile: _ => throw new IOException("Synthetic private endpoint 192.0.2.2"));
        Assert.True(recorder.Start());
        recorder.TryRecord(Raw([1]), 1);
        await WaitUntil(() => recorder.Snapshot.State == NetworkDiagnosticRecorderState.Failed);
        Assert.Equal("write-error", recorder.Snapshot.StopReason);
        Assert.DoesNotContain("192.0.2.2", JsonSerializer.Serialize(recorder.Snapshot));
        Assert.Empty(await Package(recorder));
    }

    [Fact]
    public async Task WriterTimeoutAllowsMetadataExportWithoutRepeatedWaitAndPreventsAnotherHungWorker()
    {
        using var folder = new TemporaryFolder();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var sourceAvailable = true;
        var recorder = new NetworkDiagnosticRecorder(begin => sourceAvailable && begin(), folder.Root, stopWait: TimeSpan.FromMilliseconds(20),
            createFile: path =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Synthetic writer timeout");
                return File.Create(path);
            });
        Assert.True(recorder.Start());
        recorder.TryRecord(Raw([1]), 1);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(NetworkDiagnosticRecorderState.Failed, recorder.Snapshot.State);
            Assert.Equal("writer-timeout", recorder.Snapshot.StopReason);
            Assert.False(recorder.Start()); // No second writer while the failed one is still blocked.
            Assert.Equal(NetworkDiagnosticRecorderState.Failed, recorder.Snapshot.State);
            Assert.Equal("writer-timeout", recorder.Snapshot.StopReason);
            sourceAvailable = false; // A stopped source must not mask the existing failed writer.
            Assert.False(recorder.Start());
            Assert.Equal(NetworkDiagnosticRecorderState.Failed, recorder.Snapshot.State);
            Assert.Equal("writer-timeout", recorder.Snapshot.StopReason);
            Assert.Empty(await Package(recorder).WaitAsync(TimeSpan.FromMilliseconds(250)));
            Assert.Empty(recorder.Snapshot.Artifacts);
        }
        finally { release.Set(); }
        // Once the old worker really closes, a new independent session can be started.
        sourceAvailable = true;
        await WaitUntil(recorder.Start);
        await recorder.StopAsync();
    }

    [Fact]
    public async Task APreviousSessionsPendingPacketsCannotEnterTheNextCapture()
    {
        using var folder = new TemporaryFolder();
        var recorder = new NetworkDiagnosticRecorder(begin => begin(), folder.Root);
        Assert.True(recorder.Start());
        var previousId = recorder.SessionId;
        await recorder.StopAsync();
        Assert.True(recorder.Start());
        recorder.TryRecord(Raw([1]), 1, previousId);
        recorder.TryRecord(Raw([2]), 1, 0);
        recorder.TryRecord(Raw([3]), 1, recorder.SessionId);
        await recorder.StopAsync();
        Assert.Equal(1, recorder.Snapshot.Packets);
        Assert.Equal(new byte[] { 3 }, Assert.Single(Read(Assert.Single(await Package(recorder)).Value).Frames).Data);
    }

    internal static RawCapture Raw(byte[] data, LinkLayers link = LinkLayers.Ethernet) =>
        new(link, new PosixTimeval(1791849600UL, 123456UL), data, null);

    internal static async Task<Dictionary<string, byte[]>> Package(NetworkDiagnosticRecorder recorder)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            await recorder.WriteToArchiveAsync(archive);
        bytes.Position = 0;
        using var read = new ZipArchive(bytes, ZipArchiveMode.Read);
        return read.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var stream = entry.Open(); using var contents = new MemoryStream();
            stream.CopyTo(contents); return contents.ToArray();
        });
    }

    internal static (uint LinkType, List<(uint Seconds, uint Microseconds, uint OriginalLength, byte[] Data)> Frames) Read(byte[] data)
    {
        using var stream = new MemoryStream(data); using var read = new BinaryReader(stream);
        Assert.Equal(0xa1b2c3d4u, read.ReadUInt32());
        Assert.Equal(2, read.ReadUInt16()); Assert.Equal(4, read.ReadUInt16());
        read.ReadInt32(); read.ReadUInt32(); read.ReadUInt32(); var linkType = read.ReadUInt32();
        var frames = new List<(uint, uint, uint, byte[])>();
        while (stream.Position < stream.Length)
        {
            var seconds = read.ReadUInt32(); var microseconds = read.ReadUInt32();
            var length = read.ReadUInt32(); var originalLength = read.ReadUInt32();
            frames.Add((seconds, microseconds, originalLength, read.ReadBytes(checked((int)length))));
        }
        return (linkType, frames);
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-diagnostic-recorder-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
