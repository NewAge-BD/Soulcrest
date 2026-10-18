using Soulcrest.Core.Network;

namespace Soulcrest.Core.Combat;

/// <summary>
/// Explicitly selected primary-amount evidence. This contract grants neither a complete catalog
/// nor capture, group, encounter or auxiliary-amount authority.
/// </summary>
public interface ICombatAmountClassifier
{
    string EvidenceId { get; }
    string EvidenceScope { get; }
    CombatAmountClassification Classify(CombatHitObservation hit, string? confirmedEvidenceScope);
}
