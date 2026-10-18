namespace Soulcrest.Core.Combat;

/// <summary>
/// Partial empirical facts from isolated exact HP changes in the private 2026-10-17 Global/EU corpus.
/// Different capture files are not necessarily independent runs or game versions. Game build is unknown.
/// This is neither a complete game catalog nor automatically enabled live semantics. Every other key
/// or scope stays unknown. No foreign catalog data, player identifiers or raw packets are included.
/// </summary>
public static class ObservedCombatAmountFacts
{
    public const string EvidenceScope = "global-eu-auldors-2026-10-17-v3";
    public const string EvidenceId = "auldors-hp-interval-facts-v3-20261017";

    public static CombatAmountClassifier CreateClassifier() => new(EvidenceId, EvidenceScope,
        [
            new(false, 1434003021U, CombatAmountKind.Damage, 47, 2),
            new(false, 1472000711U, CombatAmountKind.Damage, 10, 2),
            new(false, 1478000811U, CombatAmountKind.Damage, 10, 2),
            new(false, 1715000211U, CombatAmountKind.Damage, 20, 2),
            new(false, 1744003714U, CombatAmountKind.Damage, 11, 2),
            new(false, 1773000111U, CombatAmountKind.Damage, 13, 2),
            new(false, 1773000211U, CombatAmountKind.Damage, 9, 2),
            new(false, 1780000111U, CombatAmountKind.Healing, 17, 3),
            new(false, 1802034011U, CombatAmountKind.Damage, 16, 2),
            new(false, 1803034011U, CombatAmountKind.Damage, 12, 2),
            new(false, 1804003024U, CombatAmountKind.Damage, 15, 2),
            new(false, 1816000211U, CombatAmountKind.Healing, 10, 3),
            new(false, 1880000111U, CombatAmountKind.Damage, 8, 2),
            new(true, 1708024011U, CombatAmountKind.Damage, 32, 2),
            new(true, 1709014011U, CombatAmountKind.Healing, 62, 3)
        ]);
}
