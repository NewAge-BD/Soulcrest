using System.Text;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public interface INetworkLootSource
{
    bool Running { get; }
    string Status { get; }
    bool StatusIsError { get; }
    string? Connection { get; }
    long Packets { get; }
    long Messages { get; }
    IReadOnlyList<int> UnknownPets { get; }
    event Action? Changed;
    event Action<NetworkSoul>? SoulGained;
    void Start();
    void Stop();
}

/// <summary>A soul pickup read from the game's network stream.</summary>
public sealed record NetworkSoul(DateTimeOffset At, int PetNumber, string PetId, string Name, int Souls);

/// <summary>Accepted sources for the currently bound own-character session, read atomically by the UI.</summary>
public sealed record NetworkProgressSources(bool IdentityIsLive, string? Character, DateTimeOffset? PetBaselineAt,
    DateTimeOffset? QuestBaselineAt, DateTimeOffset? TeleportBaselineAt, DateTimeOffset? PetActivityAt, string Status,
    bool PetFormatRejected = false, bool QuestFormatRejected = false, bool TeleportFormatRejected = false,
    DateTimeOffset? DungeonActivityAt = null, bool DungeonFormatRejected = false,
    DateTimeOffset? KibeliskActivityAt = null, bool KibeliskFormatRejected = false)
{
    public bool HasAnyBaseline => PetBaselineAt is not null || QuestBaselineAt is not null || TeleportBaselineAt is not null;
    public bool HasAllBaselines => PetBaselineAt is not null && QuestBaselineAt is not null && TeleportBaselineAt is not null;
    public bool InitialProgressConfirmed => IdentityIsLive && HasAllBaselines && !PetFormatRejected
        && !QuestFormatRejected && !TeleportFormatRejected && !DungeonFormatRejected && !KibeliskFormatRejected;
}

/// <summary>
/// Network loot source (default enabled, docs/SAFETY.md, docs/NETWORK_LOOT.md): reads the server → client
/// stream of the game's own connections via Npcap, read-only, nothing decrypted, nothing sent. Every
/// soul message (0d90) adds its souls to the progress. Follows connection changes (zone change,
/// reconnect) by looking at the game's connections every few seconds.
/// </summary>
public sealed class NetworkLootService(ProgressService progress, ExplorationService? exploration = null) : INetworkLootSource, IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly object _connectionGate = new();
    private readonly Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> _streams = [];
    private IReadOnlySet<CaptureEndpoint> _installedCoverage = new HashSet<CaptureEndpoint>();
    private readonly List<NetworkSoul> _recent = [];
    private ILiveDevice? _device;
    private CaptureTarget? _target;
    private System.Threading.Timer? _check;
    private static readonly TimeSpan EntryCandidateLifetime = TimeSpan.FromMinutes(2);
    // A tentative login transaction, never an identity. Only a matching complete Own after one
    // Prepare can authorize its fresh full snapshots; unrelated/unanchored prefixes are discarded.
    private sealed class EntryCandidate(string stream, long characterId, int entityKey, DateTimeOffset startedAt)
    {
        public string Stream { get; } = stream;
        public long CharacterId { get; } = characterId;
        public int EntityKey { get; } = entityKey;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public bool Prepared { get; set; }
        public List<(byte[] Message, DateTimeOffset At)> Baselines { get; } = [];
        public HashSet<ushort> Sources { get; } = [];
        public int Bytes { get; set; }
    }
    private EntryCandidate? _entryCandidate;
    private bool _pendingProgressOverflow;
    private string? _identityStream;
    private OwnCharacterIdentity? _identity;
    private long _progressGeneration;
    private long _progressSequence;
    private bool _petFormatRejected, _questFormatRejected, _teleportFormatRejected, _dungeonFormatRejected, _kibeliskFormatRejected;
    private DateTimeOffset? _dungeonActivityAt, _kibeliskActivityAt;
    private long _identityBindings, _contextResets, _rejectedProgressMessages, _acceptedSoulMessages, _acceptedSoulPoints;
    private string _lastResetReason = "none";
    private DateTimeOffset? _lastResetAt, _lastRejectedAt, _lastSoulAt;
    private ushort? _lastRejectedOpcode;

    /// <summary>Anonymous, process-lifetime counters; no identities, endpoint keys or payloads.</summary>
    internal NetworkProgressDiagnostics ProgressDiagnostics
    {
        get
        {
            lock (_gate) return new(_identity is not null, _identityBindings, _contextResets, _lastResetReason, _lastResetAt,
                _rejectedProgressMessages, _lastRejectedOpcode, _lastRejectedAt, _acceptedSoulMessages, _acceptedSoulPoints,
                _lastSoulAt, _entryCandidate?.Baselines.Count ?? 0, _entryCandidate?.Bytes ?? 0, _pendingProgressOverflow);
        }
    }

    public DateTimeOffset? PetBaselineAt { get; private set; }
    public DateTimeOffset? QuestBaselineAt { get; private set; }
    public DateTimeOffset? TeleportBaselineAt { get; private set; }
    private DateTimeOffset? _petActivityAt;
    /// <summary>A baseline remains current during its uninterrupted bound session; it is not a periodic heartbeat.</summary>
    public NetworkProgressSources ProgressSources
    {
        get
        {
            lock (_gate) return new(Running && _identity is not null, Character, PetBaselineAt,
                QuestBaselineAt, TeleportBaselineAt, _petActivityAt, ProgressStatus,
                _petFormatRejected, _questFormatRejected, _teleportFormatRejected, _dungeonActivityAt, _dungeonFormatRejected,
                _kibeliskActivityAt, _kibeliskFormatRejected);
        }
    }
    public bool ProgressIsLive => ProgressSources is { IdentityIsLive: true, HasAnyBaseline: true };
    public string ProgressStatus { get; private set; } = "Warte auf die eigene Charaktererkennung.";

    public bool Running { get; private set; }
    public string Status { get; private set; } = "Netzwerk-Quelle aus.";
    public bool StatusIsError { get; private set; }
    public string? Connection => _target?.Description;
    public long Packets { get; private set; }
    public long Messages { get; private set; }
    public int Resyncs { get; private set; }
    public IReadOnlyList<int> UnknownPets { get; private set; } = [];

    public event Action? Changed;

    /// <summary>A soul was counted (also for the session totals of the loot tracker).</summary>
    public event Action<NetworkSoul>? SoulGained;

    /// <summary>The own character entered the world (after a loading screen): its name.</summary>
    public event Action<string>? CharacterEntered;
    public event Action<OwnCharacterIdentity>? OwnCharacterEntered;

    /// <summary>Only unencrypted clock/boss messages; the stream key is transient and never persisted.</summary>
    public event Action<string, byte[]>? BossMessageReceived;
    public event Action? BossSessionReset;
    public event Action<string>? BossStreamGap;
    private long _generation;
    private sealed record ReceivedMessage(string Stream, byte[] Data, bool Gap = false);

    /// <summary>The own character seen last after a loading screen, null before the first one.</summary>
    public string? Character { get; private set; }

    public IReadOnlyList<NetworkSoul> Recent
    {
        get { lock (_gate) return [.. _recent]; }
    }

    /// <summary>Whether Npcap is installed (its wpcap.dll in System32\Npcap).</summary>
    public static bool NpcapInstalled =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll"));

    public void Start()
    {
        lock (_connectionGate) StartCore();
    }

    private void StartCore()
    {
        if (Running)
            return;
        if (!NpcapInstalled)
        {
            SetStatus("Npcap ist nicht installiert (npcap.com). Ohne Npcap kann die Netzwerk-Quelle nichts lesen.", error: true);
            return;
        }
        Running = true;
        Packets = Messages = 0;
        SetStatus("Suche die Verbindung des Spiels …");
        _check = new System.Threading.Timer(_ => CheckConnection(), null, TimeSpan.Zero, CheckEvery);
    }

    public void Stop()
    {
        lock (_connectionGate) StopCore();
    }

    private void StopCore()
    {
        Running = false;
        _check?.Dispose();
        _check = null;
        CloseDevice();
        SetStatus("Netzwerk-Quelle gestoppt.");
    }

    /// <summary>Reads a recorded capture file (diagnosis/tests); returns the souls found, changes nothing.</summary>
    public static List<(DateTimeOffset At, SoulGain Gain)> ReadCaptureFile(string path, IReadOnlySet<int> serverPorts)
    {
        var found = new List<(DateTimeOffset, SoulGain)>();
        var streams = new Dictionary<(string, int, int), (TcpReassembler, GameMessageStream)>();
        using var device = new CaptureFileReaderDevice(path);
        device.Open();
        while (device.GetNextPacket(out var capture) == GetPacketStatus.PacketRead)
        {
            var raw = capture.GetPacket();
            var at = new DateTimeOffset(raw.Timeval.Date);
            foreach (var received in Feed(streams, raw, serverPorts, null))
            {
                if (LootMessage.TryParse(received.Data) is { } gains)
                    found.AddRange(gains.Select(g => (at, g)));
            }
        }
        return found;
    }

    /// <summary>Characters entering the world in a recorded capture file (diagnosis/tests).</summary>
    public static List<(DateTimeOffset At, string Name)> ReadCharacters(string path, IReadOnlySet<int> serverPorts)
    {
        var found = new List<(DateTimeOffset, string)>();
        var streams = new Dictionary<(string, int, int), (TcpReassembler, GameMessageStream)>();
        using var device = new CaptureFileReaderDevice(path);
        device.Open();
        while (device.GetNextPacket(out var capture) == GetPacketStatus.PacketRead)
        {
            var raw = capture.GetPacket();
            foreach (var received in Feed(streams, raw, serverPorts, null))
            {
                if (CharacterMessage.TryParseName(received.Data) is { } name)
                    found.Add((new DateTimeOffset(raw.Timeval.Date), name));
            }
        }
        return found;
    }

    private void CheckConnection()
    {
        // Timer callbacks may overlap. Stop and connection replacement share this gate.
        lock (_connectionGate) CheckConnectionCore();
    }

    internal static bool SameConnection(CaptureTarget current, CaptureTarget next) =>
        current.Device == next.Device && current.Filter == next.Filter
        && current.GamePorts.SetEquals(next.GamePorts) && current.ServerPorts.SetEquals(next.ServerPorts)
        && (current.Connections is null && next.Connections is null ||
            current.Connections is { } own && next.Connections is { } nextOwn && own.SetEquals(nextOwn));

    /// <summary>Local-port changes need no filter update when the installed remote coverage already includes them.</summary>
    internal static bool CanRetainCapture(CaptureTarget current, CaptureTarget next, IReadOnlySet<CaptureEndpoint> installedCoverage) =>
        string.Equals(current.Device, next.Device, StringComparison.OrdinalIgnoreCase) &&
        current.Connections is not null && next.Connections is { Count: > 0 } &&
        next.RemoteEndpoints.All(installedCoverage.Contains);

    /// <summary>Updates ownership atomically without touching the live handle or surviving partial frames.</summary>
    internal bool TryRetainCapture(CaptureTarget next)
    {
        lock (_gate)
        {
            if (_target is not { } current || !CanRetainCapture(current, next, _installedCoverage)) return false;
            var removed = current.Connections!.Except(next.Connections!).ToArray();
            _target = next;
            foreach (var connection in removed)
            {
                _streams.Remove(connection);
                ProcessProgressGap(connection.StreamKey);
            }
            return true;
        }
    }

    private void CheckConnectionCore()
    {
        if (!Running)
            return;
        try
        {
            var target = CaptureTarget.Find();
            if (target is null)
            {
                if (_target is not null)
                    CloseDevice();
                SetStatus("Keine Verbindung von AION2 gefunden – läuft das Spiel?");
                return;
            }
            if (_target is { } current && SameConnection(current, target))
                return;
            if (TryRetainCapture(target))
            {
                SetStatus($"Liest den Spielverkehr {target.Description}.");
                return;
            }
            CloseDevice();
            OpenDevice(target);
        }
        catch (Exception exception) when (exception is PcapException or DllNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            CloseDevice();
            SetStatus("Mitschnitt nicht möglich: " + exception.Message, error: true);
        }
    }

    private void OpenDevice(CaptureTarget target)
    {
        var device = CaptureDeviceList.New().FirstOrDefault(d => string.Equals(d.Name, target.Device, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Npcap-Gerät {target.Device} nicht gefunden (Loopback-Unterstützung von Npcap aktiv?).");
        device.OnPacketArrival += OnPacketArrival;
        lock (_gate)
        {
            _device = device;
            _target = target;
            _installedCoverage = target.RemoteEndpoints.ToHashSet();
            _streams.Clear();
        }
        // Register before capture starts; CloseDevice also cleans up a failed open.
        device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = 250 });
        device.Filter = target.Filter;
        device.StartCapture();
        SetStatus($"Liest den Spielverkehr {target.Description}.");
    }

    private void CloseDevice()
    {
        ILiveDevice? device;
        lock (_gate)
        {
            device = _device;
            _device = null;
            _target = null;
            _installedCoverage = new HashSet<CaptureEndpoint>();
            _streams.Clear();
            _generation++;
            ResetProgressContext(Running ? "capture-closed" : "capture-stopped");
        }
        BossSessionReset?.Invoke();
        if (device is null)
            return;
        try
        {
            device.StopCapture();
            device.Close();
        }
        catch (PcapException)
        {
            // already closed
        }
        device.OnPacketArrival -= OnPacketArrival;
    }

    private void OnPacketArrival(object sender, PacketCapture capture)
    {
        var raw = capture.GetPacket();
        lock (_gate)
        {
            if (!Running || !ReferenceEquals(sender, _device)) return;
            ProcessCapturedPacket(raw);
        }
    }

    /// <summary>Strict live dispatch, also usable with synthetic packets without opening a capture device.</summary>
    internal void ProcessCapturedPacket(RawCapture raw)
    {
        // Keep Feed and dispatch under the same gate as target refresh. An already decoded identity
        // from a removed connection must not be rebound after its scoped invalidation.
        lock (_gate)
        {
            if (!Running || _target is not { } target) return;
            Packets++;
            var messages = FeedOwn(_streams, raw, target);
            Messages += messages.Count(message => message.Data.Length > 0);
            Resyncs = _streams.Values.Sum(stream => stream.Messages.Resyncs);
            var generation = _generation;
            foreach (var received in messages)
            {
                if (!Running || generation != _generation) break;
                // Event handlers may refresh ownership reentrantly on this thread as well.
                if (received.Stream != "*" && _target?.Connections?.Any(connection => connection.StreamKey == received.Stream) != true) break;
                if (received.Gap)
                {
                    ProcessProgressGap(received.Stream);
                    continue;
                }
                ProcessProgressMessage(received.Stream, received.Data, DateTimeOffset.UtcNow);
                var message = received.Data;
                if (message.Length >= 2 && (message[0] == 0 && message[1] == 0x36 || message[0] == 1 && message[1] == 0x91))
                    BossMessageReceived?.Invoke(received.Stream, message);
            }
        }
    }

    private static List<ReceivedMessage> FeedOwn(Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> streams,
        RawCapture raw, CaptureTarget target)
    {
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        if (packet.Extract<TcpPacket>() is not { } tcp || packet.Extract<IPPacket>() is not { } ip || target.Connections is null)
            return [];
        foreach (var connection in target.Connections)
            if (connection.MatchesIncoming(ip.SourceAddress, tcp.SourcePort, ip.DestinationAddress, tcp.DestinationPort))
                return FeedPacket(streams, tcp, connection, connection.StreamKey);
        return [];
    }

    /// <summary>Server → game TCP payload of one packet through reassembly and message splitting.</summary>
    private static List<ReceivedMessage> Feed(Dictionary<(string, int, int), (TcpReassembler Tcp, GameMessageStream Messages)> streams, RawCapture raw,
        IReadOnlySet<int> serverPorts, IReadOnlySet<int>? gamePorts)
    {
        var result = new List<ReceivedMessage>();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        if (packet.Extract<TcpPacket>() is not { } tcp || packet.Extract<IPPacket>() is not { } ip)
            return result;
        if (!serverPorts.Contains(tcp.SourcePort) || gamePorts is not null && !gamePorts.Contains(tcp.DestinationPort))
            return result;
        var key = (ip.SourceAddress.ToString(), (int)tcp.SourcePort, (int)tcp.DestinationPort);
        return FeedPacket(streams, tcp, key, $"{key.Item1}:{key.Item2}:{key.Item3}");
    }

    private static List<ReceivedMessage> FeedPacket<TKey>(Dictionary<TKey, (TcpReassembler Tcp, GameMessageStream Messages)> streams,
        TcpPacket tcp, TKey key, string streamKey) where TKey : notnull
    {
        var result = new List<ReceivedMessage>();
        // A reset has no deliverable tail. Report closure even when the control packet has no payload.
        if (tcp.Reset)
        {
            streams.Remove(key);
            result.Add(new ReceivedMessage(streamKey, [], Gap: true));
            return result;
        }
        var payload = tcp.PayloadData;
        if (payload is null || payload.Length == 0)
        {
            if (tcp.Finished)
            {
                streams.Remove(key);
                result.Add(new ReceivedMessage(streamKey, [], Gap: true));
            }
            return result;
        }
        if (!streams.TryGetValue(key, out var stream))
        {
            // A capture may span many reconnects; old TCP contexts must not accumulate forever.
            if (streams.Count >= 64)
            {
                streams.Clear();
                result.Add(new ReceivedMessage("*", [], Gap: true));
            }
            streams[key] = stream = (new TcpReassembler(), new GameMessageStream(requireVerifiedSync: true));
        }
        foreach (var chunk in stream.Tcp.Add(tcp.SequenceNumber, payload))
        {
            if (chunk.Gap)
            {
                stream.Messages.Break();
                result.Add(new ReceivedMessage(streamKey, [], Gap: true));
            }
            if (chunk.Data.Length > 0)
            {
                var before = (stream.Messages.Resyncs, stream.Messages.ContinuityBreaks);
                var decoded = stream.Messages.Append(chunk.Data);
                if (stream.Messages.Resyncs > before.Resyncs || stream.Messages.ContinuityBreaks > before.ContinuityBreaks)
                    result.Add(new ReceivedMessage(streamKey, [], Gap: true));
                result.AddRange(decoded.Select(message => new ReceivedMessage(streamKey, message)));
            }
        }
        // FIN may carry the final complete messages; their resulting state is invalidated afterwards.
        if (tcp.Finished)
        {
            streams.Remove(key);
            result.Add(new ReceivedMessage(streamKey, [], Gap: true));
        }
        return result;
    }

    /// <summary>Invalidates the bound stream immediately and refreshes the passive status card.</summary>
    internal void ProcessProgressGap(string stream)
    {
        // Boss schedules can belong to a different stream than the character baseline.
        BossStreamGap?.Invoke(stream);
        lock (_gate)
        {
            if (stream != "*" && _identityStream != stream && _entryCandidate?.Stream != stream) return;
            ResetProgressContext("stream-gap");
            SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
        }
    }

    /// <summary>Pure message dispatch used by the live source and isolated integration tests.</summary>
    internal void ProcessProgressMessage(string stream, byte[] message, DateTimeOffset at)
    {
        lock (_gate)
        {
            ExpireEntryCandidate(at);
            if (message.Length < 2) return;
            var opcode = (ushort)(message[0] | message[1] << 8);
            if (opcode == 0x3620)
            {
                // An unknown entry shape also suspends old authority on this same stream. It must
                // never let the next character's snapshots mutate the previously bound profile.
                if (_identity is not null && _identityStream != stream ||
                    _entryCandidate is { } other && other.Stream != stream) return;
                ResetProgressContext("world-entry-candidate", at);
                if (OwnIdentityMessage.TryParseEntryCandidate(message) is { } tentative)
                    _entryCandidate = new(stream, tentative.CharacterId, tentative.EntityKey, at);
                else
                {
                    RecordProgressRejection(opcode, at);
                    ProgressStatus = "Netzwerkformat nicht unterstützt. Gespeicherter Fortschritt bleibt erhalten.";
                }
                SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
                return;
            }
            if (OwnIdentityMessage.IsWorldEntryPreparation(message))
            {
                if (_identity is not null && _identityStream != stream ||
                    _entryCandidate is { } other && other.Stream != stream) return;
                var candidate = _entryCandidate;
                ResetProgressContext("world-entry-preparation", at);
                // This is deliberately not a generic prefix carry: only the separately anchored
                // transaction survives its first Prepare, and a second Prepare discards it.
                if (candidate is { Prepared: false })
                {
                    candidate.Prepared = true;
                    _entryCandidate = candidate;
                }
                BossSessionReset?.Invoke();
                SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
                return;
            }
            if (OwnIdentityMessage.TryParse(message) is { } identity)
            {
                ObserveIdentity(stream, identity, at);
                return;
            }
            if (opcode is 0x3633 or 0x3621)
            {
                if (_identityStream == stream || _entryCandidate?.Stream == stream)
                {
                    RecordProgressRejection(opcode, at);
                    ResetProgressContext("unsupported-own-entry", at);
                    ProgressStatus = "Netzwerkformat nicht unterstützt. Gespeicherter Fortschritt bleibt erhalten.";
                    SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
                }
                return;
            }
            if (opcode is not (0x9000 or 0x9009 or 0x900D or 0x900E or 0x9232 or 0x9233 or 0x8D09 or 0x6101))
                return;
            if (_identity is null)
            {
                BufferEntryBaseline(stream, opcode, message, at);
                return;
            }
            if (_identityStream != stream)
                return;
            var context = new NetworkProgressContext(_identity, _progressGeneration, ++_progressSequence, at);
            var accepted = false;
            var parsed = false;
            switch (opcode)
            {
                case 0x9000 when PetProgressMessage.TryParseSnapshot(message) is { } snapshot:
                    parsed = true;
                    accepted = progress.ApplyNetworkSnapshot(snapshot, context);
                    if (accepted) { PetBaselineAt = at; _petFormatRejected = false; }
                    break;
                case 0x9009 when PetProgressMessage.TryParseAcquired(message) is { } acquired:
                    parsed = true;
                    if (acquired.EntityKey == _identity.EntityKey)
                        accepted = progress.ApplyNetworkAcquisition(acquired, context);
                    break;
                case 0x900D when LootMessage.TryParse(message) is { } gains:
                    parsed = true;
                    accepted = progress.ApplyNetworkGain(gains, context);
                    if (accepted)
                    {
                        _acceptedSoulMessages++;
                        _acceptedSoulPoints += gains.Sum(gain => (long)gain.Souls);
                        _lastSoulAt = at;
                        Count(gains, at);
                    }
                    break;
                case 0x900E when PetProgressMessage.TryParseSpent(message) is { } spent:
                    parsed = true;
                    accepted = progress.ApplyNetworkSpent(spent, context);
                    break;
                case 0x9232 when ExplorationProgressMessage.TryParseTeleports(message) is { } teleports:
                    parsed = true;
                    accepted = exploration?.ApplyNetworkTeleports(teleports, context) == true;
                    if (accepted) { TeleportBaselineAt = at; _teleportFormatRejected = false; }
                    break;
                case 0x9233 when ExplorationProgressMessage.TryParseTeleportBinding(message) is { } teleportId:
                    parsed = true;
                    accepted = exploration?.ApplyNetworkTeleportBinding(teleportId, context) == true;
                    if (accepted) _kibeliskActivityAt = at;
                    break;
                case 0x8D09 when ExplorationProgressMessage.TryParseQuests(message) is { } quests:
                    parsed = true;
                    accepted = exploration?.ApplyNetworkQuests(quests, context) == true;
                    if (accepted) { QuestBaselineAt = at; _questFormatRejected = false; }
                    break;
                case 0x6101 when DungeonProgressMessage.TryParseCompletion(message) is { } completion:
                    parsed = true;
                    accepted = exploration?.ApplyNetworkDungeonCompletion(completion, context) == true;
                    if (accepted) _dungeonActivityAt = at;
                    break;
            }
            if (!parsed)
            {
                // Reject an unknown domain schema without losing the independently verified own
                // identity. Only this source's confirmation is invalid; later valid gains still count.
                RecordProgressRejection(opcode, at);
                switch (opcode)
                {
                    case 0x8D09:
                        QuestBaselineAt = null;
                        _questFormatRejected = true;
                        exploration?.MarkNetworkQuestSourceStale();
                        break;
                    case 0x9232:
                        TeleportBaselineAt = null;
                        _teleportFormatRejected = true;
                        exploration?.MarkNetworkTeleportSourceStale();
                        break;
                    case 0x9233:
                        // An unsupported singleton cannot undo confirmed bindings or a full list.
                        _kibeliskFormatRejected = true;
                        break;
                    case 0x6101:
                        // A rejected result cannot undo a previously proven one-time completion.
                        _dungeonFormatRejected = true;
                        break;
                    default:
                        PetBaselineAt = null;
                        _petFormatRejected = true;
                        progress.MarkNetworkPetSourceStale();
                        break;
                }
                UpdateProgressStatus();
                SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
                return;
            }
            if (accepted)
            {
                if (opcode is 0x9000 or 0x9009 or 0x900D or 0x900E) _petActivityAt = at;
                UpdateProgressStatus();
                SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
            }
        }
    }

    private void RecordProgressRejection(int opcode, DateTimeOffset at)
    {
        _rejectedProgressMessages++;
        _lastRejectedOpcode = (ushort)opcode;
        _lastRejectedAt = at;
    }

    private void UpdateProgressStatus()
    {
        // A delta/other source cannot repair an unknown full baseline.
        ProgressStatus = _petFormatRejected
            ? "Pet-Netzwerkformat nicht unterstützt. Neue Soul-Gewinne werden weiter erfasst; der vollständige Pet-Stand muss erneut empfangen werden."
            : _questFormatRejected
                ? "Quest-Netzwerkformat nicht unterstützt. Pet-Erkennung läuft weiter."
                : _teleportFormatRejected
                    ? "Teleport-Netzwerkformat nicht unterstützt. Pet-Erkennung läuft weiter."
                    : _dungeonFormatRejected
                        ? "Netzwerkformat für Instanzabschlüsse nicht unterstützt. Bereits bestätigte Abschlüsse bleiben erhalten."
                        : _kibeliskFormatRejected
                            ? "Netzwerkformat für Kibelisk-Bindungen nicht unterstützt. Bereits bestätigte Bindungen bleiben erhalten."
                            : PetBaselineAt is not null && QuestBaselineAt is not null && TeleportBaselineAt is not null
                                ? "Die verfügbaren Daten zu Sealed Dungeons, Strongholds, Kibelisken und Pets wurden für diesen Charakter eingelesen."
                                : PetBaselineAt is not null || QuestBaselineAt is not null || TeleportBaselineAt is not null
                                    ? "Ein Teil der Netzwerkdaten wurde empfangen. Weitere Quellen stehen noch aus."
                                    : _kibeliskActivityAt is not null
                                        ? "Kibelisk-Bindung über das Netzwerk erfasst. Weitere Erkundungsdaten stehen noch aus."
                                        : _dungeonActivityAt is not null
                                            ? "Instanzabschluss über das Netzwerk erfasst. Weitere Erkundungsdaten stehen noch aus."
                                            : "Pet-Daten empfangen. Der vollständige Pet-Stand steht noch aus.";
    }

    private void ExpireEntryCandidate(DateTimeOffset at)
    {
        if (_entryCandidate is not { } candidate || at >= candidate.StartedAt && at - candidate.StartedAt <= EntryCandidateLifetime) return;
        ResetProgressContext("entry-candidate-expired", at);
        SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
    }

    private void BufferEntryBaseline(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (_entryCandidate is not { } candidate || candidate.Stream != stream || _pendingProgressOverflow ||
            opcode is not (0x9000 or 0x8D09 or 0x9232)) return;
        // One candidate globally and one packet per full source are stricter than the former
        // eight-stream/256-message/4 MiB caps; keep the former 2 MiB per-stream bound as well.
        if (candidate.Sources.Contains(opcode))
        {
            ResetProgressContext("duplicate-entry-baseline", at);
            SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
            return;
        }
        if (candidate.Baselines.Count >= 256 || message.Length > 2 * 1024 * 1024 - candidate.Bytes ||
            message.Length > 4 * 1024 * 1024 - candidate.Bytes)
        {
            OverflowPending();
            return;
        }
        var decoded = opcode switch
        {
            0x9000 => PetProgressMessage.TryParseSnapshot(message) is not null,
            0x8D09 => ExplorationProgressMessage.TryParseQuests(message) is not null,
            0x9232 => ExplorationProgressMessage.TryParseTeleports(message) is not null,
            _ => false,
        };
        if (!decoded)
        {
            RecordProgressRejection(opcode, at);
            ResetProgressContext("unsupported-entry-baseline", at);
            ProgressStatus = "Netzwerkformat nicht unterstützt. Gespeicherter Fortschritt bleibt erhalten.";
            SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
            return;
        }
        candidate.Baselines.Add((message.ToArray(), at));
        candidate.Sources.Add(opcode);
        candidate.Bytes += message.Length;
        SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
    }

    private void OverflowPending()
    {
        _entryCandidate = null;
        _pendingProgressOverflow = true;
        ProgressStatus = "Charakter noch unbekannt. Bitte einmal die Charakterauswahl öffnen und den Charakter erneut betreten oder den Charakter wechseln.";
        SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
    }

    internal void ObserveIdentity(string stream, OwnCharacterIdentity identity, DateTimeOffset at)
    {
        lock (_gate)
        {
            ExpireEntryCandidate(at);
            // The full Own supplies the server/name and must confirm the tentative character id.
            // No arbitrary prefix, delta or previous transaction is ever replayed on a new Own.
            var pending = _entryCandidate is { Prepared: true } candidate && candidate.Stream == stream &&
                candidate.CharacterId == identity.CharacterId && candidate.EntityKey == identity.EntityKey
                ? candidate.Baselines.ToArray() : [];
            ResetProgressContext("world-entry", at);
            _identity = identity;
            _identityStream = stream;
            Character = identity.Name;
            var context = new NetworkProgressContext(identity, _progressGeneration, 0, at);
            if (!progress.BindNetworkContext(context))
            {
                ResetProgressContext("identity-bind-rejected", at);
                return;
            }
            exploration?.BindNetworkContext(context);
            _identityBindings++;
            ProgressStatus = "Charakter erkannt. Warte auf Netzwerk-Fortschrittsdaten.";
            BossSessionReset?.Invoke();
            OwnCharacterEntered?.Invoke(identity);
            CharacterEntered?.Invoke(identity.Name);
            foreach (var item in pending)
            {
                if (_progressGeneration != context.Generation || _identity != identity || _identityStream != stream) break;
                ProcessProgressMessage(stream, item.Message, item.At);
            }
            SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
        }
    }

    internal void ResetProgressContext(string reason = "manual", DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            _contextResets++;
            _lastResetReason = reason;
            _lastResetAt = at ?? DateTimeOffset.UtcNow;
            _petFormatRejected = _questFormatRejected = _teleportFormatRejected = _dungeonFormatRejected = _kibeliskFormatRejected = false;
            _dungeonActivityAt = _kibeliskActivityAt = null;
            _progressGeneration++;
            _progressSequence = 0;
            _identity = null;
            _identityStream = null;
            Character = null;
            _entryCandidate = null;
            _pendingProgressOverflow = false;
            PetBaselineAt = QuestBaselineAt = TeleportBaselineAt = null;
            _petActivityAt = null;
            progress.MarkNetworkStale();
            exploration?.MarkNetworkStale();
            ProgressStatus = "Warte auf die eigene Charaktererkennung.";
        }
    }

    private void Count(IReadOnlyList<SoulGain> gains, DateTimeOffset at)
    {
        var unknown = new List<int>();
        foreach (var gain in gains)
        {
            var entry = PetIdTable.Find(gain.PetNumber);
            var name = entry?.En ?? $"Pet #{gain.PetNumber}";
            var petId = entry is not null ? progress.EnsureNetworkPet(entry) : null;
            if (petId is null)
            {
                unknown.Add(gain.PetNumber);
                continue;
            }
            var soul = new NetworkSoul(at, gain.PetNumber, petId, name, gain.Souls);
            lock (_gate)
            {
                _recent.Insert(0, soul);
                if (_recent.Count > 100)
                    _recent.RemoveAt(_recent.Count - 1);
            }
            SoulGained?.Invoke(soul);
        }
        if (unknown.Count > 0)
            UnknownPets = [.. UnknownPets.Union(unknown)];
        SafeEvent.Raise(Changed, "Loot-Tracker");
    }

    /// <summary>Soulcrest-style id for a pet missing in the map data ("Elite Ursus" → "elite-ursus").</summary>
    internal static string Slug(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}
