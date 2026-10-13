using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Soulcrest.App.Network;

/// <summary>An incoming endpoint covered by an installed kernel filter.</summary>
public readonly record struct CaptureEndpoint(string Address, int Port);

/// <summary>One connection owned by the game process in the same Windows TCP-table snapshot.</summary>
public readonly record struct CaptureConnection(int GamePid, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort)
{
    public CaptureEndpoint RemoteEndpoint => new(RemoteAddress, RemotePort);
    // Include both addresses and the owner: a reused port must never inherit another session.
    public string StreamKey => $"{GamePid}:{RemoteAddress}:{RemotePort}>{LocalAddress}:{LocalPort}";
    public bool MatchesIncoming(IPAddress source, int sourcePort, IPAddress destination, int destinationPort) =>
        RemotePort == sourcePort && LocalPort == destinationPort &&
        RemoteAddress == source.ToString() && LocalAddress == destination.ToString();
}

/// <summary>
/// What to capture for the network loot source: the game's server connections, or - when the game runs
/// through a local relay like ExitLag (AION2 → 127.0.0.1:&lt;port&gt; → tunnel) - its connections to that relay
/// on the loopback adapter. Only the game's own traffic, decided from the Windows TCP table (process ids).
/// </summary>
public sealed record CaptureTarget(string Device, string Filter, IReadOnlySet<int> ServerPorts, IReadOnlySet<int> GamePorts, string Description,
    IReadOnlySet<CaptureConnection>? Connections = null)
{
    public const string LoopbackDevice = @"\Device\NPF_Loopback";
    public IReadOnlySet<CaptureEndpoint> RemoteEndpoints => Connections is { } connections
        ? connections.Select(connection => connection.RemoteEndpoint).ToHashSet() : new HashSet<CaptureEndpoint>();

    public static CaptureTarget? Find()
    {
        var gamePids = Process.GetProcessesByName("AION2").Select(p => { using (p) return p.Id; }).ToHashSet();
        return gamePids.Count == 0 ? null : FromRows(gamePids, TcpTable.Read(), DeviceFor, ProcessName);
    }

    /// <summary>Pure target construction; duplicate/reordered TCP-table rows do not change the filter.</summary>
    internal static CaptureTarget? FromRows(IReadOnlySet<int> gamePids, IReadOnlyList<TcpTable.Row> table,
        Func<IPAddress, string?> deviceFor, Func<int, string> processName)
    {
        var game = table.Where(r => gamePids.Contains(r.Pid) && r.Established).ToList();

        // Relay: a loopback connection whose other end belongs to another process.
        var relay = game.Where(r => IPAddress.IsLoopback(r.Remote.Address)
                && table.Any(o => o.Local.Port == r.Remote.Port && IPAddress.IsLoopback(o.Local.Address) && !gamePids.Contains(o.Pid)))
            .ToList();
        if (relay.Count > 0)
        {
            var ports = relay.Select(r => r.Remote.Port).Distinct().Order().ToArray();
            var owner = table.First(o => o.Local.Port == relay[0].Remote.Port && !gamePids.Contains(o.Pid)).Pid;
            var name = processName(owner);
            return Create(LoopbackDevice, relay, $"über lokalen Relay {name} (Port {string.Join(", ", ports)})");
        }

        var remote = game.Where(r => !IPAddress.IsLoopback(r.Remote.Address)).ToList();
        if (remote.Count == 0)
            return null;
        var device = deviceFor(remote[0].Remote.Address);
        if (device is null)
            return null;
        return Create(device, remote, "direkt (" + string.Join(", ", remote.Select(r => r.Remote.ToString()).Distinct().Order()) + ")");
    }

    private static CaptureTarget Create(string device, IReadOnlyList<TcpTable.Row> rows, string description)
    {
        var connections = rows.Select(row => new CaptureConnection(row.Pid, row.Local.Address.ToString(), row.Local.Port,
            row.Remote.Address.ToString(), row.Remote.Port)).ToHashSet();
        var endpoints = connections.Select(connection => connection.RemoteEndpoint).Distinct()
            .OrderBy(endpoint => endpoint.Address, StringComparer.Ordinal).ThenBy(endpoint => endpoint.Port);
        var filter = "tcp and (" + string.Join(" or ", endpoints.Select(endpoint => $"(host {endpoint.Address} and port {endpoint.Port})")) + ")";
        return new(device, filter, rows.Select(row => row.Remote.Port).ToHashSet(), rows.Select(row => row.Local.Port).ToHashSet(),
            description, connections);
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return $"PID {pid}";
        }
    }

    /// <summary>Npcap device name of the interface that routes to <paramref name="address"/>.</summary>
    private static string? DeviceFor(IPAddress address)
    {
        if (GetBestInterface(BitConverter.ToUInt32(address.GetAddressBytes(), 0), out var index) != 0)
            return null;
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Supports(NetworkInterfaceComponent.IPv4) && n.GetIPProperties().GetIPv4Properties()?.Index == index);
        return adapter is null ? null : $@"\Device\NPF_{adapter.Id}";
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
}

/// <summary>IPv4 TCP connections with their owning process (GetExtendedTcpTable).</summary>
internal static class TcpTable
{
    internal readonly record struct Row(IPEndPoint Local, IPEndPoint Remote, int Pid, bool Established);

    public static List<Row> Read()
    {
        const int AfInet = 2, TcpTableOwnerPidAll = 5, MibTcpStateEstablished = 5;
        const uint ErrorInsufficientBuffer = 122;
        var size = 0;
        _ = GetExtendedTcpTable(0, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        // A connection opened between the size query and the read makes the buffer too small; an empty list
        // would close the loot capture for a while (review 2026-10-07). Ask again with the new size and room.
        nint buffer = 0;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                size += 24 * 16;
                buffer = Marshal.AllocHGlobal(size);
                var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                if (result == 0)
                    break;
                Marshal.FreeHGlobal(buffer);
                buffer = 0;
                if (result != ErrorInsufficientBuffer || attempt == 4)
                    return [];
            }
            var count = Marshal.ReadInt32(buffer);
            var rows = new List<Row>(count);
            var rowPtr = buffer + 4;
            for (var i = 0; i < count; i++, rowPtr += 24)
            {
                var state = Marshal.ReadInt32(rowPtr);
                var localAddr = (uint)Marshal.ReadInt32(rowPtr, 4);
                var localPort = Port(Marshal.ReadInt32(rowPtr, 8));
                var remoteAddr = (uint)Marshal.ReadInt32(rowPtr, 12);
                var remotePort = Port(Marshal.ReadInt32(rowPtr, 16));
                var pid = Marshal.ReadInt32(rowPtr, 20);
                rows.Add(new Row(new IPEndPoint(localAddr, localPort), new IPEndPoint(remoteAddr, remotePort), pid, state == MibTcpStateEstablished));
            }
            return rows;
        }
        finally
        {
            if (buffer != 0)
                Marshal.FreeHGlobal(buffer);
        }
    }

    // The port sits in network byte order in the low 16 bits.
    private static int Port(int raw) => ((raw & 0xff) << 8) | ((raw >> 8) & 0xff);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int afInet, int tableClass, uint reserved);
}
