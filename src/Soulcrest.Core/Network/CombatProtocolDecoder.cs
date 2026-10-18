using System.Buffers.Binary;

namespace Soulcrest.Core.Network;

/// <summary>Validated wire observations, not attributed combat events or proof of encounter completeness.</summary>
public abstract record CombatWireObservation(ushort Opcode);
public sealed record CombatWireHit(CombatHitObservation Value) : CombatWireObservation(
    Value.IsPeriodic ? CombatHitMessage.PeriodicOpcode : CombatHitMessage.DirectOpcode);
public sealed record CombatWireHp(CombatHpObservation Value) : CombatWireObservation(CombatHpMessage.Opcode);
public sealed record CombatWireParty(CombatPartySnapshot Value) : CombatWireObservation(0x9200);
public sealed record CombatWireMemberStatus(CombatPartyMemberStatus Value) : CombatWireObservation(0x921C);
public sealed record CombatWireMemberVitals(CombatPartyMemberVitals Value) : CombatWireObservation(0x921B);
public sealed record CombatWirePartyDeparture(CombatPartyDeparture Value) : CombatWireObservation(0x931F);
public sealed record CombatWireEffect(CombatEffectUpdate Value) : CombatWireObservation(Value.Operation switch
{
    CombatEffectOperation.Add => 0x382A,
    CombatEffectOperation.Change => 0x382B,
    _ => 0x382C
});

public sealed record CombatWireNpcAppearance(CombatNpcAppearance Value) : CombatWireObservation(0x3641);
public sealed record CombatWireNpcDeparture(CombatNpcDeparture Value) : CombatWireObservation(0x3642);
public sealed record CombatWireDeath(CombatEntityDeath Value) : CombatWireObservation(0x8D04);
public sealed record CombatWireMiddleBossReward(CombatMiddleBossReward Value) : CombatWireObservation(0x572E);
public sealed record CombatWireDungeonState(DungeonStateSnapshot Value) : CombatWireObservation(0x6100);
public sealed record CombatWireDungeonResult(DungeonCompletion Value) : CombatWireObservation(0x6101);

/// <summary>The shared dispatcher for live consumption and private offline replay. No raw packet is retained.</summary>
public static class CombatProtocolDecoder
{
    public static bool IsSupported(ushort opcode) => opcode is 0x3804 or 0x3805 or 0x8D00 or
        0x9200 or 0x921C or 0x921B or 0x931F or 0x382A or 0x382B or 0x382C or
        0x3641 or 0x3642 or 0x8D04 or 0x572E or 0x6100 or 0x6101;

    public static CombatWireObservation? TryParse(ReadOnlySpan<byte> message)
    {
        if (message.Length < 2) return null;
        switch (BinaryPrimitives.ReadUInt16LittleEndian(message))
        {
            case 0x3804: case 0x3805:
                return CombatHitMessage.TryParse(message, out var hit) ? new CombatWireHit(hit) : null;
            case 0x8D00:
                return CombatHpMessage.TryParse(message, out var hp) ? new CombatWireHp(hp) : null;
            case 0x9200:
                return CombatPartyMessage.TryParseSnapshot(message) is { } party ? new CombatWireParty(party) : null;
            case 0x921C:
                return CombatPartyMessage.TryParseMemberStatus(message) is { } status ? new CombatWireMemberStatus(status) : null;
            case 0x921B:
                return CombatPartyMessage.TryParseMemberVitals(message) is { } vitals ? new CombatWireMemberVitals(vitals) : null;
            case 0x931F:
                return CombatPartyMessage.TryParseDeparture(message) is { } departure ? new CombatWirePartyDeparture(departure) : null;
            case 0x382A: case 0x382B: case 0x382C:
                return CombatEffectMessage.TryParse(message) is { } effect ? new CombatWireEffect(effect) : null;
            case 0x3641:
                return CombatEncounterMessage.TryParseNpcAppearance(message) is { } npc ? new CombatWireNpcAppearance(npc) : null;
            case 0x3642:
                return CombatEncounterMessage.TryParseNpcDeparture(message) is { } gone ? new CombatWireNpcDeparture(gone) : null;
            case 0x8D04:
                return CombatEncounterMessage.TryParseDeath(message) is { } death ? new CombatWireDeath(death) : null;
            case 0x572E:
                return CombatEncounterMessage.TryParseMiddleBossReward(message) is { } reward ? new CombatWireMiddleBossReward(reward) : null;
            case 0x6100:
                return DungeonProgressMessage.TryParseState(message) is { } state ? new CombatWireDungeonState(state) : null;
            case 0x6101:
                return DungeonProgressMessage.TryParseCompletion(message) is { } result ? new CombatWireDungeonResult(result) : null;
            default: return null;
        }
    }
}
