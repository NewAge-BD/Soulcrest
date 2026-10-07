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
public sealed class NetworkLootService(ProgressService progress) : INetworkLootSource, IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly object _connectionGate = new();
    private readonly Dictionary<(string Source, int SourcePort, int DestinationPort), (TcpReassembler Tcp, GameMessageStream Messages)> _streams = [];
    private readonly List<NetworkSoul> _recent = [];
    private ILiveDevice? _device;
    private CaptureTarget? _target;
    private System.Threading.Timer? _check;

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
            foreach (var message in Feed(streams, raw, serverPorts, null))
            {
                if (LootMessage.TryParse(message) is { } gains)
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
            foreach (var message in Feed(streams, raw, serverPorts, null))
            {
                if (CharacterMessage.TryParseName(message) is { } name)
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
        }
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
        List<byte[]> messages;
        lock (_gate)
        {
            if (!Running || !ReferenceEquals(sender, _device) || _target is not { } target)
                return;
            Packets++;
            messages = Feed(_streams, raw, target.ServerPorts, target.GamePorts);
            Messages += messages.Count;
            Resyncs = _streams.Values.Sum(s => s.Messages.Resyncs);
        }
        foreach (var message in messages)
        {
            if (LootMessage.TryParse(message) is { } gains)
                Count(gains);
            else if (CharacterMessage.TryParseName(message) is { } name)
            {
                Character = name;
                CharacterEntered?.Invoke(name);
            }
        }
    }

    /// <summary>Server → game TCP payload of one packet through reassembly and message splitting.</summary>
    private static List<byte[]> Feed(Dictionary<(string, int, int), (TcpReassembler Tcp, GameMessageStream Messages)> streams, RawCapture raw,
        IReadOnlySet<int> serverPorts, IReadOnlySet<int>? gamePorts)
    {
        var result = new List<byte[]>();
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
            streams[key] = stream = (new TcpReassembler(), new GameMessageStream());
        foreach (var chunk in stream.Tcp.Add(tcp.SequenceNumber, payload))
        {
            if (chunk.Gap)
                stream.Messages.Break();
            if (chunk.Data.Length > 0)
                result.AddRange(stream.Messages.Append(chunk.Data));
        }
        return result;
    }

    private void Count(IReadOnlyList<SoulGain> gains)
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
            var soul = new NetworkSoul(DateTimeOffset.Now, gain.PetNumber, petId, name, gain.Souls);
            progress.AddSouls(petId, gain.Souls, "network", $"0d90 #{gain.PetNumber} {name} x{gain.Souls}");
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
