using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CombatAmountClassifierTests
{
    private const string Scope = "synthetic-game-build-and-corpus";
    private static CombatAmountClassifier Classifier() => new("synthetic-evidence-v1", Scope,
        [new(false, 1001, CombatAmountKind.Damage, 8, 2), new(false, 1002, CombatAmountKind.Healing, 6, 3)]);
    private static CombatHitObservation Hit(uint effect = 1001, bool periodic = false, long? amount = 123) =>
        new(900, 301, 2001, effect, amount, periodic, null, null, 4);

    [Fact]
    public void OneSkillMayContainDamageAndHealingButEveryEffectRequiresItsOwnFact()
    {
        var classifier = Classifier();
        var damage = classifier.Classify(Hit(), Scope);
        var healing = classifier.Classify(Hit(1002), Scope);
        Assert.Equal(CombatAmountKind.Damage, damage.Kind);
        Assert.Equal(CombatAmountKind.Healing, healing.Kind);
        Assert.Equal(123, healing.Amount);
        Assert.True(damage.IsEmpirical);
        Assert.True(healing.IsEmpirical);
        Assert.Equal("synthetic-evidence-v1", damage.EvidenceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("different-build")]
    public void UnconfirmedOrDifferentScopeNeverUsesTheFacts(string? scope)
    {
        var result = Classifier().Classify(Hit(), scope);
        Assert.Equal(CombatAmountKind.Unknown, result.Kind);
        Assert.Equal("evidence-scope-unconfirmed", result.UnknownReason);
        Assert.Null(result.EvidenceId);
    }

    [Fact]
    public void UnknownEffectAndUnobservedPeriodicFormNeverDefaultToDamage()
    {
        var classifier = Classifier();
        Assert.Equal(CombatAmountKind.Unknown, classifier.Classify(Hit(9999), Scope).Kind);
        Assert.Equal(CombatAmountKind.Unknown, classifier.Classify(Hit(periodic: true), Scope).Kind);
        Assert.Equal(CombatAmountKind.Unknown, new CombatAmountClassifier("empty", Scope, []).Classify(Hit(), Scope).Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    public void MissingOrInvalidAmountsCannotBecomeCountedDamage(long? amount) =>
        Assert.Equal(CombatAmountKind.Unknown, Classifier().Classify(Hit(amount: amount), Scope).Kind);

    [Fact]
    public void ObservedZeroIsNotMissingOrADeathProof()
    {
        var result = Classifier().Classify(Hit(amount: 0), Scope);
        Assert.Equal(CombatAmountKind.Damage, result.Kind);
        Assert.Equal(0, result.Amount);
    }

    [Theory]
    [InlineData(2, 2, CombatAmountKind.Damage)]
    [InlineData(3, 2, CombatAmountKind.Damage)]
    [InlineData(3, 1, CombatAmountKind.Healing)]
    [InlineData(3, 4, CombatAmountKind.Damage)]
    [InlineData(3, 2, CombatAmountKind.Unknown)]
    public void FactsRequireExplicitSemanticsAndIndependentCorroboration(int observations, int captures, CombatAmountKind kind) =>
        Assert.Throws<ArgumentException>(() => new CombatAmountClassifier("synthetic", Scope,
            [new(false, 1001, kind, observations, captures)]));

    [Fact]
    public void DuplicateOrConflictingFactsAreRejectedInsteadOfLastWriteWinning() =>
        Assert.Throws<ArgumentException>(() => new CombatAmountClassifier("synthetic", Scope,
            [new(false, 1001, CombatAmountKind.Damage, 4, 2), new(false, 1001, CombatAmountKind.Healing, 5, 2)]));
}
