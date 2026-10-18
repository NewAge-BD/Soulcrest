using Soulcrest.Core.Network;

namespace Soulcrest.Core.Combat;

public enum CombatAmountKind { Unknown, Damage, Healing }
public sealed record CombatEffectAmountRule(bool IsPeriodic, uint EffectDataId, CombatAmountKind Kind,
    int ExactHpObservations, int IndependentCaptures);
public sealed record CombatAmountClassification(CombatAmountKind Kind, long? Amount, string? EvidenceId,
    string? UnknownReason, bool IsEmpirical);

/// <summary>
/// Explicit, evidence-scoped effect facts, never a skill-id or sign/target heuristic. These empirical
/// facts do not establish a complete catalog, game build, own group, raw healing, overkill or encounter.
/// A caller must explicitly select the matching evidence scope and retain empirical quality in reports.
/// Missing effects/forms/scopes/amounts remain unknown; unknown never defaults to damage.
/// </summary>
public sealed class CombatAmountClassifier : ICombatAmountClassifier
{
    private readonly Dictionary<(bool IsPeriodic, uint EffectDataId), CombatEffectAmountRule> _rules;
    public string EvidenceId { get; }
    public string EvidenceScope { get; }
    public int RuleCount => _rules.Count;

    public CombatAmountClassifier(string evidenceId, string evidenceScope, IEnumerable<CombatEffectAmountRule> rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceScope);
        ArgumentNullException.ThrowIfNull(rules);
        EvidenceId = evidenceId;
        EvidenceScope = evidenceScope;
        _rules = [];
        foreach (var rule in rules.Take(10001))
        {
            if (_rules.Count >= 10000 || rule is null || rule.EffectDataId == 0 ||
                rule.Kind is not (CombatAmountKind.Damage or CombatAmountKind.Healing) ||
                rule.IndependentCaptures < 2 || rule.ExactHpObservations < 4 ||
                rule.IndependentCaptures > rule.ExactHpObservations ||
                !_rules.TryAdd((rule.IsPeriodic, rule.EffectDataId), rule))
                throw new ArgumentException("Invalid, insufficiently corroborated or duplicate effect fact.", nameof(rules));
        }
    }

    public CombatAmountClassification Classify(CombatHitObservation hit, string? confirmedEvidenceScope)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (!StringComparer.Ordinal.Equals(EvidenceScope, confirmedEvidenceScope)) return Unknown("evidence-scope-unconfirmed");
        if (hit.Amount is null) return Unknown("amount-unobserved");
        if (hit.Amount < 0) return Unknown("invalid-amount");
        if (!_rules.TryGetValue((hit.IsPeriodic, hit.EffectDataId), out var rule)) return Unknown("effect-semantics-unconfirmed");
        return new(rule.Kind, hit.Amount, EvidenceId, null, true);

        CombatAmountClassification Unknown(string reason) => new(CombatAmountKind.Unknown, hit.Amount, null, reason, false);
    }
}
