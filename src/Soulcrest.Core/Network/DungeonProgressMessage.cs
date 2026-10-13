using System.Collections.ObjectModel;

namespace Soulcrest.Core.Network;

public enum DungeonRunState : byte { None, Prepare, Process, Complete, End }

public sealed record DungeonStateSnapshot(int DungeonId, DungeonRunState State, long StartTime, long EndTime);

public sealed record DungeonCompletion(int DungeonId, bool Succeeded, int? ContentsId,
    IReadOnlyDictionary<int, long> Rewards, int? ClearLevel);

/// <summary>
/// Passive decoding of dungeon-run state and result packets. A successful result proves a run
/// completion for a mapped sealed dungeon or stronghold; it does not establish initial exploration states.
/// </summary>
public static class DungeonProgressMessage
{
    private const int MaximumRewards = 10000;

    public static DungeonStateSnapshot? TryParseState(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x6100)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var dungeon = r.I32();
            var state = r.U8();
            var start = r.I64();
            var end = r.I64();
            r.Exact();
            if (dungeon <= 0 || state > (byte)DungeonRunState.End || start < 0 || end < 0) return null;
            return new(dungeon, (DungeonRunState)state, start, end);
        }
        catch (InvalidDataException) { return null; }
    }

    public static DungeonCompletion? TryParseCompletion(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x6101)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var flags = r.U8();
            if ((flags & ~3) != 0) return null;
            var dungeon = r.I32();
            // This bool has its own packed-bit cursor; the optional-field flags above are separate.
            var succeeded = r.Bool();
            int? contents = (flags & 1) != 0 ? r.Vi32() : null;
            if (dungeon <= 0 || contents is < 0) return null;
            var count = r.Count(12, MaximumRewards);
            var rewards = new Dictionary<int, long>(count);
            for (var i = 0; i < count; i++)
            {
                var item = r.I32();
                var amount = r.I64();
                if (item <= 0 || amount < 0 || !rewards.TryAdd(item, amount)) return null;
            }
            int? clearLevel = (flags & 2) != 0 ? r.Vi32() : null;
            if (clearLevel is < 0) return null;
            r.Exact();
            return new(dungeon, succeeded, contents, new ReadOnlyDictionary<int, long>(rewards), clearLevel);
        }
        catch (InvalidDataException) { return null; }
    }
}
