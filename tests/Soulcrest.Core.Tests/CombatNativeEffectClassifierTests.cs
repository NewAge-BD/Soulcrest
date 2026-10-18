using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CombatNativeEffectClassifierTests
{
    private const string Scope = "synthetic-explicit-native-evidence-scope";
    private const string Evidence = "synthetic-own-native-prefix-facts";

    private static CombatNativeEffectClassifier Classifier() => new(Evidence, Scope,
        [new(false, 1001, "ESkillEffectType::Damage"), new(false, 1002, "ESkillEffectType::HpHeal"),
         new(true, 1001, "EAbnormalEffectType::Hot"), new(true, 1002, "EAbnormalEffectType::Dot_NormalCalc")]);

    private static CombatHitObservation Hit(uint effect = 1001, bool periodic = false, long? amount = 123) =>
        new(900, 301, 2001, effect, amount, periodic, null, null, 4);

    [Theory]
    [InlineData(false, 1001U, CombatAmountKind.Damage)]
    [InlineData(false, 1002U, CombatAmountKind.Healing)]
    [InlineData(true, 1001U, CombatAmountKind.Healing)]
    [InlineData(true, 1002U, CombatAmountKind.Damage)]
    public void NativeFactsKeepDirectAndPeriodicIdSpacesSeparate(bool periodic, uint effect, CombatAmountKind kind)
    {
        ICombatAmountClassifier classifier = Classifier();
        var result = classifier.Classify(Hit(effect, periodic), Scope);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(123, result.Amount);
        Assert.Equal(Evidence, result.EvidenceId);
        Assert.Null(result.UnknownReason);
        Assert.False(result.IsEmpirical);
        Assert.Equal(Evidence, classifier.EvidenceId);
        Assert.Equal(Scope, classifier.EvidenceScope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SYNTHETIC-EXPLICIT-NATIVE-EVIDENCE-SCOPE")]
    [InlineData("synthetic-explicit-native-evidence-scope ")]
    [InlineData("another-evidence-scope")]
    public void ScopeRequiresExactExplicitConfirmation(string? scope)
    {
        var result = Classifier().Classify(Hit(), scope);
        Assert.Equal(CombatAmountKind.Unknown, result.Kind);
        Assert.Equal("evidence-scope-unconfirmed", result.UnknownReason);
        Assert.Null(result.EvidenceId);
        Assert.False(result.IsEmpirical);
    }

    [Theory]
    [InlineData(false, "ESkillEffectType::HpHealRatio")]
    [InlineData(false, "ESkillEffectType::HpHeal_Item")]
    [InlineData(false, "ESkillEffectType::HpHealRatio_Item")]
    [InlineData(false, "ESkillEffectType::MpHeal")]
    [InlineData(false, "ESkillEffectType::SpHeal")]
    [InlineData(false, "ESkillEffectType::SelfDamage")]
    [InlineData(false, "ESkillEffectType::Abnormal_True")]
    [InlineData(false, "ESkillEffectType::Spawn")]
    [InlineData(false, "ESkillEffectType::damage")]
    [InlineData(false, "ESkillEffectType::Future_Type2")]
    [InlineData(true, "EAbnormalEffectType::HpBarrier")]
    [InlineData(true, "EAbnormalEffectType::Dot_TargetMaxHP")]
    [InlineData(true, "EAbnormalEffectType::Hot_Item")]
    [InlineData(true, "EAbnormalEffectType::HotRatio_Item")]
    [InlineData(true, "EAbnormalEffectType::Mot_Ratio")]
    [InlineData(true, "EAbnormalEffectType::Sot_Ratio")]
    public void OtherWellFormedNativeTypesRemainUnknown(bool periodic, string nativeType)
    {
        var classifier = new CombatNativeEffectClassifier(Evidence, Scope, [new(periodic, 1001, nativeType)]);
        var result = classifier.Classify(Hit(periodic: periodic), Scope);
        Assert.Equal(1, classifier.RowCount);
        Assert.Equal(CombatAmountKind.Unknown, result.Kind);
        Assert.Equal("effect-semantics-unconfirmed", result.UnknownReason);
        Assert.Null(result.EvidenceId);
    }

    [Fact]
    public void EmptyCatalogAndUnlistedExactIdRemainUnknown()
    {
        Assert.Equal(CombatAmountKind.Unknown, new CombatNativeEffectClassifier(Evidence, Scope, []).Classify(Hit(), Scope).Kind);
        Assert.Equal(CombatAmountKind.Unknown, Classifier().Classify(Hit(1000), Scope).Kind);
        Assert.Equal(CombatAmountKind.Unknown, Classifier().Classify(Hit(10010), Scope).Kind);
        Assert.Equal(CombatAmountKind.Unknown, Classifier().Classify(Hit(0), Scope).Kind);
    }

    [Theory]
    [InlineData(null, "amount-unobserved")]
    [InlineData(-1L, "invalid-amount")]
    [InlineData(long.MinValue, "invalid-amount")]
    public void MissingAndInvalidPrimaryAmountsRemainUnknown(long? amount, string reason)
    {
        var result = Classifier().Classify(Hit(amount: amount), Scope);
        Assert.Equal(CombatAmountKind.Unknown, result.Kind);
        Assert.Equal(amount, result.Amount);
        Assert.Equal(reason, result.UnknownReason);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void ObservedZeroAndLargestPrimaryAmountRemainExact(long amount)
    {
        var result = Classifier().Classify(Hit(amount: amount), Scope);
        Assert.Equal(CombatAmountKind.Damage, result.Kind);
        Assert.Equal(amount, result.Amount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(123L)]
    [InlineData(long.MaxValue)]
    public void FullUInt64AuxiliaryValuesNeverReplaceOrAddToPrimaryAmount(long? amount)
    {
        var direct = Hit(amount: amount) with
        {
            RawPresenceFlags = 0x0406,
            DirectFields = new(255, ulong.MaxValue, 255, uint.MaxValue, uint.MaxValue, true, ushort.MaxValue,
                ulong.MaxValue, true)
        };
        var periodic = Hit(periodic: true, amount: amount) with
        {
            RawPresenceFlags = 0x003B,
            PeriodicFields = new(555, ulong.MaxValue, uint.MaxValue, 777, uint.MaxValue)
        };
        var classifier = Classifier();
        Assert.Equal(amount is null ? CombatAmountKind.Unknown : CombatAmountKind.Damage, classifier.Classify(direct, Scope).Kind);
        Assert.Equal(amount is null ? CombatAmountKind.Unknown : CombatAmountKind.Healing, classifier.Classify(periodic, Scope).Kind);
        Assert.Equal(amount, classifier.Classify(direct, Scope).Amount);
        Assert.Equal(amount, classifier.Classify(periodic, Scope).Amount);
    }

    [Theory]
    [InlineData(false, "EAbnormalEffectType::Damage")]
    [InlineData(true, "ESkillEffectType::Hot")]
    [InlineData(false, "Damage")]
    [InlineData(false, "ESkillEffectType::")]
    [InlineData(false, "eskilleffecttype::Damage")]
    [InlineData(false, "ESkillEffectType::Damage ")]
    [InlineData(false, " ESkillEffectType::Damage")]
    [InlineData(false, "ESkillEffectType::Dam\u0000age")]
    [InlineData(false, "ESkillEffectType::Damage::Other")]
    [InlineData(false, "ESkillEffectType::1Damage")]
    [InlineData(false, "ESkillEffectType::Dämage")]
    [InlineData(false, "")]
    [InlineData(false, null)]
    public void NativeNamespaceAndMemberSyntaxMustBeValid(bool periodic, string? nativeType) =>
        Assert.Throws<ArgumentException>(() => new CombatNativeEffectClassifier(Evidence, Scope, [new(periodic, 1001, nativeType!)]));

    [Fact]
    public void ExcessivelyLongNativeTypeIsRejected() =>
        Assert.Throws<ArgumentException>(() => new CombatNativeEffectClassifier(Evidence, Scope,
            [new(false, 1001, "ESkillEffectType::" + new string('A', CombatNativeEffectClassifier.MaximumNativeTypeLength))]));

    [Fact]
    public void NullRowZeroIdAndDuplicateOrConflictingKeysAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new CombatNativeEffectClassifier(Evidence, Scope, [null!]));
        Assert.Throws<ArgumentException>(() => new CombatNativeEffectClassifier(Evidence, Scope, [new(false, 0, "ESkillEffectType::Damage")]));
        Assert.Throws<ArgumentException>(() => new CombatNativeEffectClassifier(Evidence, Scope,
            [new(false, 1001, "ESkillEffectType::Damage"), new(false, 1001, "ESkillEffectType::Damage")]));
        Assert.Throws<ArgumentException>(() => new CombatNativeEffectClassifier(Evidence, Scope,
            [new(false, 1001, "ESkillEffectType::Damage"), new(false, 1001, "ESkillEffectType::HpHeal")]));
        Assert.Throws<ArgumentException>(() => new CombatNativeEffectClassifier(Evidence, Scope,
            [new(false, 1001, "ESkillEffectType::MpHeal"), new(false, 1001, "ESkillEffectType::SpHeal")]));
    }

    [Fact]
    public void CatalogBoundIsInclusiveAndStopsOversizedEnumeration()
    {
        var consumed = 0;
        IEnumerable<CombatNativeEffectRow> InfiniteRows()
        {
            for (uint id = 1; ; id++)
            {
                consumed++;
                yield return new(false, id, "ESkillEffectType::Damage");
            }
        }
        var valid = new CombatNativeEffectClassifier(Evidence, Scope, InfiniteRows().Take(CombatNativeEffectClassifier.MaximumRows));
        Assert.Equal(CombatNativeEffectClassifier.MaximumRows, consumed);
        Assert.Equal(CombatNativeEffectClassifier.MaximumRows, valid.RowCount);
        Assert.Equal(CombatAmountKind.Damage, valid.Classify(Hit((uint)CombatNativeEffectClassifier.MaximumRows), Scope).Kind);
        consumed = 0;
        Assert.Throws<ArgumentException>(() => new CombatNativeEffectClassifier(Evidence, Scope, InfiniteRows()));
        Assert.Equal(CombatNativeEffectClassifier.MaximumRows + 1, consumed);
    }

    [Fact]
    public void ExplicitMetadataAndObservationsAreRequired()
    {
        Assert.ThrowsAny<ArgumentException>(() => new CombatNativeEffectClassifier(null!, Scope, []));
        Assert.ThrowsAny<ArgumentException>(() => new CombatNativeEffectClassifier(" ", Scope, []));
        Assert.ThrowsAny<ArgumentException>(() => new CombatNativeEffectClassifier(Evidence, null!, []));
        Assert.ThrowsAny<ArgumentException>(() => new CombatNativeEffectClassifier(Evidence, " ", []));
        Assert.Throws<ArgumentNullException>(() => new CombatNativeEffectClassifier(Evidence, Scope, null!));
        Assert.Throws<ArgumentNullException>(() => Classifier().Classify(null!, Scope));
    }

    [Fact]
    public void ExistingEmpiricalClassifierRetainsItsEvidenceContractThroughInterface()
    {
        ICombatAmountClassifier classifier = new CombatAmountClassifier(Evidence, Scope,
            [new(false, 1001, CombatAmountKind.Damage, 4, 2)]);
        var result = classifier.Classify(Hit(), Scope);
        Assert.Equal(CombatAmountKind.Damage, result.Kind);
        Assert.True(result.IsEmpirical);
        Assert.Equal(Evidence, classifier.EvidenceId);
        Assert.Equal(Scope, classifier.EvidenceScope);
    }
}
