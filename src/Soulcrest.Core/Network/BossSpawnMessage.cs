using System.Buffers.Binary;

namespace Soulcrest.Core.Network;

public sealed record BossSpawn(int SpawnId, bool Spawned, long SpawnUnixMs);
public sealed record BossSpawnList(int MapId, IReadOnlyList<BossSpawn> Bosses);

/// <summary>Observed 0191 boss lists (2026-10-03/06). Optional NPC data is skipped, never decoded.</summary>
public static class BossSpawnMessage
{
    public static BossSpawnList? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || data[0] != 0x01 || data[1] != 0x91 || data[2] != 0 || data[3] != 0)
            return null;
        var map = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int pos = 8;
        if (map <= 0 || !ReadVarint(data, ref pos, out var count) || count is < 1 or > 128)
            return null;
        var bosses = new List<BossSpawn>();
        var ids = new HashSet<int>();
        byte mask = 0;
        for (var i = 0; i < count; i++)
        {
            if (pos >= data.Length || data[pos] > 1) return null;
            bool spawned = data[pos++] == 1;
            if (!ReadVarint(data, ref pos, out var id) || id <= 0 || !ids.Add(id)) return null;
            if (spawned) pos += 12;
            if (i % 8 == 0)
            {
                if (pos >= data.Length) return null;
                mask = data[pos++];
            }
            if (pos > data.Length - 8 || (((mask >> (i % 8)) & 1) != 0) != spawned) return null;
            var at = BinaryPrimitives.ReadInt64LittleEndian(data[pos..]);
            pos += 8;
            if (!ValidTime(at)) return null;
            bosses.Add(new BossSpawn(id, spawned, at));
        }
        // Unknown suffix kept strict until another format is independently evidenced.
        if (data.Length - pos != 3 || data[pos] != 0 || data[pos + 1] != 0 || data[pos + 2] != 0)
            return null;
        return new BossSpawnList(map, bosses);
    }

    public static long? ReadServerClock(ReadOnlySpan<byte> data) =>
        data.Length == 10 && data[0] == 0 && data[1] == 0x36
            && BinaryPrimitives.ReadInt64LittleEndian(data[2..]) is var time && ValidTime(time) ? time : null;

    private static bool ValidTime(long time) => time is >= 1577836800000 and < 4102444800000; // 2020–2100

    private static bool ReadVarint(ReadOnlySpan<byte> data, ref int pos, out int value)
    {
        uint result = 0;
        value = 0;
        for (var shift = 0; shift <= 28 && pos < data.Length; shift += 7)
        {
            byte b = data[pos++];
            if (shift == 28 && b > 7) return false;
            result |= (uint)(b & 127) << shift;
            if ((b & 128) == 0)
            {
                value = (int)result;
                return true;
            }
        }
        return false;
    }
}
