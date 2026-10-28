using System.Text;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Progress;

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
    public bool PetCanCount { get; init; } = IdentityIsLive;
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
public sealed partial class NetworkLootService(ProgressService progress, ExplorationService? exploration = null,
    WorldContextService? worldContext = null) : INetworkLootSource, INetworkMerchantSource,
    INetworkGatheringSource, INetworkKinaSource, INetworkFarmSource, INetworkAbyssSource, INetworkAbyssCombatSource, IDisposable
{
    public WorldContextService WorldContext { get; } = worldContext ?? new();
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly object _connectionGate = new();
    private int _disposed;
    private readonly Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> _streams = [];
    private IReadOnlySet<CaptureEndpoint> _installedCoverage = new HashSet<CaptureEndpoint>();
    private readonly List<NetworkSoul> _recent = [];
    private ILiveDevice? _device;
    // Filters are immutable after capture starts: Npcap clears the handle's packet buffer on
    // pcap_setfilter. Additional, disjoint game endpoints get an independent narrow handle.
    private readonly Dictionary<ILiveDevice, IReadOnlySet<CaptureEndpoint>> _additionalDevices = new(ReferenceEqualityComparer.Instance);
    private const int MaxCaptureHandles = 32;
    internal Func<string, ILiveDevice> CaptureDeviceFactory { get; set; } = name =>
        CaptureDeviceList.New().FirstOrDefault(device => string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Npcap-Gerät {name} nicht gefunden (Loopback-Unterstützung von Npcap aktiv?).");
    private CaptureTarget? _target;
    private System.Threading.Timer? _check;
    private static readonly TimeSpan IncomingProbeInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan IncomingPacketLifetime = TimeSpan.FromMilliseconds(250);
    private const int MaxIncomingFlows = 16, MaxIncomingPackets = 128, MaxIncomingBytes = 512 * 1024;
    private readonly record struct IncomingFlow(string RemoteAddress, int RemotePort, string LocalAddress, int LocalPort)
    {
        public bool Matches(CaptureConnection connection) => connection.RemoteAddress == RemoteAddress
            && connection.RemotePort == RemotePort && connection.LocalAddress == LocalAddress && connection.LocalPort == LocalPort;
    }
    private sealed record PendingIncoming(IncomingFlow Flow, RawCapture Raw, object Device, long Generation, DateTimeOffset At);
    private readonly List<PendingIncoming> _incomingPackets = [];
    // A flow remains a barrier while its current packet is being dispatched, including reentrant callbacks.
    private readonly HashSet<IncomingFlow> _incomingBarriers = [];
    private int _incomingBytes;
    private bool _incomingProbeRunning;
    private Task _incomingProbeTask = Task.CompletedTask;
    private DateTimeOffset? _lastIncomingProbeAt;
    private long _incomingProbes, _incomingDroppedPackets;
    private long _ownershipRevision;
    private Task _deviceLifecycleTask = Task.CompletedTask;
    internal Task DeviceLifecycleTask { get { lock (_gate) return _deviceLifecycleTask; } }
    internal Func<CaptureTarget?> IncomingOwnershipReader { get; set; } = CaptureTarget.Find;
    internal Func<CaptureTarget?> PeriodicOwnershipReader { get; set; } = CaptureTarget.Find;
    internal Func<DateTimeOffset> IncomingOwnershipUtcNow { get; set; } = () => DateTimeOffset.UtcNow;
    internal Func<TimeSpan, Task> IncomingOwnershipWait { get; set; } = delay => Task.Delay(delay);
    internal Task IncomingOwnershipTask { get { lock (_gate) return _incomingProbeTask; } }
    internal (int Flows, int Packets, int Bytes, bool ProbeRunning, long Probes, long DroppedPackets) IncomingOwnershipQueue
    {
        get { lock (_gate) return (_incomingBarriers.Count, _incomingPackets.Count, _incomingBytes,
            _incomingProbeRunning, _incomingProbes, _incomingDroppedPackets); }
    }
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
        public HashSet<ushort> RejectedSources { get; } = [];
        public CurrencyInventoryList? KinaList { get; set; }
        public DateTimeOffset? KinaAt { get; set; }
        public bool KinaSeen { get; set; }
        public bool FarmInventoryRejected { get; set; }
        public bool KinaRejected { get; set; }
        public DailyMonsterKinaObservation? DailyKina { get; set; }
        public DateTimeOffset? DailyKinaAt { get; set; }
        public bool DailyKinaSeen { get; set; }
        public bool DailyKinaRejected { get; set; }
        public int Bytes { get; set; }
    }
    private EntryCandidate? _entryCandidate;
    private bool _pendingProgressOverflow;
    private string? _identityStream;
    private OwnCharacterIdentity? _identity;
    private OwnCharacterContext? _ownCharacter;

    /// <summary>Atomic live own identity/class binding; null on every network context boundary.</summary>
    public OwnCharacterContext? OwnCharacter { get { lock (_gate) return _ownCharacter; } }
    private long _progressGeneration, _merchantGeneration;
    private long _progressSequence;
    private long _petGeneration, _petSequence;
    private readonly HashSet<string> _petStreams = new(StringComparer.Ordinal);
    private readonly HashSet<string> _merchantStreams = new(StringComparer.Ordinal);
    private bool _petDispatching;
    private readonly Queue<PendingPetMessage> _petDispatchQueue = new();
    private int _petDispatchBytes;
    private sealed record PendingPetMessage(string Stream, ushort Opcode, byte[] Message, DateTimeOffset At,
        long CaptureGeneration, long PetGeneration);
    // Monotone source revisions also cover rejected messages. Reentrant service/Own callbacks
    // must never let an older login-prefix snapshot overwrite a newer observation of that source.
    private readonly long[] _sourceRevisions = new long[3];
    private bool _petFormatRejected, _questFormatRejected, _teleportFormatRejected, _dungeonFormatRejected, _kibeliskFormatRejected;
    private DateTimeOffset? _dungeonActivityAt, _kibeliskActivityAt;
    private long _identityBindings, _contextResets, _rejectedProgressMessages, _acceptedSoulMessages, _acceptedSoulPoints;
    private readonly Queue<NetworkProgressDiagnosticEvent> _progressEvents = new();
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
                _lastSoulAt, _entryCandidate?.Baselines.Count ?? 0, _entryCandidate?.Bytes ?? 0, _pendingProgressOverflow)
            { Events = _progressEvents.ToArray(), UnboundSoulMessages = 0, UnboundSoulPoints = 0 };
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
                _kibeliskActivityAt, _kibeliskFormatRejected) { PetCanCount = Running };
        }
    }
    public bool ProgressIsLive => ProgressSources is { PetCanCount: true, PetBaselineAt: not null } or
        { IdentityIsLive: true, HasAnyBaseline: true };
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
    /// <summary>Own identity/class was bound or invalidated; read OwnCharacter atomically in the handler.</summary>
    public event Action? OwnCharacterChanged;

    /// <summary>Only unencrypted clock/boss messages; the stream key is transient and never persisted.</summary>
    public event Action<string, byte[]>? BossMessageReceived;
    public event Action<MerchantNetworkMessage>? MerchantMessageReceived;
    public event Action? MerchantSessionReset;
    public event Action? BossSessionReset;
    public event Action<string>? BossStreamGap;
    private long _generation;
    private sealed record ReceivedMessage(string Stream, byte[] Data, bool Gap = false, GameMessageStream? Transport = null,
        bool EndStream = false, string GapReason = "stream-gap");

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
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Monitor.IsEntered(_gate)) { QueueDeviceLifecycle(StartCore); return; }
        lock (_connectionGate) StartCore();
    }

    private void StartCore()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var npcapInstalled = NpcapInstalled;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || Running) return;
            if (!npcapInstalled)
            {
                SetStatus("Npcap ist nicht installiert (npcap.com). Ohne Npcap kann die Netzwerk-Quelle nichts lesen.", error: true);
                return;
            }
            Running = true;
            Packets = Messages = 0;
            // Install before notifying subscribers: a reentrant Stop/Dispose must also dispose
            // this timer, rather than allowing the outer Start to install one after that stop.
            _check = new System.Threading.Timer(_ => CheckConnection(), null, TimeSpan.Zero, CheckEvery);
            SetStatus("Suche die Verbindung des Spiels …");
        }
    }

    public void Stop()
    {
        // Synchronous subscribers run under _gate. Invalidate immediately, but never acquire
        // _connectionGate or join the capture callback from that reentrant call.
        if (Monitor.IsEntered(_gate)) { StopCore(); return; }
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

    private void CheckConnection()
    {
        CheckConnectionCore();
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

    /// <summary>Keep running filters unchanged; capture newly proven endpoints on a disjoint handle.</summary>
    internal bool TryRetainCapture(CaptureTarget next) => TryRetainCapture(next, out _);

    private bool TryRetainCapture(CaptureTarget next, out bool filterRetry)
    {
        filterRetry = false;
        CaptureTarget current;
        CaptureConnection[] removed;
        IReadOnlySet<CaptureEndpoint> oldCoverage;
        HashSet<CaptureEndpoint> added;
        long generation, revision;
        lock (_gate)
        {
            revision = ++_ownershipRevision;
            if (_target is not { Connections: not null } existing || !string.Equals(existing.Device, next.Device, StringComparison.OrdinalIgnoreCase) ||
                next.Connections is not { Count: > 0 }) return false;
            current = existing;
            removed = current.Connections!.Except(next.Connections).ToArray();
            added = next.RemoteEndpoints.Except(_installedCoverage).ToHashSet();
            if (added.Count == 0)
            {
                _target = next;
                RemoveCapturedConnections(removed);
                return true;
            }
            if (_device is null) return false;
            oldCoverage = _installedCoverage;
            generation = _generation;
        }
        ILiveDevice? device = null;
        var claimed = false;
        var handleLimit = false;
        try
        {
            // Never run native open/start/close under the packet gate. A callback may need it
            // while StartCapture/StopCapture waits for the capture thread.
            lock (_gate)
                if (_additionalDevices.Count >= MaxCaptureHandles - 1)
                {
                    handleLimit = true;
                    throw new InvalidOperationException("Capture handle limit reached.");
                }
            device = CaptureDeviceFactory(next.Device);
            lock (_gate)
                if (IsCaptureDeviceLocked(device)) throw new InvalidOperationException("Capture factory reused a live handle.");
            device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = 250 });
            device.Filter = "tcp and (" + string.Join(" or ", added.OrderBy(endpoint => endpoint.Address, StringComparer.Ordinal)
                .ThenBy(endpoint => endpoint.Port).Select(endpoint => $"(host {endpoint.Address} and port {endpoint.Port})")) + ")";
            lock (_gate)
            {
                if (!Running || generation != _generation || revision != _ownershipRevision || !ReferenceEquals(current, _target))
                {
                    filterRetry = true;
                    return false;
                }
                device.OnPacketArrival += OnPacketArrival;
                device.OnCaptureStopped += OnCaptureStopped;
                _additionalDevices.Add(device, added);
                claimed = true;
                _installedCoverage = oldCoverage.Union(added).ToHashSet();
                _target = next;
                RemoveCapturedConnections(removed);
                if (!Running || generation != _generation || !IsCaptureDeviceLocked(device)) return false;
            }
            device.StartCapture();
            lock (_gate)
            {
                if (!Running || generation != _generation || !IsCaptureDeviceLocked(device)) return false;
                RecordProgressEvent(new(DateTimeOffset.UtcNow, NetworkProgressEventKind.FilterExpanded));
                return true;
            }
        }
        catch (Exception error) when (error is PcapException or DllNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (_gate)
            {
                if (!Running || generation != _generation) return false;
                if (claimed && device is not null)
                {
                    // A capture-stopped callback may already own the failed handle's cleanup.
                    if (!_additionalDevices.Remove(device)) return false;
                    claimed = false;
                    foreach (var flow in _incomingPackets.Where(packet => ReferenceEquals(packet.Device, device)).Select(packet => packet.Flow).Distinct().ToArray())
                    {
                        DropIncomingFlowLocked(flow, invalidate: true);
                        if (!Running || generation != _generation) return false;
                    }
                    // Another existing handle may have failed during StartCapture. Never restore
                    // coverage from the pre-start snapshot and resurrect that failed sender.
                    var remaining = _installedCoverage.Except(added).ToHashSet();
                    _installedCoverage = remaining.SetEquals(oldCoverage) ? oldCoverage : remaining;
                    if (_target is { Connections: not null } active)
                    {
                        var retained = active.Connections.Where(connection => _installedCoverage.Contains(connection.RemoteEndpoint)).ToHashSet();
                        _target = active with { Connections = retained,
                            GamePorts = retained.Select(connection => connection.LocalPort).ToHashSet(),
                            ServerPorts = retained.Select(connection => connection.RemotePort).ToHashSet() };
                        RemoveCapturedConnections(active.Connections.Except(retained));
                        if (!Running || generation != _generation) return false;
                    }
                }
                // Removed native tuples lose authority even when adding a handle fails.
                if (ReferenceEquals(current, _target) && removed.Length > 0)
                {
                    var surviving = current.Connections!.Except(removed).ToHashSet();
                    _target = current with { Connections = surviving,
                        GamePorts = surviving.Select(connection => connection.LocalPort).ToHashSet(),
                        ServerPorts = surviving.Select(connection => connection.RemotePort).ToHashSet() };
                    RemoveCapturedConnections(removed);
                    if (!Running || generation != _generation) return false;
                }
                filterRetry = true;
                RecordProgressEvent(new(DateTimeOffset.UtcNow, NetworkProgressEventKind.FilterFailed,
                    ResetReason: handleLimit ? "capture-handle-limit" : null));
                SetStatus("Mitschnittfilter konnte nicht erweitert werden. Bestehende Spielverbindungen werden weiter gelesen; ein neuer Versuch folgt.", error: true);
                return false;
            }
        }
        finally
        {
            if (device is not null && !claimed)
            {
                bool active;
                lock (_gate) active = IsCaptureDeviceLocked(device);
                if (!active) CloseDetachedDevice(device);
            }
        }
    }

    private bool IsCaptureDeviceLocked(object device) => ReferenceEquals(device, _device) ||
        device is ILiveDevice live && _additionalDevices.ContainsKey(live);

    private void RemoveCapturedConnections(IEnumerable<CaptureConnection> removed)
    {
        var generation = _generation;
        foreach (var connection in removed)
        {
            if (generation != _generation) break;
            _streams.Remove(connection);
            ProcessProgressGap(connection.StreamKey, "ownership-removed");
        }
    }

    internal void CheckConnectionCore()
    {
        CaptureTarget? before;
        long generation, revision;
        lock (_gate)
        {
            if (!Running) return;
            before = _target; generation = _generation; revision = ++_ownershipRevision;
        }
        try
        {
            // Both periodic and first-packet native reads run outside both locks. A newer proof
            // adopted while this read was in flight must not be revoked by its stale result.
            var target = PeriodicOwnershipReader();
            UpdateCaptureTargetIfCurrent(target, before, generation, revision);
        }
        catch (Exception exception) when (exception is PcapException or DllNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (_connectionGate)
            {
                if (!CloseDeviceIfCurrent(before, generation, revision)) return;
                SetStatus("Mitschnitt nicht möglich: " + exception.Message, error: true);
            }
        }
    }

    /// <summary>Apply one TCP ownership snapshot without opening a device in synthetic retention tests.</summary>
    internal void UpdateCaptureTarget(CaptureTarget? target)
    {
        if (!Monitor.IsEntered(_gate)) { UpdateCaptureTargetIfCurrent(target, null, null); return; }
        _ownershipRevision++;
        if (!Running) return;
        if (_target is { } current && target is not null && CanRetainCapture(current, target, _installedCoverage))
        {
            TryRetainCapture(target);
            return;
        }
        if (_target is { Connections: not null } retained && target is { Connections.Count: > 0 } &&
            string.Equals(retained.Device, target.Device, StringComparison.OrdinalIgnoreCase))
        {
            var epoch = _generation;
            var observation = _ownershipRevision;
            QueueDeviceLifecycle(() => UpdateCaptureTargetIfCurrent(target, retained, epoch, observation));
            return;
        }
        CloseDevice();
        var generation = _generation;
        QueueDeviceLifecycle(() =>
        {
            lock (_gate) if (!Running || generation != _generation || _target is not null) return;
            if (target is not null) OpenDevice(target, generation);
        });
    }

    private void UpdateCaptureTargetIfCurrent(CaptureTarget? target, CaptureTarget? expected, long? expectedGeneration, long? expectedRevision = null)
    {
        lock (_connectionGate)
        {
            CaptureTarget? current;
            long generation;
            lock (_gate)
            {
                if (!Running || expectedGeneration is { } epoch &&
                    (epoch != _generation || !ReferenceEquals(expected, _target)) ||
                    expectedRevision is { } observation && observation != _ownershipRevision) return;
                // Even a same/null snapshot is a newer native observation than an in-flight probe.
                _ownershipRevision++;
                current = _target; generation = _generation;
                if (target is not null)
                {
                    if (current is not null && SameConnection(current, target))
                    {
                        if (StatusIsError && _device is not null) SetStatus($"Liest den Spielverkehr {target.Description}.");
                        return;
                    }
                }
            }
            if (target is not null)
            {
                if (TryRetainCapture(target, out var filterRetry))
                {
                    lock (_gate)
                        if (Running && generation == _generation) SetStatus($"Liest den Spielverkehr {target.Description}.");
                    return;
                }
                if (filterRetry) return;
            }
            // Detach atomically, but never hold _gate while StopCapture can wait for its callback.
            if (current is not null && !CloseDeviceIfCurrent(current, generation)) return;
            if (target is null)
            {
                SetStatus("Keine Verbindung von AION2 gefunden – läuft das Spiel?");
                return;
            }
            if (current is null && !CloseDeviceIfCurrent(null, generation)) return;
            OpenDevice(target, generation + 1);
        }
    }

    private void OpenDevice(CaptureTarget target, long expectedGeneration)
    {
        lock (_gate)
            if (Volatile.Read(ref _disposed) != 0 || !Running || expectedGeneration != _generation || _device is not null || _target is not null) return;
        var device = CaptureDeviceFactory(target.Device);
        lock (_gate)
        {
            // Enumeration runs outside the gate. Stop or a reentrant reset may have won since
            // the first check; an unopened device must not be claimed by that obsolete request.
            if (Volatile.Read(ref _disposed) != 0 || !Running || expectedGeneration != _generation || _device is not null || _target is not null) return;
            device.OnPacketArrival += OnPacketArrival;
            device.OnCaptureStopped += OnCaptureStopped;
            _device = device;
            _target = target;
            _installedCoverage = target.RemoteEndpoints.ToHashSet();
            _streams.Clear();
        }
        // Native operations stay outside the gate. A callback can stop the source while an
        // operation is in flight; the detached handle is then closed by its queued lifecycle.
        try
        {
            device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = 250 });
            device.Filter = target.Filter;
            lock (_gate)
                if (Volatile.Read(ref _disposed) != 0 || !Running || expectedGeneration != _generation || !ReferenceEquals(device, _device)) return;
            device.StartCapture();
            lock (_gate)
                if (Running && expectedGeneration == _generation && ReferenceEquals(device, _device))
                    SetStatus($"Liest den Spielverkehr {target.Description}.");
        }
        catch (Exception error)
        {
            if (CloseDeviceIfCurrent(target, expectedGeneration) && Running)
                SetStatus("Mitschnitt nicht möglich: " + error.Message, error: true);
            throw;
        }
    }

    private void CloseDevice() => CloseDeviceIfCurrent(null, null);

    private bool CloseDeviceIfCurrent(CaptureTarget? expected, long? expectedGeneration, long? expectedRevision = null)
    {
        var reentrant = Monitor.IsEntered(_gate);
        ILiveDevice[] devices;
        lock (_gate)
        {
            if (expectedGeneration is { } epoch && (epoch != _generation || !ReferenceEquals(expected, _target)) ||
                expectedRevision is { } observation && observation != _ownershipRevision) return false;
            devices = (_device is { } primary ? _additionalDevices.Keys.Append(primary) : _additionalDevices.Keys).ToArray();
            _additionalDevices.Clear();
            _device = null;
            _target = null;
            _installedCoverage = new HashSet<CaptureEndpoint>();
            _streams.Clear();
            ClearIncomingPacketsLocked(invalidate: false);
            _generation++;
            _ownershipRevision++;
            ResetProgressContext(Running ? "capture-closed" : "capture-stopped");
        }
        BossSessionReset?.Invoke();
        if (devices.Length == 0) return true;
        void CloseAll() { foreach (var device in devices) CloseDetachedDevice(device); }
        if (reentrant) QueueDeviceLifecycle(CloseAll);
        else CloseAll();
        return true;
    }

    private void QueueDeviceLifecycle(Action action)
    {
        lock (_gate)
            _deviceLifecycleTask = _deviceLifecycleTask.ContinueWith(_ =>
            {
                lock (_connectionGate) action();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void CloseDetachedDevice(ILiveDevice device)
    {
        device.OnPacketArrival -= OnPacketArrival;
        device.OnCaptureStopped -= OnCaptureStopped;
        try
        {
            device.StopCapture();
        }
        catch (PcapException)
        {
            // already closed
        }
        // Closing must still be attempted when stopping a partially opened/failed handle fails.
        try { device.Close(); }
        catch (PcapException) { }
    }

    private void OnPacketArrival(object sender, PacketCapture capture)
    {
        ProcessDevicePacket(sender, capture.GetPacket());
    }

    private void OnCaptureStopped(object sender, CaptureStoppedEventStatus status)
    {
        if (status != CaptureStoppedEventStatus.ErrorWhileCapturing) return;
        lock (_gate)
        {
            if (!Running || sender is not ILiveDevice device || !IsCaptureDeviceLocked(device)) return;
            RecordProgressEvent(new(DateTimeOffset.UtcNow, NetworkProgressEventKind.FilterFailed,
                ResetReason: "capture-handle-error"));
            if (ReferenceEquals(device, _device))
            {
                // This callback is on the native capture thread. Detach now, join only afterwards.
                CloseDevice();
            }
            else if (_additionalDevices.Remove(device, out var coverage))
            {
                var generation = _generation;
                // Cleanup ownership is transferred before any invalidation callback can Stop.
                QueueDeviceLifecycle(() => CloseDetachedDevice(device));
                _ownershipRevision++;
                _installedCoverage = _installedCoverage.Except(coverage).ToHashSet();
                foreach (var flow in _incomingPackets.Where(packet => ReferenceEquals(packet.Device, device))
                    .Select(packet => packet.Flow).Distinct().ToArray())
                {
                    DropIncomingFlowLocked(flow, invalidate: true);
                    if (!Running || generation != _generation) return;
                }
                if (_target is { Connections: not null } target)
                {
                    var surviving = target.Connections.Where(connection => !coverage.Contains(connection.RemoteEndpoint)).ToHashSet();
                    _target = target with { Connections = surviving,
                        GamePorts = surviving.Select(connection => connection.LocalPort).ToHashSet(),
                        ServerPorts = surviving.Select(connection => connection.RemotePort).ToHashSet() };
                    foreach (var connection in target.Connections.Except(surviving))
                    {
                        _streams.Remove(connection);
                        ProcessProgressGap(connection.StreamKey, "capture-handle-error");
                        if (!Running || generation != _generation) return;
                    }
                }
            }
            if (Running)
                SetStatus("Mitschnittfilter konnte nicht erweitert werden. Bestehende Spielverbindungen werden weiter gelesen; ein neuer Versuch folgt.", error: true);
        }
    }

    /// <summary>
    /// Preserve an eligible first packet until a fresh native snapshot proves its exact game-owned tuple.
    /// Native reads and waits never hold either capture lock; this path never changes the device/filter.
    /// </summary>
    internal void ProcessDevicePacket(object sender, RawCapture raw)
    {
        lock (_gate)
        {
            if (!Running || !IsCaptureDeviceLocked(sender) || _target is not { Connections: not null } target) return;
            Packets++;
            DropExpiredIncomingPacketsLocked(IncomingOwnershipUtcNow());
            if (!Running || !IsCaptureDeviceLocked(sender) || _target is not { Connections: not null } current) return;
            target = current;
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            if (packet.Extract<TcpPacket>() is not { } tcp || packet.Extract<IPPacket>() is not { } ip) return;
            var flow = new IncomingFlow(ip.SourceAddress.ToString(), tcp.SourcePort, ip.DestinationAddress.ToString(), tcp.DestinationPort);
            var owned = target.Connections.Any(flow.Matches);
            if (owned && !_incomingBarriers.Contains(flow))
            {
                DispatchCapturedPacket(raw, countPacket: false);
                return;
            }
            if (!_incomingBarriers.Contains(flow) &&
                (tcp.PayloadData is not { Length: > 0 } || !_installedCoverage.Contains(new(flow.RemoteAddress, flow.RemotePort)))) return;
            if (raw.Data.Length > MaxIncomingBytes - _incomingBytes || _incomingPackets.Count >= MaxIncomingPackets ||
                !_incomingBarriers.Contains(flow) && _incomingBarriers.Count >= MaxIncomingFlows)
            {
                _incomingDroppedPackets++;
                ClearIncomingPacketsLocked(invalidate: true);
                return;
            }
            _incomingBarriers.Add(flow);
            var retained = new RawCapture(raw.LinkLayerType, raw.Timeval, raw.Data.ToArray(), null);
            _incomingPackets.Add(new(flow, retained, sender, _generation, IncomingOwnershipUtcNow()));
            _incomingBytes += retained.Data.Length;
            StartIncomingProbeLocked();
        }
    }

    private void StartIncomingProbeLocked()
    {
        if (_incomingProbeRunning || _incomingPackets.Count == 0 || !Running) return;
        _incomingProbeRunning = true;
        _incomingProbeTask = Task.Run(ProbeIncomingOwnership);
    }

    private async Task ProbeIncomingOwnership()
    {
        try
        {
            while (true)
            {
                CaptureTarget target;
                object device;
                long generation, revision;
                TimeSpan delay;
                Func<CaptureTarget?> reader;
                Func<TimeSpan, Task> wait;
                lock (_gate)
                {
                    DropExpiredIncomingPacketsLocked(IncomingOwnershipUtcNow());
                    DrainOwnedIncomingPacketsLocked();
                    if (!Running || _device is null || _target is null || _incomingPackets.Count == 0) return;
                    target = _target; device = _device; generation = _generation;
                    var now = IncomingOwnershipUtcNow();
                    if (_lastIncomingProbeAt is { } previous && now < previous) _lastIncomingProbeAt = null;
                    delay = _lastIncomingProbeAt is { } last ? IncomingProbeInterval - (now - last) : TimeSpan.Zero;
                    var remaining = _incomingPackets.Min(packet => packet.At) + IncomingPacketLifetime - now;
                    if (delay > remaining) delay = remaining;
                    reader = IncomingOwnershipReader;
                    wait = IncomingOwnershipWait;
                    revision = _ownershipRevision;
                    if (delay <= TimeSpan.Zero) { _lastIncomingProbeAt = now; _incomingProbes++; revision = ++_ownershipRevision; }
                }
                if (delay > TimeSpan.Zero)
                {
                    await wait(delay).ConfigureAwait(false);
                    continue;
                }
                CaptureTarget? native = null;
                try { native = reader(); }
                catch (Exception)
                {
                    // A failed/empty native lookup cannot revoke the independently established old ownership.
                }
                lock (_gate)
                {
                    DropExpiredIncomingPacketsLocked(IncomingOwnershipUtcNow());
                    if (!Running || generation != _generation || !ReferenceEquals(device, _device)) continue;
                    if (revision == _ownershipRevision && ReferenceEquals(target, _target) && native is { Connections: not null } &&
                        string.Equals(native.Device, target.Device, StringComparison.OrdinalIgnoreCase))
                    {
                        var covered = native.Connections.Where(connection => _installedCoverage.Contains(connection.RemoteEndpoint)).ToHashSet();
                        // A consistent native snapshot is usable only after it proves an actual queued incoming tuple.
                        if (_incomingPackets.Any(pending => pending.Generation == generation && IsCaptureDeviceLocked(pending.Device)
                            && covered.Any(pending.Flow.Matches)))
                        {
                            var removed = target.Connections!.Except(covered).ToArray();
                            _target = target with { Connections = covered,
                                GamePorts = covered.Select(connection => connection.LocalPort).ToHashSet(),
                                ServerPorts = covered.Select(connection => connection.RemotePort).ToHashSet() };
                            RemoveCapturedConnections(removed);
                        }
                    }
                    // A periodic native refresh may already own the flow. Never adopt the now-stale probe snapshot.
                    DrainOwnedIncomingPacketsLocked();
                }
            }
        }
        catch
        {
            // A failing injected wait or unexpected callback must not cause a finally/restart hot loop.
            lock (_gate) ClearIncomingPacketsLocked(invalidate: true);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _incomingProbeRunning = false;
                StartIncomingProbeLocked();
            }
        }
    }

    private void DrainOwnedIncomingPacketsLocked()
    {
        for (var index = 0; index < _incomingPackets.Count;)
        {
            var pending = _incomingPackets[index];
            if (!Running || pending.Generation != _generation || !IsCaptureDeviceLocked(pending.Device))
            {
                DropIncomingFlowLocked(pending.Flow, invalidate: false);
                index = 0;
                continue;
            }
            if (_target?.Connections?.Any(pending.Flow.Matches) != true) { index++; continue; }
            _incomingPackets.RemoveAt(index);
            _incomingBytes -= pending.Raw.Data.Length;
            // Keep its barrier during dispatch: callbacks may supply the next bytes of this same flow.
            DispatchCapturedPacket(pending.Raw, countPacket: false);
            if (!_incomingPackets.Any(item => item.Flow == pending.Flow)) _incomingBarriers.Remove(pending.Flow);
            index = 0;
        }
    }

    private void DropExpiredIncomingPacketsLocked(DateTimeOffset now)
    {
        foreach (var flow in _incomingPackets.Where(packet => now < packet.At || now - packet.At >= IncomingPacketLifetime)
            .Select(packet => packet.Flow).Distinct().ToArray()) DropIncomingFlowLocked(flow, invalidate: true);
    }

    private void DropIncomingFlowLocked(IncomingFlow flow, bool invalidate)
    {
        var dropped = _incomingPackets.Where(packet => packet.Flow == flow).ToArray();
        _incomingPackets.RemoveAll(packet => packet.Flow == flow);
        _incomingBytes -= dropped.Sum(packet => packet.Raw.Data.Length);
        _incomingDroppedPackets += dropped.Length;
        _incomingBarriers.Remove(flow);
        if (!invalidate) return;
        foreach (var connection in _target?.Connections?.Where(flow.Matches).ToArray() ?? [])
        {
            _streams.Remove(connection);
            ProcessProgressGap(connection.StreamKey, "incoming-dropped");
        }
    }

    private void ClearIncomingPacketsLocked(bool invalidate)
    {
        foreach (var flow in _incomingBarriers.ToArray()) DropIncomingFlowLocked(flow, invalidate);
    }

    /// <summary>Strict live dispatch, also usable with synthetic packets without opening a capture device.</summary>
    internal void ProcessCapturedPacket(RawCapture raw) => DispatchCapturedPacket(raw, countPacket: true);

    private void DispatchCapturedPacket(RawCapture raw, bool countPacket)
    {
        // Keep Feed and dispatch under the same gate as target refresh. An already decoded identity
        // from a removed connection must not be rebound after its scoped invalidation.
        lock (_gate)
        {
            if (!Running || _target is not { } target) return;
            if (countPacket) Packets++;
            var messages = FeedOwn(_streams, raw, target);
            Messages += messages.Count(message => message.Data.Length > 0);
            Resyncs = _streams.Values.Sum(stream => stream.Messages.Resyncs);
            var generation = _generation;
            foreach (var received in messages)
            {
                if (!Running || generation != _generation) break;
                // Event handlers may refresh ownership reentrantly on this thread as well.
                if (received.Stream != "*" && _target?.Connections?.Any(connection => connection.StreamKey == received.Stream) != true) break;
                if (received.Transport is not null && !_streams.Any(pair => pair.Key.StreamKey == received.Stream &&
                    ReferenceEquals(pair.Value.Messages, received.Transport))) break;
                if (received.Gap)
                {
                    if (received.EndStream)
                        foreach (var connection in _streams.Keys.Where(connection => connection.StreamKey == received.Stream).ToArray())
                            _streams.Remove(connection);
                    ProcessProgressGap(received.Stream, received.GapReason, new DateTimeOffset(raw.Timeval.Date).ToUniversalTime());
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
                return FeedPacket(streams, tcp, connection, connection.StreamKey, deferFinish: true);
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

    private sealed class TcpSynStart(uint sequence)
    {
        public uint Sequence { get; } = sequence;
        public bool FirstPayloadPending { get; set; } = true;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameMessageStream, TcpSynStart> TcpSynStarts = new();

    private static List<ReceivedMessage> FeedPacket<TKey>(Dictionary<TKey, (TcpReassembler Tcp, GameMessageStream Messages)> streams,
        TcpPacket tcp, TKey key, string streamKey, bool deferFinish = false) where TKey : notnull
    {
        var result = new List<ReceivedMessage>();
        // A reset has no deliverable tail. Report closure even when the control packet has no payload.
        if (tcp.Reset)
        {
            streams.Remove(key);
            result.Add(new ReceivedMessage(streamKey, [], Gap: true, GapReason: "tcp-reset"));
            return result;
        }
        var synBoundary = false;
        if (tcp.Synchronize && (!streams.TryGetValue(key, out var synStream)
            || !TcpSynStarts.TryGetValue(synStream.Messages, out var knownSyn)
            || !knownSyn.FirstPayloadPending || knownSyn.Sequence != tcp.SequenceNumber))
        {
            // A new handshake is a new transport even when its tuple is reused and the old
            // FIN/RST was not observed. Only its exact repeat before the first payload is harmless;
            // after payload even the same ISN cannot distinguish a retransmission from tuple reuse.
            streams.Remove(key);
            if (streams.Count >= 64)
            {
                streams.Clear();
                result.Add(new ReceivedMessage("*", [], Gap: true, GapReason: "stream-limit"));
            }
            var fresh = (Tcp: new TcpReassembler(), Messages: new GameMessageStream(requireVerifiedSync: true));
            streams[key] = fresh;
            TcpSynStarts.Add(fresh.Messages, new(tcp.SequenceNumber));
            result.Add(new ReceivedMessage(streamKey, [], Gap: true, GapReason: "tcp-start"));
            synBoundary = true;
        }
        var payload = tcp.PayloadData;
        if (payload is null || payload.Length == 0)
        {
            if (tcp.Finished)
            {
                streams.Remove(key);
                result.Add(new ReceivedMessage(streamKey, [], Gap: true, GapReason: "tcp-finished"));
            }
            return result;
        }
        var startedHere = false;
        if (!streams.TryGetValue(key, out var stream))
        {
            startedHere = true;
            // A capture may span many reconnects; old TCP contexts must not accumulate forever.
            if (streams.Count >= 64)
            {
                streams.Clear();
                result.Add(new ReceivedMessage("*", [], Gap: true, GapReason: "stream-limit"));
            }
            streams[key] = stream = (new TcpReassembler(), new GameMessageStream(requireVerifiedSync: true));
        }
        if (TcpSynStarts.TryGetValue(stream.Messages, out var initialSyn) && initialSyn.FirstPayloadPending)
        {
            startedHere = true;
            initialSyn.FirstPayloadPending = false;
        }
        // TCP SYN consumes one sequence position before any optional payload.
        var payloadSequence = unchecked(tcp.SequenceNumber + (tcp.Synchronize ? 1u : 0u));
        foreach (var chunk in stream.Tcp.Add(payloadSequence, payload))
        {
            if (chunk.Gap)
            {
                stream.Messages.Break();
                if (!synBoundary)
                    result.Add(new ReceivedMessage(streamKey, [], Gap: true,
                        GapReason: startedHere ? "tcp-start" : "tcp-pending-overflow"));
                startedHere = false;
            }
            if (chunk.Data.Length > 0)
            {
                var before = (stream.Messages.Resyncs, stream.Messages.ContinuityBreaks);
                var decoded = stream.Messages.Append(chunk.Data);
                if (stream.Messages.Resyncs > before.Resyncs || stream.Messages.ContinuityBreaks > before.ContinuityBreaks)
                    result.Add(new ReceivedMessage(streamKey, [], Gap: true, GapReason: "framing-gap"));
                result.AddRange(decoded.Select(message => new ReceivedMessage(streamKey, message, Transport: stream.Messages)));
            }
        }
        // Live dispatch retains this transport until its complete FIN payload has been delivered.
        if (tcp.Finished)
        {
            if (!deferFinish) streams.Remove(key);
            result.Add(new ReceivedMessage(streamKey, [], Gap: true, EndStream: deferFinish, GapReason: "tcp-finished"));
        }
        return result;
    }

    /// <summary>Invalidates the bound stream immediately and refreshes the passive status card.</summary>
    internal void ProcessProgressGap(string stream, string reason = "stream-gap", DateTimeOffset? capturedAtUtc = null)
    {
        lock (_gate)
        {
            if (stream == "*" || _abyssPointContinuity?.Stream == stream) RevokeAbyssPointContinuity();
            var characterAffected = stream == "*" || _identityStream == stream || _entryCandidate?.Stream == stream;
            var characterGeneration = _progressGeneration;
            var ownCharacterChanged = characterAffected && _ownCharacter is not null;
            var petAffected = stream == "*" || _petStreams.Remove(stream);
            var merchantAffected = stream == "*" || _merchantStreams.Remove(stream);
            // Revoke the entity proof before any service event can reenter with a broadcast.
            if (characterAffected)
            {
                _ownCharacter = null;
                _identity = null;
                ResetGatheringContext();
                ResetAbyssCombatContext();
                ResetKinaContext();
                ResetAbyssContext();
                ResetDailyKinaContext();
                ResetFarmDropContext();
                _identityStream = null;
                Character = null;
                _entryCandidate = null;
                // Revoke world authority before any pet/merchant callback can reenter.
                WorldContext.Invalidate(WorldContextStatus.Stale, _progressGeneration + 1, DiagnosticResetReason(reason), capturedAtUtc);
            }
            if (petAffected) ResetPetContext(forgetStreams: stream == "*");
            // A nested new Own has already reset and rebound the character sources.
            if (characterAffected && characterGeneration == _progressGeneration)
                ResetProgressContext(DiagnosticResetReason(reason));
            else if (!characterAffected && merchantAffected) ResetMerchantContext();
            if (ownCharacterChanged) SafeEvent.Raise(OwnCharacterChanged, "Eigener Charakter");
            if (characterAffected || petAffected) SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
        }
        // Boss schedules can belong to a different stream; notify only after revoking stale Own authority.
        BossStreamGap?.Invoke(stream);
    }

    /// <summary>Pure message dispatch used by the live source and isolated integration tests.</summary>
    internal void ProcessProgressMessage(string stream, byte[] message, DateTimeOffset at)
    {
        lock (_gate)
        {
            ExpireAbyssPointContinuity(stream, at);
            ExpireEntryCandidate(at);
            if (message.Length < 2) return;
            var opcode = (ushort)(message[0] | message[1] << 8);
            if (_identityStream == stream && (_gatheringCompletions.Count + _gatheringWitnesses.Count + _ambiguousGatherings.Count > 0))
            {
                var ownBeforeExpiry = _ownCharacter;
                ExpireGatheringPairs(at);
                // A status subscriber can synchronously bind another character. This old message
                // may still carry global pet data, but cannot gain authority over the new profile.
                if (!ReferenceEquals(ownBeforeExpiry, _ownCharacter)
                    && opcode is not (0x9000 or 0x900D or 0x900E or 0x9014)) return;
            }
            if (opcode is 0xFFA5 or 0xFFA6)
            {
                ProcessDailyKinaMessage(stream, opcode, message, at);
                return;
            }
            if (opcode == 0x9011)
            {
                ProcessFarmCrystalMessage(stream, message, at);
                return;
            }
            if (opcode is 0x5611 or 0x561B)
            {
                ProcessKinaMessage(stream, opcode, message, at);
                return;
            }
            if (opcode == 0xE273)
            {
                ProcessAbyssRankMessage(stream, message, at);
                return;
            }
            if (opcode == 0x8D04)
            {
                ProcessAbyssCombatMessage(stream, message, at);
                return;
            }
            if (opcode == 0x3645)
            {
                ProcessAbyssPlayerAppearance(stream, message, at);
                return;
            }
            if (opcode is 0x3641 or 0x3642 or 0x3647)
            {
                ProcessAbyssActorRevocation(stream, message, at);
                return;
            }
            if (opcode == 0xFFA9)
            {
                ProcessAbyssFarmMessage(stream, message, at);
                return;
            }
            if (opcode is 0x5691 or 0x5693)
            {
                _merchantStreams.Add(stream);
                // Native socket ownership is verified by the shared capture path. The pet collection
                // no longer needs the merchant connection to supply a character or server binding.
                SafeEvent.Raise(MerchantMessageReceived, new MerchantNetworkMessage(stream, _merchantGeneration,
                    _identityStream == stream ? _identity?.ServerId : null, at, message.ToArray()), "Händler-Netzwerkdaten");
                return;
            }
            if (opcode is 0x9000 or 0x9009 or 0x900D or 0x900E or 0x9014)
            {
                ProcessPetMessage(stream, opcode, message, at);
                return;
            }
            if (opcode is 0x3634 or 0x3635 or 0x8D33 or 0x8D34)
            {
                ProcessGatheringMessage(stream, opcode, message, at);
                return;
            }
            if (opcode == 0x3620)
            {
                // An unknown entry shape also suspends old authority on this same stream. It must
                // never let the next character's snapshots mutate the previously bound profile.
                if (_identity is not null && _identityStream != stream ||
                    _entryCandidate is { } other && other.Stream != stream ||
                    _abyssPointContinuity is { } otherContinuity && otherContinuity.Stream != stream) return;
                var captureGeneration = _generation;
                var candidateGeneration = _progressGeneration + 1;
                var tentative = OwnIdentityMessage.TryParseEntryCandidate(message);
                var continuity = BeginAbyssPointCandidate(stream, tentative, at);
                ResetProgressContextCore("world-entry-candidate", at, continuity);
                CompleteAbyssPointTransitionReset(continuity);
                if (_generation != captureGeneration || _progressGeneration != candidateGeneration || _identity is not null) return;
                if (tentative is not null)
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
                    _entryCandidate is { } other && other.Stream != stream ||
                    _abyssPointContinuity is { } otherContinuity && otherContinuity.Stream != stream) return;
                var candidate = _entryCandidate;
                var captureGeneration = _generation;
                var preparedGeneration = _progressGeneration + 1;
                var kinaRevision = _kinaMessageRevision;
                var dailyKinaRevision = _dailyKinaMessageRevision;
                var continuity = PrepareAbyssPointContinuity(stream, at);
                ResetProgressContextCore("world-entry-preparation", at, continuity);
                CompleteAbyssPointTransitionReset(continuity);
                if (_generation != captureGeneration || _progressGeneration != preparedGeneration || _identity is not null) return;
                if (candidate is { Prepared: false })
                {
                    if (_kinaMessageRevision != kinaRevision)
                    {
                        candidate.KinaRejected = true;
                        candidate.KinaList = null;
                    }
                    if (_dailyKinaMessageRevision != dailyKinaRevision)
                    {
                        candidate.DailyKinaRejected = true;
                        candidate.DailyKina = null;
                    }
                    candidate.Prepared = true;
                    _entryCandidate = candidate;
                }
                BossSessionReset?.Invoke();
                SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
                return;
            }
            if (OwnIdentityMessage.TryParseWorld(message) is { } world)
            {
                ObserveIdentity(stream, world.Identity, at, world.MapDataId, world.CombatScore, world.GearScore, world.AbyssRankPoints);
                return;
            }
            if (opcode is 0x3633 or 0x3621)
            {
                if (_identityStream == stream || _entryCandidate?.Stream == stream || _abyssPointContinuity?.Stream == stream)
                {
                    RecordProgressRejection(opcode, at);
                    var rejectedGeneration = _progressGeneration + 1;
                    ResetProgressContext("unsupported-own-entry", at);
                    if (_progressGeneration != rejectedGeneration || _identity is not null) return;
                    ProgressStatus = "Netzwerkformat nicht unterstützt. Gespeicherter Fortschritt bleibt erhalten.";
                    SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
                }
                return;
            }
            if (opcode == 0x6100)
            {
                // No pre-Own buffering and no exploration/source counters: this only refines location.
                if (_identity is not null && _identityStream == stream)
                {
                    if (DungeonProgressMessage.TryParseState(message) is { } state)
                        WorldContext.ObserveDungeon(state, _progressGeneration, at);
                    else WorldContext.RejectDungeonRefinement(_progressGeneration, at);
                }
                return;
            }
            if (opcode is not (0x9232 or 0x9233 or 0x8D09 or 0x6101)) return;
            if (_identity is null)
            {
                BufferEntryBaseline(stream, opcode, message, at);
                return;
            }
            if (_identityStream != stream)
                return;
            var sourceIndex = ProgressSourceIndex(opcode);
            if (sourceIndex >= 0) _sourceRevisions[sourceIndex]++;
            var context = new NetworkProgressContext(_identity, _progressGeneration, ++_progressSequence, at);
            var accepted = false;
            var parsed = false;
            switch (opcode)
            {
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
                UpdateProgressStatus();
                SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
            }
        }
    }

    private void ResetPetContext(bool forgetStreams = true)
    {
        _petGeneration++;
        _petSequence = 0;
        if (forgetStreams) _petStreams.Clear();
        PetBaselineAt = null;
        _petActivityAt = null;
        _petFormatRejected = false;
        progress.MarkPetNetworkStale();
    }

    /// <summary>Entity-free own-pet messages belong to one collection, independently of character/realm.</summary>
    private void ProcessPetMessage(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (IgnoreAcquisition(stream, opcode, message)) return;
        _petStreams.Add(stream);
        if (_petDispatching)
        {
            if (_petDispatchQueue.Count >= MaxIncomingPackets || message.Length > MaxIncomingBytes - _petDispatchBytes)
            {
                _petDispatchQueue.Clear();
                _petDispatchBytes = 0;
                ResetPetContext();
                RecordProgressEvent(new(at, NetworkProgressEventKind.Reset, ResetReason: "other"));
                return;
            }
            _petDispatchQueue.Enqueue(new(stream, opcode, message.ToArray(), at, _generation, _petGeneration));
            _petDispatchBytes += message.Length;
            return;
        }
        _petDispatching = true;
        try
        {
            DispatchPetMessage(stream, opcode, message, at);
            while (_petDispatchQueue.TryDequeue(out var pending))
            {
                _petDispatchBytes -= pending.Message.Length;
                if (pending.CaptureGeneration == _generation && pending.PetGeneration == _petGeneration &&
                    (_target is null || _target.Connections?.Any(connection => connection.StreamKey == pending.Stream) == true))
                    DispatchPetMessage(pending.Stream, pending.Opcode, pending.Message, pending.At);
            }
        }
        finally
        {
            _petDispatching = false;
            _petDispatchQueue.Clear();
            _petDispatchBytes = 0;
        }
    }

    private void DispatchPetMessage(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (IgnoreAcquisition(stream, opcode, message)) return;
        var farmOwn = _identityStream == stream ? _ownCharacter : null;
        var revision = ++_sourceRevisions[0];
        var generation = _petGeneration;
        _petStreams.Add(stream); // Bind/apply notify synchronously; a reentrant gap must already see its scope.
        if (!progress.BindPetSession(new(generation, 0, at)) || generation != _petGeneration) return;
        var context = new PetProgressContext(generation, ++_petSequence, at);
        var parsed = false;
        var accepted = false;
        switch (opcode)
        {
            case 0x9000 when PetProgressMessage.TryParseSnapshot(message) is { } snapshot:
                parsed = true;
                accepted = progress.ApplyPetSnapshot(snapshot, context);
                if (accepted && generation == _petGeneration && revision == _sourceRevisions[0])
                {
                    PetBaselineAt = at;
                    _petFormatRejected = false;
                    RecordProgressEvent(new(at, NetworkProgressEventKind.Snapshot, opcode,
                        Count: snapshot.Levels.Keys.Concat(snapshot.AvailableSouls.Keys).Distinct().Count()));
                }
                break;
            case 0x9009 when PetProgressMessage.TryParseAcquired(message) is { } acquired:
                parsed = true;
                // CharRideAcquire is broadcast: the entity still needs its own independent proof.
                if (_identityStream == stream && _identity?.EntityKey == acquired.EntityKey)
                    accepted = progress.ApplyPetAcquisition(acquired, context);
                if (accepted) RecordProgressEvent(new(at, NetworkProgressEventKind.Acquired, opcode, acquired.PetNumber, acquired.Level));
                break;
            case 0x9014 when PetProgressMessage.TryParseUpgrade(message) is { } upgrade:
                parsed = true;
                accepted = progress.ApplyPetUpgrade(upgrade, context);
                if (accepted) RecordProgressEvent(new(at, NetworkProgressEventKind.Upgraded, opcode, upgrade.PetNumber, upgrade.Level));
                break;
            case 0x900D when LootMessage.TryParse(message) is { } gains:
                parsed = true;
                accepted = progress.ApplyPetGain(gains, context);
                if (accepted)
                {
                    _acceptedSoulMessages++;
                    _acceptedSoulPoints += gains.Sum(gain => (long)gain.Souls);
                    _lastSoulAt = at;
                    foreach (var gain in gains) RecordProgressEvent(new(at, NetworkProgressEventKind.Gained, opcode, gain.PetNumber, Souls: gain.Souls));
                    foreach (var gain in gains)
                        if (farmOwn is not null && ReferenceEquals(farmOwn, _ownCharacter))
                            ProcessFarmPetSoulGain(stream, gain.PetNumber, gain.Souls, at);
                    Count(gains, at);
                }
                break;
            case 0x900E when PetProgressMessage.TryParseSpent(message) is { } spent:
                parsed = true;
                accepted = progress.ApplyPetSpent(spent, context);
                if (accepted)
                    foreach (var spend in spent) RecordProgressEvent(new(at, NetworkProgressEventKind.Spent, opcode, spend.PetNumber, Souls: spend.Souls));
                break;
        }
        if (generation != _petGeneration) return;
        if (!parsed)
        {
            RecordProgressRejection(opcode, at);
            PetBaselineAt = null;
            _petFormatRejected = true;
            progress.MarkNetworkPetSourceStale();
        }
        else if (accepted)
        {
            if (revision == _sourceRevisions[0]) _petActivityAt = at;
        }
        UpdateProgressStatus();
        SafeEvent.Raise(Changed, "Pet-Netzwerkfortschritt");
    }

    private bool IgnoreAcquisition(string stream, ushort opcode, byte[] message) => opcode == 0x9009 &&
        (_identityStream != stream || _identity is null || PetProgressMessage.TryParseAcquired(message) is { } broadcast &&
            broadcast.EntityKey != _identity.EntityKey);

    private static int ProgressSourceIndex(ushort opcode) => opcode switch
    {
        0x9000 or 0x9009 or 0x900D or 0x900E or 0x9014 => 0,
        0x8D09 => 1,
        0x9232 or 0x9233 => 2,
        _ => -1,
    };

    private void RecordProgressRejection(int opcode, DateTimeOffset at)
    {
        _rejectedProgressMessages++;
        _lastRejectedOpcode = (ushort)opcode;
        _lastRejectedAt = at;
        RecordProgressEvent(new(at, NetworkProgressEventKind.Rejected, (ushort)opcode));
    }

    private void RecordProgressEvent(NetworkProgressDiagnosticEvent entry)
    {
        lock (_gate)
        {
            if (_progressEvents.Count >= 128) _progressEvents.Dequeue();
            _progressEvents.Enqueue(entry with { At = entry.At.ToUniversalTime() });
        }
    }

    private static string DiagnosticResetReason(string reason) => reason switch
    {
        "manual" or "capture-closed" or "capture-stopped" or "stream-gap" or "world-entry-candidate" or
        "tcp-start" or "tcp-pending-overflow" or "tcp-reset" or "tcp-finished" or "stream-limit" or
        "framing-gap" or "ownership-removed" or "incoming-dropped" or "capture-handle-error" or
        "world-entry-preparation" or "unsupported-own-entry" or "entry-candidate-expired" or
        "duplicate-entry-baseline" or "unsupported-entry-baseline" or "world-entry" or "identity-bind-rejected" or
        "pet-continuity-expired" or "pet-continuity-unread" => reason,
        _ => "other",
    };

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
                                ? "Die gemeinsame Pet-Sammlung und die verfügbaren Erkundungsdaten dieses Charakters wurden eingelesen."
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
            opcode is not (0x8D09 or 0x9232)) return;
        // One candidate globally and one packet per full source are stricter than the former
        // eight-stream/256-message/4 MiB caps; keep the former 2 MiB per-stream bound as well.
        if (candidate.Baselines.Count >= 256 || message.Length > 2 * 1024 * 1024 - candidate.Bytes ||
            message.Length > 4 * 1024 * 1024 - candidate.Bytes)
        {
            OverflowPending();
            return;
        }
        var decoded = opcode switch
        {
            0x8D09 => ExplorationProgressMessage.TryParseQuests(message) is not null,
            0x9232 => ExplorationProgressMessage.TryParseTeleports(message) is not null,
            _ => false,
        };
        if (!decoded)
        {
            RecordProgressRejection(opcode, at);
            // A schema failure is source-specific. It cannot invalidate the independent identity
            // correlation or discard another fully decoded source; this source never gets replayed.
            RemoveEntryBaseline(candidate, opcode);
            candidate.RejectedSources.Add(opcode); // only the three bounded full-source opcodes enter here
            ProgressStatus = "Netzwerkformat nicht unterstützt. Gespeicherter Fortschritt bleibt erhalten.";
            SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
            return;
        }
        if (candidate.Sources.Contains(opcode))
        {
            ResetProgressContext("duplicate-entry-baseline", at);
            SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
            return;
        }
        candidate.RejectedSources.Remove(opcode);
        candidate.Baselines.Add((message.ToArray(), at));
        candidate.Sources.Add(opcode);
        candidate.Bytes += message.Length;
        SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
    }

    private static void RemoveEntryBaseline(EntryCandidate candidate, ushort opcode)
    {
        if (!candidate.Sources.Remove(opcode)) return;
        for (var index = candidate.Baselines.Count - 1; index >= 0; index--)
        {
            var item = candidate.Baselines[index];
            if (item.Message.Length < 2 || item.Message[0] != (byte)(opcode & 0xFF) || item.Message[1] != (byte)(opcode >> 8)) continue;
            candidate.Bytes -= item.Message.Length;
            candidate.Baselines.RemoveAt(index);
        }
    }

    private void OverflowPending()
    {
        RevokeAbyssPointContinuity();
        _entryCandidate = null;
        _pendingProgressOverflow = true;
        ProgressStatus = "Charakter noch unbekannt. Bitte einmal die Charakterauswahl öffnen und den Charakter erneut betreten oder den Charakter wechseln.";
        SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
    }

    private bool OwnBindingMatches(string stream, OwnCharacterIdentity identity, NetworkProgressContext context, long captureGeneration) =>
        _generation == captureGeneration && _progressGeneration == context.Generation && _identity == identity && _identityStream == stream;

    internal void ObserveIdentity(string stream, OwnCharacterIdentity identity, DateTimeOffset at, int? mapDataId = null,
        long? combatScore = null, int? gearScore = null, long? abyssRankPoints = null)
    {
        lock (_gate)
        {
            ExpireAbyssPointContinuity(stream, at);
            ExpireEntryCandidate(at);
            var captureGeneration = _generation;
            var transaction = _entryCandidate is { Prepared: true } candidate && candidate.Stream == stream &&
                candidate.CharacterId == identity.CharacterId && candidate.EntityKey == identity.EntityKey ? candidate : null;
            var pending = transaction?.Baselines.ToArray() ?? [];
            var rejectedSources = transaction?.RejectedSources.ToArray() ?? [];
            var ownGeneration = _progressGeneration + 1;
            var kinaRevision = _kinaMessageRevision;
            var dailyKinaRevision = _dailyKinaMessageRevision;
            var continuity = BeginAbyssPointRebind(stream, identity, at);
            ResetProgressContextCore("world-entry", at, continuity);
            CompleteAbyssPointTransitionReset(continuity);
            if (_generation != captureGeneration || _progressGeneration != ownGeneration || _identity is not null) return;
            _identity = identity;
            _ownCharacter = new(identity, ownGeneration)
            {
                CombatScore = combatScore is >= 0 ? combatScore : null,
                GearScore = gearScore is >= 0 ? gearScore : null,
                ObservedAt = at,
            };
            _identityStream = stream;
            BindAbyssCombatOwnTransport(stream);
            if (transaction is not null && _kinaMessageRevision != kinaRevision)
            {
                transaction.KinaRejected = true;
                transaction.KinaList = null;
            }
            BindKinaBaseline(transaction);
            BindAbyssBaseline(transaction);
            BindAbyssPointContinuity(continuity, stream, at);
            BindAbyssRankBaseline(abyssRankPoints, at);
            BindFarmDropBaseline(transaction is { KinaRejected: false, FarmInventoryRejected: false } ? transaction.KinaList : null);
            if (transaction is not null && _dailyKinaMessageRevision != dailyKinaRevision)
            {
                transaction.DailyKinaRejected = true;
                transaction.DailyKina = null;
            }
            BindDailyKinaBaseline(transaction);
            Character = identity.Name;
            _questFormatRejected = rejectedSources.Contains((ushort)0x8D09);
            _teleportFormatRejected = rejectedSources.Contains((ushort)0x9232);
            var sourceRevisions = _sourceRevisions.ToArray();
            var context = new NetworkProgressContext(identity, ownGeneration, 0, at);
            exploration?.BindNetworkContext(context);
            if (!OwnBindingMatches(stream, identity, context, captureGeneration)) return;
            WorldContext.ObserveOwn(mapDataId, ownGeneration, at);
            if (!OwnBindingMatches(stream, identity, context, captureGeneration)) return;
            _identityBindings++;
            SafeEvent.Raise(OwnCharacterChanged, "Eigener Charakter");
            if (!OwnBindingMatches(stream, identity, context, captureGeneration)) return;
            UpdateProgressStatus();
            BossSessionReset?.Invoke();
            OwnCharacterEntered?.Invoke(identity);
            CharacterEntered?.Invoke(identity.Name);
            foreach (var item in pending)
            {
                if (!OwnBindingMatches(stream, identity, context, captureGeneration)) break;
                var sourceIndex = ProgressSourceIndex((ushort)(item.Message[0] | item.Message[1] << 8));
                if (sourceIndex >= 0 && _sourceRevisions[sourceIndex] != sourceRevisions[sourceIndex]) continue;
                ProcessProgressMessage(stream, item.Message, item.At);
            }
            SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
        }
    }

    internal void ResetProgressContext(string reason = "manual", DateTimeOffset? at = null) =>
        ResetProgressContextCore(reason, at, null);

    private void ResetProgressContextCore(string reason, DateTimeOffset? at, AbyssPointContinuity? continuity)
    {
        lock (_gate)
        {
            // Default resets are hard. Only the explicit validated soft entry paths retain
            // their same, still live RAM ticket; nested hard resets can never resurrect it.
            if (continuity is null || !ReferenceEquals(_abyssPointContinuity, continuity)) RevokeAbyssPointContinuity();
            else continuity.Resetting = true;
            if (reason == "manual")
            {
                ClearIncomingPacketsLocked(invalidate: false);
            }
            _contextResets++;
            _lastResetReason = reason;
            _lastResetAt = at ?? DateTimeOffset.UtcNow;
            RecordProgressEvent(new(_lastResetAt.Value, NetworkProgressEventKind.Reset, ResetReason: DiagnosticResetReason(reason)));
            _questFormatRejected = _teleportFormatRejected = _dungeonFormatRejected = _kibeliskFormatRejected = false;
            _dungeonActivityAt = _kibeliskActivityAt = null;
            _progressGeneration++;
            _progressSequence = 0;
            _identity = null;
            ResetGatheringContext();
            ResetAbyssCombatContext();
            ResetKinaContext();
            ResetAbyssContext();
            ResetDailyKinaContext();
            ResetFarmDropContext();
            var ownCharacterChanged = _ownCharacter is not null;
            _ownCharacter = null;
            _identityStream = null;
            Character = null;
            _entryCandidate = null;
            _pendingProgressOverflow = false;
            QuestBaselineAt = TeleportBaselineAt = null;
            ProgressStatus = "Warte auf die eigene Charaktererkennung.";
            var resetGeneration = _progressGeneration;
            var worldStatus = reason is "world-entry-candidate" or "world-entry-preparation" or "world-entry"
                ? WorldContextStatus.Transitioning : WorldContextStatus.Stale;
            WorldContext.Invalidate(worldStatus, resetGeneration, DiagnosticResetReason(reason), at);
            if (_progressGeneration != resetGeneration) return;
            if (reason is "manual" or "capture-closed" or "capture-stopped")
            {
                _merchantStreams.Clear();
                ResetPetContext();
                if (_progressGeneration != resetGeneration) return;
            }
            ResetMerchantContext();
            if (_progressGeneration != resetGeneration) return;
            exploration?.MarkNetworkStale();
            if (ownCharacterChanged && _progressGeneration == resetGeneration)
                SafeEvent.Raise(OwnCharacterChanged, "Eigener Charakter");
        }
    }

    private void ResetMerchantContext()
    {
        _merchantGeneration++;
        SafeEvent.Raise(MerchantSessionReset, "Händler-Sitzung");
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }
}
