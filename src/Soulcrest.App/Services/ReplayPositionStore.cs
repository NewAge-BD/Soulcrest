using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Soulcrest.App.Services;

/// <summary>Versioned CRC-protected positional sidecar owned by one original unified session directory.</summary>
public static class ReplayPositionStore
{
    internal const int Version = 1;
    internal const int MaximumSamplesPerChunk = 128;
    internal const int MaximumChunkBytes = 256 * 1024;
    private static readonly byte[] Magic = "SCRPOS01"u8.ToArray();
    private static readonly ConcurrentDictionary<string, ReplayPositionWriter> Writers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> FailedWriters = new(StringComparer.OrdinalIgnoreCase);
    public static string DefaultRoot => Path.Combine(AppPaths.DataDirectory, "loot-tracker", "unified-sessions");
    public static ReplayPositionReadResult Read(string sessionId, TimeSpan finalElapsed) => Read(DefaultRoot, sessionId, finalElapsed);
    public static ReplayPositionReadResult Read(string root, string sessionId, TimeSpan finalElapsed)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) return new([], [], "", false, false);
        var path = PathFor(root, sessionId);
        if (!File.Exists(path)) return FailedWriters.ContainsKey(path)
            ? new([], [new(TimeSpan.Zero, ReplayPositionGapReason.WriteFailure)], "WRITE-FAILURE", false, true)
            : new([], [], "", false, false);
        var samples = ImmutableArray.CreateBuilder<ReplayPositionSample>();
        var gaps = ImmutableArray.CreateBuilder<ReplayPositionGap>();
        var truncated = false; var ended = false;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            try
            {
                var header = reader.ReadBytes(12); digest.AppendData(header);
                if (header.Length != 12 || !header.AsSpan(0, 8).SequenceEqual(Magic) || BitConverter.ToInt32(header, 8) != Version)
                    throw new InvalidDataException();
                long expected = 1;
                while (stream.Position < stream.Length)
                {
                    var chunkHeader = reader.ReadBytes(16); digest.AppendData(chunkHeader);
                    if (chunkHeader.Length != 16) { truncated = true; break; }
                    var sequence = BitConverter.ToInt64(chunkHeader, 0);
                    var length = BitConverter.ToInt32(chunkHeader, 8);
                    var crc = BitConverter.ToUInt32(chunkHeader, 12);
                    if (ended || sequence != expected++ || length is <= 0 or > MaximumChunkBytes) { truncated = true; break; }
                    var payload = reader.ReadBytes(length); digest.AppendData(payload);
                    if (payload.Length != length || Crc(payload) != crc) { truncated = true; break; }
                    using var content = new MemoryStream(payload, false);
                    using var item = new BinaryReader(content, Encoding.UTF8, true);
                    var kind = item.ReadByte();
                    var chunkSamples = new List<ReplayPositionSample>(); var chunkGaps = new List<ReplayPositionGap>();
                    if (kind == 1)
                    {
                        var count = item.ReadInt32(); if (count is < 0 or > MaximumSamplesPerChunk) throw new InvalidDataException();
                        for (var index = 0; index < count; index++)
                        {
                            var elapsed = TimeSpan.FromTicks(item.ReadInt64());
                            var at = new DateTimeOffset(item.ReadInt64(), TimeSpan.Zero);
                            var map = item.ReadString(); var name = item.ReadString();
                            var x = item.ReadDouble(); var y = item.ReadDouble();
                            var provenance = (ReplayPositionProvenance)item.ReadByte();
                            var segment = item.ReadInt64(); var frame = item.ReadInt64();
                            if (elapsed < TimeSpan.Zero || at == default || map.Length is < 1 or > 256 || name.Length > 1024
                                || !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0
                                || !Enum.IsDefined(provenance) || segment < 0 || frame < 0) throw new InvalidDataException();
                            chunkSamples.Add(new(elapsed, at, map, name, x, y, provenance, segment, frame));
                        }
                    }
                    else if (kind == 2) { _ = item.ReadInt64(); ended = true; }
                    else throw new InvalidDataException();
                    var gapCount = item.ReadInt32(); if (gapCount is < 0 or > 128) throw new InvalidDataException();
                    for (var index = 0; index < gapCount; index++)
                    {
                        var elapsed = TimeSpan.FromTicks(item.ReadInt64()); var reason = (ReplayPositionGapReason)item.ReadByte();
                        if (elapsed < TimeSpan.Zero || !Enum.IsDefined(reason)) throw new InvalidDataException();
                        chunkGaps.Add(new(elapsed, reason));
                    }
                    if (content.Position != content.Length) throw new InvalidDataException();
                    var previousElapsed = samples.Count > 0 ? samples[^1].Elapsed : TimeSpan.Zero;
                    foreach (var sample in chunkSamples)
                    {
                        if (sample.Elapsed < previousElapsed) throw new InvalidDataException();
                        previousElapsed = sample.Elapsed;
                    }
                    samples.AddRange(chunkSamples.Where(sample => sample.Elapsed <= finalElapsed));
                    gaps.AddRange(chunkGaps.Where(gap => gap.Elapsed <= finalElapsed));
                }
            }
            finally
            {
                // Fingerprint the complete source, including bytes after a semantically invalid chunk.
                var buffer = new byte[8192]; int read;
                while ((read = stream.Read(buffer)) > 0) digest.AppendData(buffer.AsSpan(0, read));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { truncated = true; }
        truncated |= !ended;
        if (FailedWriters.ContainsKey(path))
        { truncated = true; gaps.Add(new(samples.Count > 0 ? samples[^1].Elapsed : TimeSpan.Zero, ReplayPositionGapReason.WriteFailure)); }
        if (truncated) gaps.Add(new(samples.Count > 0 ? samples[^1].Elapsed : TimeSpan.Zero, ReplayPositionGapReason.IncompleteTail));
        return new(samples.ToImmutable(), gaps.ToImmutable(), Convert.ToHexString(digest.GetHashAndReset()), true, truncated);
    }

    public static Task FlushAsync(string sessionId, CancellationToken cancellationToken = default) => FlushAsync(DefaultRoot, sessionId, cancellationToken);
    public static Task FlushAsync(string root, string sessionId, CancellationToken cancellationToken = default) =>
        Writers.TryGetValue(PathFor(root, sessionId), out var writer) ? writer.Completion.WaitAsync(cancellationToken) : Task.CompletedTask;

    internal static ReplayPositionWriter Create(string root, string sessionId, Action<Stream, byte[]>? write = null)
    {
        var path = PathFor(root, sessionId);
        var writer = new ReplayPositionWriter(path, write);
        if (!Writers.TryAdd(path, writer)) throw new InvalidOperationException("A positional writer already owns this session.");
        writer.Start(); return writer;
    }
    internal static void Forget(string root, string sessionId)
    { var path = PathFor(root, sessionId); Writers.TryRemove(path, out _); FailedWriters.TryRemove(path, out _); }
    internal static void ReportFailure(string path) => FailedWriters[path] = true;
    internal static string PathFor(string root, string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new ArgumentException("A canonical original session ID is required.", nameof(sessionId));
        var full = Path.GetFullPath(root);
        if (full.StartsWith(@"\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new InvalidDataException("Position storage must be local.");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse storage is not supported.");
        var directory = Path.Combine(full, sessionId);
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        var path = Path.Combine(directory, "positions-v1.bin");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        return path;
    }
    internal static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
    internal static byte[] Header() { var header = new byte[12]; Magic.CopyTo(header, 0); BitConverter.GetBytes(Version).CopyTo(header, 8); return header; }
}

/// <summary>Ten one-second chunks queue without blocking capture. Only this worker performs file I/O.</summary>
internal sealed class ReplayPositionWriter
{
    private sealed record Chunk(ReplayPositionSample[] Samples, ReplayPositionGap[] Gaps);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Action<Stream, byte[]> _write;
    private readonly Channel<Chunk> _chunks = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(10)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly List<ReplayPositionSample> _current = [];
    private readonly List<ReplayPositionGap> _gaps = [];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _second = -1, _sourceSegment = -1, _outputSegment;
    private TimeSpan _finalElapsed, _lastElapsed;
    private bool _sealed, _deleted, _failed;
    public Task Completion => _completion.Task;
    internal ReplayPositionWriter(string path, Action<Stream, byte[]>? write)
    { _path = path; _write = write ?? ((stream, bytes) => stream.Write(bytes)); }
    internal void Start() => _ = Task.Run(Run);
    internal void Append(ReplayPositionSample sample)
    {
        lock (_gate)
        {
            if (_sealed || _deleted || _failed || sample.Elapsed < _lastElapsed) return;
            var second = sample.Elapsed.Ticks / TimeSpan.TicksPerSecond;
            if (_current.Count > 0 && (second != _second || _current.Count == ReplayPositionStore.MaximumSamplesPerChunk)) QueueCurrent();
            if (_sourceSegment != sample.SegmentId) { _sourceSegment = sample.SegmentId; _outputSegment++; }
            _current.Add(sample with { SegmentId = _outputSegment });
            _second = second; _lastElapsed = sample.Elapsed;
        }
    }
    internal void Gap(TimeSpan elapsed, ReplayPositionGapReason reason)
    {
        lock (_gate)
        {
            if (_sealed || _deleted || _failed) return;
            QueueCurrent(); _outputSegment++;
            if (_gaps.Count < 128) _gaps.Add(new(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed, reason));
        }
    }
    private void QueueCurrent()
    {
        if (_current.Count == 0) return;
        if (_chunks.Writer.TryWrite(new(_current.ToArray(), _gaps.ToArray()))) _gaps.Clear();
        else
        {
            if (_gaps.Count < 128) _gaps.Add(new(_current[0].Elapsed, ReplayPositionGapReason.QueueOverflow));
            _outputSegment++;
        }
        _current.Clear();
    }
    internal Task SealAsync(TimeSpan elapsed)
    {
        lock (_gate)
        {
            if (!_sealed) { QueueCurrent(); _finalElapsed = elapsed; _sealed = true; _chunks.Writer.TryComplete(); }
        }
        return Completion;
    }
    internal async Task StopForDeleteAsync()
    {
        lock (_gate) { _deleted = true; _sealed = true; _current.Clear(); _chunks.Writer.TryComplete(); }
        await Completion.ConfigureAwait(false);
    }
    private async Task Run()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
            _write(stream, ReplayPositionStore.Header()); long sequence = 0;
            await foreach (var chunk in _chunks.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                lock (_gate) if (_deleted) break;
                using var data = new MemoryStream(); using var writer = new BinaryWriter(data, Encoding.UTF8, true);
                writer.Write((byte)1); writer.Write(chunk.Samples.Length);
                foreach (var sample in chunk.Samples)
                {
                    writer.Write(sample.Elapsed.Ticks); writer.Write(sample.OriginalAt.UtcTicks);
                    writer.Write(sample.MapId); writer.Write(sample.MapName); writer.Write(sample.X); writer.Write(sample.Y);
                    writer.Write((byte)sample.Provenance); writer.Write(sample.SegmentId); writer.Write(sample.FrameId);
                }
                WriteGaps(writer, chunk.Gaps); WriteChunk(stream, ++sequence, data.ToArray()); stream.Flush(true);
            }
            lock (_gate)
            {
                if (!_deleted)
                {
                    using var data = new MemoryStream(); using var writer = new BinaryWriter(data, Encoding.UTF8, true);
                    writer.Write((byte)2); writer.Write(_finalElapsed.Ticks); WriteGaps(writer, _gaps);
                    WriteChunk(stream, ++sequence, data.ToArray()); stream.Flush(true);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { lock (_gate) _failed = true; ReplayPositionStore.ReportFailure(_path); }
        finally { _chunks.Writer.TryComplete(); _completion.TrySetResult(); }
    }
    private void WriteChunk(Stream stream, long sequence, byte[] payload)
    {
        var header = new byte[16]; BitConverter.GetBytes(sequence).CopyTo(header, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(header, 8); BitConverter.GetBytes(ReplayPositionStore.Crc(payload)).CopyTo(header, 12);
        _write(stream, header); _write(stream, payload);
    }
    private static void WriteGaps(BinaryWriter writer, IEnumerable<ReplayPositionGap> gaps)
    {
        var rows = gaps.ToArray(); writer.Write(rows.Length);
        foreach (var gap in rows) { writer.Write(gap.Elapsed.Ticks); writer.Write((byte)gap.Reason); }
    }
}
