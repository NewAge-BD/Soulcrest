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
    private readonly Dictionary<(string Source, int SourcePort, int DestinationPort), (TcpReassembler Tcp, GameMessageStream Messages)> _streams = [];
    private readonly List<NetworkSoul> _recent = [];
    private ILiveDevice? _device;
    private CaptureTarget? _target;
    private System.Threading.Timer? _check;
    private readonly Dictionary<string, List<(byte[] Message, DateTimeOffset At)>> _pendingProgress = [];
    private int _pendingProgressBytes;
    private bool _pendingProgressOverflow;
    private string? _identityStream;
    private OwnCharacterIdentity? _identity;
    private long _progressGeneration;
    private long _progressSequence;

    public DateTimeOffset? PetBaselineAt { get; private set; }
    public DateTimeOffset? QuestBaselineAt { get; private set; }
    public DateTimeOffset? TeleportBaselineAt { get; private set; }
    public bool ProgressIsLive => Running && _identity is not null && PetBaselineAt is not null;
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
        && current.GamePorts.SetEquals(next.GamePorts) && current.ServerPorts.SetEquals(next.ServerPorts);

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
            _streams.Clear();
            _generation++;
            ResetProgressContext();
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
        List<ReceivedMessage> messages;
        long generation;
        lock (_gate)
        {
            if (!Running || !ReferenceEquals(sender, _device) || _target is not { } target)
                return;
            Packets++;
            messages = Feed(_streams, raw, target.ServerPorts, target.GamePorts);
            Messages += messages.Count(m => m.Data.Length > 0);
            Resyncs = _streams.Values.Sum(s => s.Messages.Resyncs);
            generation = _generation;
        }
        foreach (var received in messages)
        {
            lock (_gate)
            {
                if (!Running || generation != _generation)
                    break;
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
        var payload = tcp.PayloadData;
        if (payload is null || payload.Length == 0)
            return result;
        var key = (ip.SourceAddress.ToString(), (int)tcp.SourcePort, (int)tcp.DestinationPort);
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
                result.Add(new ReceivedMessage($"{key.Item1}:{key.Item2}:{key.Item3}", [], Gap: true));
            }
            if (chunk.Data.Length > 0)
            {
                var before = (stream.Messages.Resyncs, stream.Messages.ContinuityBreaks);
                var decoded = stream.Messages.Append(chunk.Data);
                if (stream.Messages.Resyncs > before.Resyncs || stream.Messages.ContinuityBreaks > before.ContinuityBreaks)
                    result.Add(new ReceivedMessage($"{key.Item1}:{key.Item2}:{key.Item3}", [], Gap: true));
                result.AddRange(decoded.Select(message => new ReceivedMessage($"{key.Item1}:{key.Item2}:{key.Item3}", message)));
            }
        }
        return result;
    }

    /// <summary>Invalidates the bound stream immediately and refreshes the passive status card.</summary>
    internal void ProcessProgressGap(string stream)
    {
        lock (_gate)
        {
            if (_pendingProgress.Remove(stream, out var pending))
                _pendingProgressBytes -= pending.Sum(p => p.Message.Length);
            if (stream != "*" && _identityStream != stream) return;
            ResetProgressContext();
            SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
        }
    }

    /// <summary>Pure message dispatch used by the live source and isolated integration tests.</summary>
    internal void ProcessProgressMessage(string stream, byte[] message, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (OwnIdentityMessage.IsWorldEntryPreparation(message))
            {
                if (_identity is not null && _identityStream != stream) return;
                ResetProgressContext();
                BossSessionReset?.Invoke();
                SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
                return;
            }
            if (OwnIdentityMessage.TryParse(message) is { } identity)
            {
                ObserveIdentity(stream, identity, at);
                return;
            }
            if (message.Length < 2)
                return;
            var opcode = message[0] | message[1] << 8;
            if (opcode is 0x3633 or 0x3621)
            {
                // A known world-entry opcode with an unsupported schema must not leave the old
                // character trusted. Never bind a partial prefix or invalidate another stream.
                if (_identityStream == stream)
                {
                    ResetProgressContext();
                    ProgressStatus = "Netzwerkformat nicht unterstützt. Gespeicherter Fortschritt bleibt erhalten.";
                    SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
                }
                return;
            }
            if (opcode is not (0x9000 or 0x9009 or 0x900D or 0x900E or 0x9232 or 0x8D09))
                return;
            if (_identity is null)
            {
                if (_pendingProgressOverflow) return;
                if (!_pendingProgress.TryGetValue(stream, out var pending))
                {
                    if (_pendingProgress.Count >= 8) { OverflowPending(); return; }
                    _pendingProgress[stream] = pending = [];
                }
                // Bound the complete pre-identity prefix, not an arbitrary surviving tail.
                if (pending.Count >= 256 || pending.Sum(p => p.Message.Length) + message.Length > 2 * 1024 * 1024
                    || _pendingProgressBytes + message.Length > 4 * 1024 * 1024)
                    OverflowPending();
                else
                {
                    pending.Add((message.ToArray(), at));
                    _pendingProgressBytes += message.Length;
                    SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
                }
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
                    if (accepted) PetBaselineAt = at;
                    break;
                case 0x9009 when PetProgressMessage.TryParseAcquired(message) is { } acquired:
                    parsed = true;
                    if (acquired.EntityKey == _identity.EntityKey)
                        accepted = progress.ApplyNetworkAcquisition(acquired, context);
                    break;
                case 0x900D when LootMessage.TryParse(message) is { } gains:
                    parsed = true;
                    accepted = progress.ApplyNetworkGain(gains, context);
                    if (accepted) Count(gains, at);
                    break;
                case 0x900E when PetProgressMessage.TryParseSpent(message) is { } spent:
                    parsed = true;
                    accepted = progress.ApplyNetworkSpent(spent, context);
                    break;
                case 0x9232 when ExplorationProgressMessage.TryParseTeleports(message) is { } teleports:
                    parsed = true;
                    accepted = exploration?.ApplyNetworkTeleports(teleports, context) == true;
                    if (accepted) TeleportBaselineAt = at;
                    break;
                case 0x8D09 when ExplorationProgressMessage.TryParseQuests(message) is { } quests:
                    parsed = true;
                    accepted = exploration?.ApplyNetworkQuests(quests, context) == true;
                    if (accepted) QuestBaselineAt = at;
                    break;
            }
            if (!parsed)
            {
                ResetProgressContext();
                ProgressStatus = "Netzwerkformat nicht unterstützt. Gespeicherter Fortschritt bleibt erhalten.";
                SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
                return;
            }
            ProgressStatus = PetBaselineAt is null
                ? "Charakter erkannt. Warte auf den vollständigen Pet-Fortschritt."
                : QuestBaselineAt is not null && TeleportBaselineAt is not null
                    ? "Netzwerkdaten aktuell. Der Erkundungsstatus ist noch unbestätigt."
                    : "Pet-Fortschritt aktuell. Warte auf die Erkundungsdaten.";
            if (accepted) SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
        }
    }

    private void OverflowPending()
    {
        _pendingProgress.Clear();
        _pendingProgressBytes = 0;
        _pendingProgressOverflow = true;
        ProgressStatus = "Charakter noch unbekannt. Bitte das Gebiet wechseln, um den Fortschritt einzulesen.";
        SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
    }

    internal void ObserveIdentity(string stream, OwnCharacterIdentity identity, DateTimeOffset at)
    {
        lock (_gate)
        {
            // A world-entry message begins a new logical session even on the same TCP connection.
            var pending = _identity is null && _pendingProgress.TryGetValue(stream, out var buffered)
                ? buffered.ToArray() : [];
            ResetProgressContext();
            _identity = identity;
            _identityStream = stream;
            Character = identity.Name;
            var context = new NetworkProgressContext(identity, _progressGeneration, 0, at);
            if (!progress.BindNetworkContext(context))
            {
                ResetProgressContext();
                return;
            }
            exploration?.BindNetworkContext(context);
            BossSessionReset?.Invoke();
            OwnCharacterEntered?.Invoke(identity);
            CharacterEntered?.Invoke(identity.Name);
            foreach (var item in pending)
                ProcessProgressMessage(stream, item.Message, item.At);
            if (pending.Length == 0)
                ProgressStatus = "Charakter erkannt. Warte auf den vollständigen Pet-Fortschritt.";
            SafeEvent.Raise(Changed, "Netzwerk-Fortschritt");
        }
    }

    internal void ResetProgressContext()
    {
        lock (_gate)
        {
            _progressGeneration++;
            _progressSequence = 0;
            _identity = null;
            _identityStream = null;
            Character = null;
            _pendingProgress.Clear();
            _pendingProgressBytes = 0;
            _pendingProgressOverflow = false;
            PetBaselineAt = QuestBaselineAt = TeleportBaselineAt = null;
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
