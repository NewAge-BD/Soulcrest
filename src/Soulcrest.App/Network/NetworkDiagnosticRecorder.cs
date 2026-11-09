using System.IO.Compression;
using System.Diagnostics;
using System.Threading.Channels;
using SharpPcap;
using Soulcrest.App.Services;
using Timer = System.Threading.Timer;

namespace Soulcrest.App.Network;

public enum NetworkDiagnosticRecorderState { Idle, Recording, Stopping, Completed, Failed, Unavailable }

/// <summary>Safe artifact metadata. Paths, process IDs and endpoints never enter this record.</summary>
public sealed record NetworkDiagnosticArtifact(string Name, long Packets, long Bytes, uint LinkType, long Generation);

public sealed record NetworkDiagnosticRecorderSnapshot(NetworkDiagnosticRecorderState State,
    DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt, long Packets, long Bytes, long DroppedPackets,
    string? StopReason, string Coverage, IReadOnlyList<NetworkDiagnosticArtifact> Artifacts)
{
    public long CaptureBoundaries { get; init; }
    public const string SupportedCoverage = "IPv4 TCP; active game-source adapter only; both directions; starts mid-transport; UDP, IPv6 and other adapters not captured; kernel capture drops unknown; completeness unknown";
    public static NetworkDiagnosticRecorderSnapshot Empty { get; } = new(NetworkDiagnosticRecorderState.Idle,
        null, null, 0, 0, 0, null, SupportedCoverage, []);
}

/// <summary>
/// Opt-in bounded tap of the existing game source (user request 2026-11-09). The source proves
/// ownership before calling TryRecord. Capture callbacks only copy and TryWrite: never disk I/O,
/// blocking waits or user callbacks. Failures cannot stop or reset the productive tracker.
/// </summary>
public sealed class NetworkDiagnosticRecorder
{
    private const int MaxPacketBytes = 262144, MaxQueuedBytes = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Func<Func<bool>, bool> _tryBegin;
    private readonly string _root;
    private readonly TimeSpan _duration;
    private readonly TimeSpan _stopWait;
    private readonly long _maxBytes;
    private readonly int _capacity;
    private readonly Func<string, Stream> _createFile;
    private Session? _session;
    private bool _unavailable;
    private long _nextSessionId;
    private int _exports;

    internal NetworkDiagnosticRecorder(Func<Func<bool>, bool> tryBegin, string? root = null,
        TimeSpan? duration = null, long maxBytes = 64L * 1024 * 1024, int capacity = 128,
        Func<string, Stream>? createFile = null, TimeSpan? stopWait = null)
    {
        _tryBegin = tryBegin;
        _root = root ?? Path.Combine(AppPaths.DataDirectory, "captures", "diagnostics");
        _duration = duration ?? TimeSpan.FromMinutes(5);
        _stopWait = stopWait ?? TimeSpan.FromSeconds(5);
        _maxBytes = maxBytes;
        _capacity = capacity;
        _createFile = createFile ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        if (_duration <= TimeSpan.Zero || _stopWait <= TimeSpan.Zero || maxBytes < 24 || capacity < 1) throw new ArgumentOutOfRangeException(nameof(duration));
    }

    public event Action? Changed;

    public NetworkDiagnosticRecorderSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                if (_unavailable) return NetworkDiagnosticRecorderSnapshot.Empty with
                    { State = NetworkDiagnosticRecorderState.Unavailable, StopReason = "source-unavailable" };
                if (_session is not { } session) return NetworkDiagnosticRecorderSnapshot.Empty;
                return new(session.State, session.StartedAt, session.StoppedAt, session.Packets, session.Bytes,
                    session.Dropped, session.Reason, NetworkDiagnosticRecorderSnapshot.SupportedCoverage,
                    session.Artifacts.Select(file => file.Metadata).ToArray()) { CaptureBoundaries = session.Boundaries };
            }
        }
    }

    /// <summary>Returns false when the game source is inactive/unbound or the previous writer is still active.</summary>
    public bool Start()
    {
        var busy = false;
        var started = _tryBegin(() =>
        {
            lock (_gate)
            {
                if (_exports > 0 || _session is { Writer.IsCompleted: false })
                { busy = true; return false; }
                _unavailable = false;
                var session = new Session(Path.Combine(_root, Guid.NewGuid().ToString("N")), _capacity, ++_nextSessionId);
                _session = session;
                session.Writer = Task.Run(() => WriteSession(session));
                session.Timer = new Timer(_ => Tick(session), null,
                    _duration < TimeSpan.FromSeconds(1) ? _duration : TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                return true;
            }
        });
        if (!started && !busy)
        {
            lock (_gate)
                // The source may reject the request before invoking begin. Preserve an unfinished
                // failed writer's diagnostic state even when that source has since stopped.
                if (_session is null || _session.Writer.IsCompleted)
                    _unavailable = true;
        }
        NotifyChanged();
        return started;
    }

    internal long SessionId { get { lock (_gate) return _session is { State: NetworkDiagnosticRecorderState.Recording } session ? session.Id : 0; } }

    internal void TryRecord(RawCapture raw, long generation, long? sessionId = null)
    {
        lock (_gate)
        {
            if (_session is not { State: NetworkDiagnosticRecorderState.Recording } session) return;
            if (sessionId is { } expected && (expected == 0 || expected != session.Id)) return;
            if (Stopwatch.GetElapsedTime(session.StartedTimestamp) >= _duration)
            {
                FinishLocked(session, "time-limit");
                return;
            }
            if (raw.Timeval.Seconds > uint.MaxValue || raw.Timeval.MicroSeconds >= 1_000_000 || raw.Data.Length > MaxPacketBytes)
            {
                session.Dropped++;
                return;
            }
            var key = (generation, (uint)raw.LinkLayerType);
            var bytes = 16L + raw.Data.Length + (session.Segments.Contains(key) ? 0 : 24);
            if (session.ReservedBytes + bytes > _maxBytes)
            {
                session.Dropped++;
                FinishLocked(session, "size-limit");
                return;
            }
            if (session.QueuedBytes + raw.Data.Length > MaxQueuedBytes)
            {
                session.Dropped++;
                return;
            }
            var packet = new RecordedPacket(generation, (uint)raw.LinkLayerType, (uint)raw.Timeval.Seconds,
                (uint)raw.Timeval.MicroSeconds, (uint)Math.Max(raw.PacketLength, raw.Data.Length), raw.Data.ToArray());
            if (!session.Queue.Writer.TryWrite(packet)) { session.Dropped++; return; }
            session.Segments.Add(key);
            session.ReservedBytes += bytes;
            session.QueuedBytes += packet.Data.Length;
        }
    }

    internal void RecordDropped(long count = 1, long? sessionId = null)
    {
        lock (_gate)
            if (_session is { State: NetworkDiagnosticRecorderState.Recording } session &&
                (sessionId is null || sessionId != 0 && sessionId == session.Id)) session.Dropped += count;
    }

    internal void RecordBoundary()
    {
        lock (_gate)
            if (_session is { State: NetworkDiagnosticRecorderState.Recording } session) session.Boundaries++;
    }

    internal void SourceStopped(string reason = "network-stopped")
    {
        lock (_gate)
            if (_session is { State: NetworkDiagnosticRecorderState.Recording } session) FinishLocked(session, reason);
        NotifyChanged();
    }

    public async Task StopAsync()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            if (session is null) return;
            if (session.TimedOut) return;
            if (session.State == NetworkDiagnosticRecorderState.Recording) FinishLocked(session, "user-stopped");
        }
        NotifyChanged();
        try { await session.Writer.WaitAsync(_stopWait).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (session.Writer.IsCompleted) return;
                session.TimedOut = true;
                session.State = NetworkDiagnosticRecorderState.Failed;
                session.Reason = "writer-timeout";
                session.StoppedAt ??= DateTimeOffset.UtcNow;
                session.Artifacts.Clear();
            }
            NotifyChanged();
        }
    }

    /// <summary>Copies only closed files owned by the current session; never scans the user's captures directory.</summary>
    public async Task WriteToArchiveAsync(ZipArchive zip, CancellationToken cancellationToken = default)
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            if (_unavailable || session is null || session.TimedOut) return;
            // Claim export before awaiting the writer, so Start cannot replace the session in
            // the gap between the snapshot and StopAsync, or while files are being copied.
            _exports++;
        }
        try
        {
            await StopAsync().ConfigureAwait(false);
            FileArtifact[] files;
            lock (_gate)
            {
                if (!ReferenceEquals(session, _session) || !session.Writer.IsCompleted) return;
                files = session.Artifacts.ToArray();
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var destination = zip.CreateEntry("network/" + file.Metadata.Name).Open();
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { lock (_gate) _exports--; }
    }

    private void Tick(Session session)
    {
        lock (_gate)
            if (session.State == NetworkDiagnosticRecorderState.Recording && Stopwatch.GetElapsedTime(session.StartedTimestamp) >= _duration)
                FinishLocked(session, "time-limit");
        NotifyChanged();
    }

    private static void FinishLocked(Session session, string reason)
    {
        session.State = NetworkDiagnosticRecorderState.Stopping;
        session.Reason = reason;
        session.StoppedAt = DateTimeOffset.UtcNow;
        session.Timer?.Dispose();
        session.Queue.Writer.TryComplete();
    }

    private async Task WriteSession(Session session)
    {
        var outputs = new Dictionary<(long Generation, uint LinkType), (BinaryWriter Writer, FileArtifact Artifact)>();
        var failed = false;
        try
        {
            await foreach (var packet in session.Queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                lock (_gate) session.QueuedBytes -= packet.Data.Length;
                var key = (packet.Generation, packet.LinkType);
                if (!outputs.TryGetValue(key, out var output))
                {
                    Directory.CreateDirectory(session.Directory);
                    var name = $"game-{outputs.Count + 1:000}.pcap";
                    var path = Path.Combine(session.Directory, name);
                    var writer = new BinaryWriter(_createFile(path));
                    var artifact = new FileArtifact(path, new(name, 0, 24, packet.LinkType, packet.Generation));
                    outputs.Add(key, (writer, artifact));
                    // Classic PCAP, little endian, microsecond timestamps, complete unmodified frames.
                    writer.Write(0xa1b2c3d4u); writer.Write((ushort)2); writer.Write((ushort)4);
                    writer.Write(0); writer.Write(0u); writer.Write((uint)MaxPacketBytes); writer.Write(packet.LinkType);
                    output = (writer, artifact);
                    lock (_gate) session.Bytes += 24;
                }
                output.Writer.Write(packet.Seconds); output.Writer.Write(packet.Microseconds);
                output.Writer.Write((uint)packet.Data.Length); output.Writer.Write(packet.OriginalLength);
                output.Writer.Write(packet.Data);
                lock (_gate)
                {
                    session.Packets++;
                    session.Bytes += 16 + packet.Data.Length;
                    output.Artifact.Metadata = output.Artifact.Metadata with
                        { Packets = output.Artifact.Metadata.Packets + 1, Bytes = output.Artifact.Metadata.Bytes + 16 + packet.Data.Length };
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            failed = true;
        }
        finally
        {
            foreach (var output in outputs.Values)
            {
                try { output.Writer.Dispose(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { failed = true; }
            }
            lock (_gate)
            {
                session.Timer?.Dispose();
                session.Queue.Writer.TryComplete();
                while (session.Queue.Reader.TryRead(out _)) session.Dropped++;
                session.QueuedBytes = 0;
                session.StoppedAt ??= DateTimeOffset.UtcNow;
                failed |= session.TimedOut;
                session.State = failed ? NetworkDiagnosticRecorderState.Failed : NetworkDiagnosticRecorderState.Completed;
                if (failed && !session.TimedOut) session.Reason = "write-error";
                // Partial files from a failed writer are never exported as valid captures.
                if (!failed) session.Artifacts.AddRange(outputs.Values.Select(value => value.Artifact));
            }
            NotifyChanged();
        }
    }

    private void NotifyChanged() => ThreadPool.QueueUserWorkItem(_ =>
    {
        foreach (Action callback in Changed?.GetInvocationList() ?? [])
            try { callback(); } catch (Exception) { /* A UI listener cannot affect capture or its writer. */ }
    });

    private sealed record RecordedPacket(long Generation, uint LinkType, uint Seconds, uint Microseconds, uint OriginalLength, byte[] Data);
    private sealed class FileArtifact(string path, NetworkDiagnosticArtifact metadata)
    {
        public string Path { get; } = path;
        public NetworkDiagnosticArtifact Metadata { get; set; } = metadata;
    }
    private sealed class Session(string directory, int capacity, long id)
    {
        public long Id { get; } = id;
        public long StartedTimestamp { get; } = Stopwatch.GetTimestamp();
        public string Directory { get; } = directory;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StoppedAt;
        public NetworkDiagnosticRecorderState State = NetworkDiagnosticRecorderState.Recording;
        public string? Reason;
        public bool TimedOut;
        public long Packets, Bytes, Dropped, ReservedBytes, QueuedBytes, Boundaries;
        public HashSet<(long Generation, uint LinkType)> Segments { get; } = [];
        public List<FileArtifact> Artifacts { get; } = [];
        public Timer? Timer;
        public Task Writer = Task.CompletedTask;
        public Channel<RecordedPacket> Queue { get; } = Channel.CreateBounded<RecordedPacket>(new BoundedChannelOptions(capacity)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    }
}
