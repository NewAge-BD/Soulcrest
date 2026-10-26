namespace Soulcrest.Core.Network;

/// <summary>
/// Complete structural observation of GatherSourceCompleted. The first two bytes are deliberately
/// not interpreted as success: observed material rewards use different first-byte values.
/// </summary>
public sealed record GatheringCompletionObservation(byte ResultCandidate, byte Unknown, int TargetKey,
    int RewardItemId, int BasicCount, int BonusCount, int AdditionalItemId, int AdditionalCount);

/// <summary>
/// Complete actor/target witness. The two intervening bytes are opaque and can only be compared
/// with a completion; this observation alone grants neither own authority nor a successful reward.
/// </summary>
public sealed record GatheringOwnershipWitness(int ActorKey, byte OpaqueA, byte OpaqueB, int TargetKey);

public enum GatheringRewardQualificationStatus { Invalid, NoReward, Qualified, UnsupportedAdditional }

/// <summary>Only Qualified carries a positive primary item and quantity; other outcomes carry no gain.</summary>
public readonly record struct GatheringRewardQualification(GatheringRewardQualificationStatus Status,
    int? ItemId = null, int? Quantity = null);

/// <summary>Transient environment entity facts, not a stable map node or an own gathering action.</summary>
public sealed record GatheringObjectObservation(int EntityKey, int DataId, int OriginSpawnKey,
    int? SpawnUniqueKey);

public sealed record GatheringObjectRemoval(int EntityKey, byte State, byte Reason, int ActionId);

/// <summary>
/// Bounded complete readers reconstructed independently from static wire facts and existing offline
/// traffic. Structural decoding does not establish own authority, a stable node join, or production
/// counting. Primary reward qualification checks amounts only; callers must independently correlate
/// the complete own witness. No player coordinates or strings are returned.
/// </summary>
public static class GatheringMessage
{
    public static GatheringOwnershipWitness? TryParseOwnershipWitness(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x8D34)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var actor = r.Vi32();
            var opaqueA = r.U8();
            var opaqueB = r.U8();
            var target = r.Vi32();
            r.Exact();
            return actor > 0 && target > 0 ? new(actor, opaqueA, opaqueB, target) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// Qualifies only the observed primary-item amount. Outcome bytes are not success enums.
    /// Any additional-item field is unsupported, so an unqualified extra can never be silently lost
    /// while counting a primary reward. This does not establish ownership or identify a map node.
    /// </summary>
    public static GatheringRewardQualification QualifyPrimaryReward(GatheringCompletionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.AdditionalItemId != 0 || observation.AdditionalCount != 0)
            return new(GatheringRewardQualificationStatus.UnsupportedAdditional);
        if (observation.BasicCount < 0 || observation.BonusCount < 0 || observation.RewardItemId < 0)
            return new(GatheringRewardQualificationStatus.Invalid);
        int quantity;
        try { quantity = checked(observation.BasicCount + observation.BonusCount); }
        catch (OverflowException) { return new(GatheringRewardQualificationStatus.Invalid); }
        if (quantity == 0) return new(GatheringRewardQualificationStatus.NoReward);
        return observation.RewardItemId > 0
            ? new(GatheringRewardQualificationStatus.Qualified, observation.RewardItemId, quantity)
            : new(GatheringRewardQualificationStatus.Invalid);
    }

    public static GatheringCompletionObservation? TryParseCompletion(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x8D33)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var result = r.U8();
            var unknown = r.U8();
            var target = r.Vi32();
            var item = r.I32();
            var basic = r.I32();
            var bonus = r.I32();
            var additional = r.I32();
            var additionalCount = r.I32();
            r.Exact();
            return new(result, unknown, target, item, basic, bonus, additional, additionalCount);
        }
        catch (InvalidDataException) { return null; }
    }

    public static GatheringObjectObservation? TryParseObject(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x3634)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var key = r.Vi32();
            var flags = r.U8();
            if ((flags & ~15) != 0) return null;
            var descriptorFlags = r.U8();
            if ((descriptorFlags & ~31) != 0) return null;
            if ((descriptorFlags & 1) != 0) r.SkipString();
            var dataId = r.I32();
            if ((descriptorFlags & 2) != 0) r.Skip(1);
            if ((descriptorFlags & 4) != 0) r.Skip(1);
            if ((descriptorFlags & 8) != 0) r.Skip(5);
            if ((descriptorFlags & 16) != 0) r.Skip(1);
            r.Skip(12); // object position deliberately not returned or used for node association
            var origin = r.I32();
            r.Skip(4 + 1); // direction and object state
            r.Bool();
            r.Skip(1 + 8 + 4); // reason, create time, interaction count
            if ((flags & 1) != 0) r.Skip(8);
            r.Skip(4); // hit count
            if ((flags & 2) != 0) r.Skip(8);
            int? unique = (flags & 4) != 0 ? r.I32() : null;
            r.Skip(2); // relationship and phase
            if ((flags & 8) != 0) r.Skip(4);
            r.Vi32(); // spawn action
            r.Exact();
            return key > 0 && dataId > 0 ? new(key, dataId, origin, unique) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static GatheringObjectRemoval? TryParseRemoval(ReadOnlySpan<byte> message)
    {
        if (!PetProgressMessage.HasOpcode(message, 0x3635)) return null;
        try
        {
            var r = new ProgressPacketReader(message[2..]);
            var key = r.Vi32();
            var state = r.U8();
            var reason = r.U8();
            var action = r.Vi32();
            r.Exact();
            return key > 0 ? new(key, state, reason, action) : null;
        }
        catch (InvalidDataException) { return null; }
    }
}
