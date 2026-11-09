using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Soulcrest.Core.Network;

/// <summary>Native stat number and unscaled wire value. Numeric-to-name qualification belongs to a separate catalogue.</summary>
public sealed record CharacterStatValue(ushort StatType, int Value);
public sealed record CharacterAbnormal(int AbnormalId, byte Level, bool ReadyToRemove);
public sealed record CharacterAbnormalState(ImmutableArray<CharacterAbnormal> Abnormals);
public sealed record CharacterStatObservation(int? EntityKey, ImmutableDictionary<ushort, int> Stats, bool IsComplete);
/// <summary>Only explicitly listed boards have a known node-list scope; an absent board is unknown.</summary>
public sealed record CharacterDaevanionState(ImmutableDictionary<int, ImmutableHashSet<int>> Boards);
public sealed record CharacterDaevanionPointState(ImmutableDictionary<byte, ushort> Used,
    ImmutableDictionary<byte, ushort> Bonus);
/// <summary>Absolute spent-point map; this message does not contain free points, totals or bonus points.</summary>
public sealed record CharacterDaevanionUsedPointUpdate(ImmutableDictionary<byte, ushort> Used);
public sealed record CharacterTitleState(ImmutableHashSet<int> OwnedTitleIds,
    ImmutableDictionary<byte, int> EquippedByCategory);
public sealed record CharacterAchievement(int Count, long CompleteTime, byte RewardedStep);
/// <summary>Raw positive rewarded-step acknowledgement; no counter, timestamp or group state is transmitted.</summary>
public sealed record CharacterAchievementRewardUpdate(int AchievementId, byte RewardedStep);
public sealed record CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement> Achievements,
    ImmutableDictionary<int, ImmutableArray<bool>> RewardGroups);
public sealed record CharacterMagicstone(int ItemId, ushort StatType, byte Grade);
public sealed record CharacterSkillBonus(int SkillId, byte Level);
/// <summary>Equipment details without maker strings, binding identities, expiry times or other personal fields.</summary>
public sealed record CharacterEquipmentDetail(byte EnchantLevel, int EnchantExperience, int EnchantBonusProbability,
    byte SurpassEnchantLevel, int SurpassEnchantBonusProbability, int ItemLevel, bool IsBroken, byte EquipSlot,
    ImmutableArray<CharacterMagicstone> Magicstones, byte SoulBindCount, ImmutableArray<int> Godstones,
    ImmutableArray<CharacterStatValue> ArcanaMainStats, ImmutableArray<CharacterStatValue> SubStats,
    ImmutableArray<CharacterStatValue> AdditionalSubStats, ImmutableArray<CharacterSkillBonus> SubSkills,
    ImmutableArray<CharacterSkillBonus> AdditionalSubSkills, ImmutableArray<CharacterStatValue> SoulAdditionalSubStats,
    ImmutableArray<CharacterSkillBonus> SoulAdditionalSubSkills, byte SkinExtractionCount);
public sealed record CharacterEquipmentItem(int ItemId, CharacterEquipmentDetail Detail)
{
    /// <summary>Transient matching key only. Never serialize it or include it in diagnostics.</summary>
    [JsonIgnore] public long InstanceKey { get; init; }
    public override string ToString() => $"CharacterEquipmentItem {{ ItemId = {ItemId}, Detail = {Detail} }}";
}
public sealed record CharacterEquipmentState(ImmutableArray<CharacterEquipmentItem> Items);
public sealed record CharacterGenusEffect(byte Slot, byte Grade, ushort StatType, long Value, bool IsLocked, bool IsActivated);
public sealed record CharacterGenusBoard(byte CreatureType, int Level, long Experience,
    ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>> Panels);

/// <summary>Strict, bounded passive readers. Successful decoding alone grants no own-character authority.</summary>
public static class CharacterCombatMessage
{
    internal const int MaximumMessageBytes = 1 << 20;

    public static CharacterStatObservation? TryParseStats(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes) return null;
        var full = PetProgressMessage.HasOpcode(message, 0x3649);
        if (!full && !PetProgressMessage.HasOpcode(message, 0x364A)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            int? entity = null;
            if (full) { if (r.U16() != 0) return null; }
            else { entity = r.Vi32(); if (entity <= 0) return null; }
            var stats = ReadStats(ref r);
            r.I64(); // Native trailer is not the displayed HP maximum; never replace a stat entry with it.
            r.Exact();
            return new(entity, stats, full);
        }
        catch (InvalidDataException) { return null; }
    }

    public static long? TryParseCombatScore(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x3656)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var current = r.I64(); r.I64(); r.Exact();
            return current >= 0 ? current : null;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// The qualified 0xE22E variant contains exactly native types 1..5. The controlled
    /// 2026-11-03 refund/rebuy showed Normal Used 416 -> 415 -> 416 while Free was 0 -> 1 -> 0.
    /// Unknown, incomplete or future variants remain unsupported; no absent-type zeroes.
    /// </summary>
    public static CharacterDaevanionUsedPointUpdate? TryParseUsedDaevanionPoints(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes || !PetProgressMessage.HasOpcode(message, 0xE22E)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var values = ReadPoints(ref r);
            r.Exact();
            return values.Count == 5 && values.Keys.All(type => type is >= 1 and <= 5) ? new(values) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static CharacterDaevanionState? TryParseBoards(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes || !PetProgressMessage.HasOpcode(message, 0xE226)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var result = ImmutableDictionary.CreateBuilder<int, ImmutableHashSet<int>>();
            var count = r.Count(5, 256);
            for (var i = 0; i < count; i++)
            {
                var board = r.I32(); var nodes = ImmutableHashSet.CreateBuilder<int>();
                var size = r.Count(4, 4096);
                for (var j = 0; j < size; j++) { var node = r.I32(); if (node <= 0 || !nodes.Add(node)) return null; }
                if (board <= 0 || !result.TryAdd(board, nodes.ToImmutable())) return null;
            }
            r.Exact(); return new(result.ToImmutable());
        }
        catch (InvalidDataException) { return null; }
    }

    public static CharacterTitleState? TryParseTitles(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes || !PetProgressMessage.HasOpcode(message, 0x5679)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]); var owned = ImmutableHashSet.CreateBuilder<int>();
            var count = r.Count(4);
            for (var i = 0; i < count; i++) { var id = r.I32(); if (id <= 0 || !owned.Add(id)) return null; }
            var equipped = ImmutableDictionary.CreateBuilder<byte, int>(); count = r.Count(5, 256);
            for (var i = 0; i < count; i++)
            {
                var slot = r.U8(); var id = r.I32();
                if (id < 0 || !equipped.TryAdd(slot, id)) return null;
            }
            r.Exact(); return new(owned.ToImmutable(), equipped.ToImmutable());
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>The observed 5686 success variant is exactly zero-result U16, positive I32 ID and positive step byte.</summary>
    public static CharacterAchievementRewardUpdate? TryParseAchievementReward(ReadOnlySpan<byte> message)
    {
        if (message.Length != 9 || !PetProgressMessage.HasOpcode(message, 0x5686)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            if (r.U16() != 0) return null;
            var id = r.I32(); var step = r.U8();
            r.Exact();
            return id > 0 && step > 0 ? new(id, step) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static CharacterAchievementState? TryParseAchievements(ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessageBytes ||
            !PetProgressMessage.HasOpcode(message, 0x5683) && !PetProgressMessage.HasOpcode(message, 0x5684)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var values = ImmutableDictionary.CreateBuilder<int, CharacterAchievement>(); var count = r.Count(17, 50000);
            for (var i = 0; i < count; i++)
            {
                var id = r.I32(); var value = new CharacterAchievement(r.I32(), r.I64(), r.U8());
                if (id <= 0 || !values.TryAdd(id, value)) return null;
            }
            var groups = ImmutableDictionary.CreateBuilder<int, ImmutableArray<bool>>();
            if (PetProgressMessage.HasOpcode(message, 0x5683))
            {
                count = r.Count(5);
                for (var i = 0; i < count; i++)
                {
                    var id = r.I32(); var size = r.Count(0, 100000);
                    if (size > (long)r.Remaining * 8 + 8) return null;
                    var bits = ImmutableArray.CreateBuilder<bool>(size);
                    for (var j = 0; j < size; j++) bits.Add(r.Bool());
                    if (id <= 0 || !groups.TryAdd(id, bits.MoveToImmutable())) return null;
                }
            }
            r.Exact(); return new(values.ToImmutable(), groups.ToImmutable());
        }
        catch (InvalidDataException) { return null; }
    }

    internal static ImmutableDictionary<ushort, int> ReadStats(ref ProgressPacketReader r)
    {
        var values = ImmutableDictionary.CreateBuilder<ushort, int>(); var count = r.Count(6);
        for (var i = 0; i < count; i++) if (!values.TryAdd(r.U16(), r.I32())) throw new InvalidDataException("Duplicate stat.");
        return values.ToImmutable();
    }

    internal static ImmutableDictionary<byte, ushort> ReadPoints(ref ProgressPacketReader r)
    {
        var values = ImmutableDictionary.CreateBuilder<byte, ushort>(); var count = r.Count(3, 256);
        for (var i = 0; i < count; i++) if (!values.TryAdd(r.U8(), r.U16())) throw new InvalidDataException("Duplicate point type.");
        return values.ToImmutable();
    }

    internal static ImmutableArray<CharacterStatValue> ReadStatArray(ref ProgressPacketReader r)
    {
        var count = r.Count(6); var values = ImmutableArray.CreateBuilder<CharacterStatValue>(count);
        for (var i = 0; i < count; i++) values.Add(new(r.U16(), r.I32()));
        return values.MoveToImmutable();
    }

    internal static ImmutableArray<CharacterSkillBonus> ReadSkillArray(ref ProgressPacketReader r)
    {
        var count = r.Count(5); var values = ImmutableArray.CreateBuilder<CharacterSkillBonus>(count);
        for (var i = 0; i < count; i++) values.Add(new(r.I32(), r.U8()));
        return values.MoveToImmutable();
    }
}
