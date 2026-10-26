using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using PacketDotNet;
using SharpPcap;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Production packet dispatch with synthetic TCP tuples; no capture device, game or OS lookup.</summary>
[Collection("Settings file")]
public sealed class NetworkBossOverlayDispatchTests
{
    private static readonly CaptureConnection World = new(42, "192.0.2.2", 50000, "192.0.2.1", 9000);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReopeningMapReplacesExpiredOverlayRowsThroughTheProductionPacketPath(bool fragmented)
    {
        using var fixture = new Fixture();
        var now = fixture.UnixNow;
        var previous = Enumerable.Range(111001, 6).Select((id, index) =>
            new BossSpawn(id, false, now + (index < 5 ? -(index + 1) * 60000 : 30000))).ToArray();

        fixture.Send([.. Frame(Clock(now)), .. Frame(List(previous))]);

        Assert.Equal(2, fixture.Network.Messages);
        Assert.Equal(6, fixture.Bosses.OverlaySpawns.Count);
        Assert.Equal(5, fixture.Bosses.OverlaySpawns.Count(row => row.Remaining == TimeSpan.Zero));
        Assert.All(fixture.Bosses.OverlaySpawns, row => Assert.False(row.Cached));

        // A new map reply supplies the next cycle for a killed boss and a positive live flag for another.
        BossSpawn[] updated = [new(111001, false, now + 3600000), new(111002, false, now + 600000),
            new(111003, true, now - 1000), new(111004, false, now + 900000),
            new(111005, false, now + 1200000), new(111006, false, now + 30000)];
        var packet = Frame(List(updated));
        if (fragmented)
        {
            // Split both framing and payload; only the complete list may replace the existing snapshot.
            fixture.Send(packet[..1]);
            fixture.Send(packet[1..27]);
            Assert.Equal(5, fixture.Bosses.OverlaySpawns.Count(row => row.Remaining == TimeSpan.Zero));
            fixture.Send(packet[27..]);
        }
        else fixture.Send(packet);

        Assert.Equal(3, fixture.Network.Messages);
        Assert.Equal(6, fixture.Bosses.Schedule.Count);
        Assert.True(Assert.Single(fixture.Bosses.Schedule, row => row.Place.Boss.SpawnId == 111003).Spawned);
        Assert.Equal(new[] { 111006, 111002, 111004, 111005, 111001 },
            fixture.Bosses.OverlaySpawns.Select(row => row.Place.Boss.SpawnId));
        Assert.All(fixture.Bosses.OverlaySpawns, row =>
        {
            Assert.False(row.Spawned);
            Assert.False(row.Cached);
            Assert.True(row.Remaining > TimeSpan.Zero);
        });
        Assert.Equal(TimeSpan.FromHours(1),
            Assert.Single(fixture.Bosses.OverlaySpawns, row => row.Place.Boss.SpawnId == 111001).Remaining);
        Assert.False(fixture.Bosses.View.Cached);
        var spawned = Assert.Single(fixture.Targets.BossNavigationTargets, target => target.BossRole == "spawned");
        Assert.Equal("Synthetic boss 3", spawned.Name);
        Assert.Equal("#4ade80", spawned.Color);
    }

    [Theory]
    [InlineData((byte)0x11)]
    [InlineData((byte)0x14)]
    public void ReopenedListAfterStreamClosureConfirmsBossDeathOnlyAfterItsOwnNewClock(byte closeFlags)
    {
        using var fixture = new Fixture();
        var now = fixture.UnixNow;
        fixture.Send([.. Frame(Clock(now)), .. Frame(List(new(111001, true, now - 1000),
            new(111002, false, now + 30000)))]);
        Assert.True(fixture.Bosses.View.Spawned);
        Assert.Single(fixture.Bosses.OverlaySpawns);

        fixture.Send([], closeFlags);
        Assert.True(fixture.Bosses.View.Cached);
        Assert.All(fixture.Bosses.Schedule, row => Assert.True(row.Cached));
        var received = fixture.Network.Messages;

        // Even a perfectly formed clock/list on a foreign tuple cannot refresh the game-owned schedule.
        fixture.SendForeign(World with { LocalPort = 50001 },
            [.. Frame(Clock(now)), .. Frame(List(new BossSpawn(111001, false, now + 90000)))]);
        Assert.Equal(received, fixture.Network.Messages);
        Assert.True(fixture.Bosses.View.Cached);

        fixture.RestartSequence();
        fixture.Send(Frame(Clock(now)));
        Assert.True(fixture.Bosses.View.Cached); // A clock alone is not a fresh boss observation.
        Assert.True(Assert.Single(fixture.Bosses.Schedule, row => row.Place.Boss.SpawnId == 111001).Spawned);

        fixture.Send(Frame(List(new(111001, false, now + 90000), new(111002, false, now + 180000))));

        Assert.False(fixture.Bosses.View.Cached);
        Assert.False(fixture.Bosses.View.Spawned);
        Assert.Equal(new[] { 111001, 111002 }, fixture.Bosses.OverlaySpawns.Select(row => row.Place.Boss.SpawnId));
        Assert.Equal(new[] { TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(180) },
            fixture.Bosses.OverlaySpawns.Select(row => row.Remaining));
        Assert.All(fixture.Bosses.OverlaySpawns, row =>
        {
            Assert.False(row.Spawned);
            Assert.False(row.Cached);
        });
        Assert.DoesNotContain(fixture.Targets.BossNavigationTargets, target => target.BossRole == "spawned");
        Assert.Equal("Synthetic boss 1", Assert.Single(fixture.Targets.BossNavigationTargets, target => target.BossRole == "next").Name);
    }

    private static byte[] Clock(long now) => [0, 0x36, .. BitConverter.GetBytes(now)];

    private static byte[] List(params BossSpawn[] bosses)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write((ushort)0x9101);
        writer.Write((ushort)0);
        writer.Write(1110);
        Varint(writer, (uint)bosses.Length);
        for (var index = 0; index < bosses.Length; index++)
        {
            var boss = bosses[index];
            writer.Write((byte)(boss.Spawned ? 1 : 0));
            Varint(writer, (uint)boss.SpawnId);
            if (boss.Spawned) writer.Write(new byte[12]);
            if (index % 8 == 0)
            {
                byte mask = 0;
                for (var bit = 0; bit < 8 && index + bit < bosses.Length; bit++)
                    if (bosses[index + bit].Spawned) mask |= (byte)(1 << bit);
                writer.Write(mask);
            }
            writer.Write(boss.SpawnUnixMs ?? 0);
        }
        writer.Write(new byte[3]);
        return buffer.ToArray();
    }

    private static byte[] Frame(byte[] message)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        Varint(writer, (uint)(message.Length + 4));
        writer.Write(message);
        return buffer.ToArray();
    }

    private static void Varint(BinaryWriter writer, uint value)
    {
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }

    private static RawCapture Capture(CaptureConnection connection, uint sequence, byte[] payload, byte flags, long unixNow)
    {
        var bytes = new byte[54 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800);
        bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), checked((ushort)(bytes.Length - 14)));
        bytes[22] = 64;
        bytes[23] = 6;
        IPAddress.Parse(connection.RemoteAddress).GetAddressBytes().CopyTo(bytes, 26);
        IPAddress.Parse(connection.LocalAddress).GetAddressBytes().CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), checked((ushort)connection.RemotePort));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), checked((ushort)connection.LocalPort));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(38), sequence);
        bytes[46] = 0x50;
        bytes[47] = flags;
        payload.CopyTo(bytes, 54);
        return new(LinkLayers.Ethernet, new PosixTimeval((ulong)(unixNow / 1000), 0), bytes, null);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-boss-dispatch-" + Guid.NewGuid().ToString("N"));
        private readonly string? _mapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _language = UiText.Language;
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private uint _sequence = 1000;
        public long UnixNow { get; } = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        public NetworkLootService Network { get; }
        public BossRushService Bosses { get; }
        public MapTargetsService Targets { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(_folder, "altgard"));
            File.WriteAllText(Path.Combine(_folder, "manifest.json"),
                """{"maps":[{"id":"altgard","label":"Synthetic map","group":"Synthetic"}]}""");
            var catalogue = new
            {
                mapId = 1110,
                bosses = Enumerable.Range(1, 6).Select(index => new
                {
                    spawnId = 111000 + index, npcId = 2400000 + index,
                    en = "Synthetic boss " + index, x = 100 + index * 20, y = 200
                })
            };
            File.WriteAllText(Path.Combine(_folder, "altgard", "bosses.json"),
                System.Text.Json.JsonSerializer.Serialize(catalogue));
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            var settings = new SettingsService();
            settings.Current.BossRushEnabled = true;
            settings.Current.BossOverlayCount = 6;
            settings.Current.BossAlertsEnabled = false;
            settings.Current.BossAlertIds = [];
            settings.Current.NameLanguage = settings.Current.OverlayLanguage = "en";
            var progress = new ProgressService(Path.Combine(_folder, "progress.json"), new PetCatalog([]));
            Network = new(progress);
            Targets = new();
            Bosses = new(progress, settings, Targets, Network, new FixedTime(UnixNow), Path.Combine(_folder, "boss-timers.json"));
            var row = new TcpTable.Row(new IPEndPoint(IPAddress.Parse(World.LocalAddress), World.LocalPort),
                new IPEndPoint(IPAddress.Parse(World.RemoteAddress), World.RemotePort), World.GamePid, true);
            var target = CaptureTarget.FromRows(new HashSet<int> { World.GamePid }, [row], _ => "synthetic-adapter", _ => "Synthetic relay")!;
            typeof(NetworkLootService).GetField("_target", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Network, target);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.IncomingOwnershipReader = () => throw new InvalidOperationException("Synthetic tests must not query OS ownership.");
            Network.PeriodicOwnershipReader = Network.IncomingOwnershipReader;
        }

        public void Send(byte[] payload, byte flags = 0x10)
        {
            Network.ProcessCapturedPacket(Capture(World, _sequence, payload, flags, UnixNow));
            _sequence += (uint)payload.Length;
        }

        public void SendForeign(CaptureConnection connection, byte[] payload) =>
            Network.ProcessCapturedPacket(Capture(connection, 1000, payload, 0x10, UnixNow));

        public void RestartSequence() => _sequence = 10;

        public void Dispose()
        {
            Bosses.Dispose();
            Network.Dispose();
            Targets.Dispose();
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _mapData);
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _language;
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FixedTime(long unixNow) : TimeProvider
    {
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => 0;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(unixNow);
    }
}
