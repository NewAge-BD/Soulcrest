using Soulcrest.Core.Network;

namespace Soulcrest.Core.Combat;

public sealed record CombatNativeEffectRow(bool IsPeriodic, uint EffectDataId, string NativeEffectType);

/// <summary>
/// An explicitly scoped subset of native effect facts from the caller's local input. Only the
/// independently corroborated primary HP types are classified. Native types for other resources,
/// barriers and unconfirmed behavior stay unknown. No ID arithmetic or auxiliary amount fallback.
/// These facts establish neither the captured game build nor a complete DPS capability.
/// </summary>
public sealed class CombatNativeEffectClassifier : ICombatAmountClassifier
{
    public const int MaximumRows = 50000;
    public const int MaximumNativeTypeLength = 128;
    private readonly Dictionary<(bool IsPeriodic, uint EffectDataId), CombatAmountKind> _kinds = [];

    public string EvidenceId { get; }
    public string EvidenceScope { get; }
    public int RowCount => _kinds.Count;

    public CombatNativeEffectClassifier(string evidenceId, string evidenceScope, IEnumerable<CombatNativeEffectRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceScope);
        ArgumentNullException.ThrowIfNull(rows);
        EvidenceId = evidenceId;
        EvidenceScope = evidenceScope;
        foreach (var row in rows.Take(MaximumRows + 1))
        {
            if (_kinds.Count >= MaximumRows || row is null || row.EffectDataId == 0 ||
                !ValidNativeType(row.IsPeriodic, row.NativeEffectType) ||
                !_kinds.TryAdd((row.IsPeriodic, row.EffectDataId), Kind(row.IsPeriodic, row.NativeEffectType)))
                throw new ArgumentException("Invalid, oversized or duplicate native effect fact.", nameof(rows));
        }
    }

    public CombatAmountClassification Classify(CombatHitObservation hit, string? confirmedEvidenceScope)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (!StringComparer.Ordinal.Equals(EvidenceScope, confirmedEvidenceScope)) return Unknown("evidence-scope-unconfirmed");
        if (hit.Amount is null) return Unknown("amount-unobserved");
        if (hit.Amount < 0) return Unknown("invalid-amount");
        if (!_kinds.TryGetValue((hit.IsPeriodic, hit.EffectDataId), out var kind) || kind == CombatAmountKind.Unknown)
            return Unknown("effect-semantics-unconfirmed");
        return new(kind, hit.Amount, EvidenceId, null, false);

        CombatAmountClassification Unknown(string reason) => new(CombatAmountKind.Unknown, hit.Amount, null, reason, false);
    }

    private static CombatAmountKind Kind(bool periodic, string nativeType) => (periodic, nativeType) switch
    {
        (false, "ESkillEffectType::Damage") => CombatAmountKind.Damage,
        (false, "ESkillEffectType::HpHeal") => CombatAmountKind.Healing,
        (true, "EAbnormalEffectType::Dot_NormalCalc") => CombatAmountKind.Damage,
        (true, "EAbnormalEffectType::Hot") => CombatAmountKind.Healing,
        _ => CombatAmountKind.Unknown
    };

    private static bool ValidNativeType(bool periodic, string? nativeType)
    {
        if (string.IsNullOrEmpty(nativeType) || nativeType.Length > MaximumNativeTypeLength) return false;
        var prefix = periodic ? "EAbnormalEffectType::" : "ESkillEffectType::";
        if (!nativeType.StartsWith(prefix, StringComparison.Ordinal) || nativeType.Length == prefix.Length) return false;
        var member = nativeType.AsSpan(prefix.Length);
        if (!char.IsAsciiLetter(member[0]) && member[0] != '_') return false;
        foreach (var character in member)
            if (!char.IsAsciiLetterOrDigit(character) && character != '_') return false;
        return true;
    }
}
