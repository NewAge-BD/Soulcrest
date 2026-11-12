using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CharacterDisplayStatProjectionTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QualifiedMainVectorMatchesAllEightPanelValuesWithoutTruncatingWeaponAverage()
    {
        var projected = CharacterDisplayStatProjection.Project(Input(MainVector()));
        foreach (var (id, expected) in new[] { ("HPMax", 25543), ("MPMax", 4945), ("Attack", 3035),
            ("Defense", 17012), ("Accuracy", 749), ("Evasion", 251), ("Critical", 916), ("CriticalResist", 630) })
        {
            var row = Row(projected, id);
            Assert.Equal(expected, row.Value); Assert.Equal(CombatKnowledgeStatus.Complete, row.Status);
            Assert.Equal(At, row.ObservedAt); Assert.Equal("own-full-stat-list", row.Source);
            Assert.Empty(row.MissingInputIds);
        }
        Assert.Equal(3035.1375m, Row(projected, "Attack").UnroundedValue);
        Assert.Equal(550.5m, Row(projected, "Attack").Inputs.Where(term => term.NativeId is 31 or 33)
            .Sum(term => term.RawValue * term.Coefficient));
        Assert.Equal(CharacterDisplayStatQualification.ControlledChange, Row(projected, "HPMax").Qualification);
        Assert.Equal(CharacterDisplayStatQualification.ObservedMatch, Row(projected, "Attack").Qualification);
        Assert.Equal(26, projected.Rows.Count(row => row.Category == CharacterDisplayStatCategory.Main));
    }

    [Fact]
    public void ExplicitPrimaryAttributesKeepTheirSemanticIdentityWithoutInventingMissingIntelligence()
    {
        var values = new Dictionary<ushort, int> { [1] = 83, [2] = 49, [4] = 44, [5] = 41, [6] = 9 };
        var input = Input(values) with { ClassId = "Assassin" };
        var result = CharacterDisplayStatProjection.Project(input);
        foreach (var (id, value, nativeId) in new[] { ("Might", 83, 1), ("Dexterity", 49, 2),
            ("Constitution", 44, 4), ("Precision", 41, 5), ("Willpower", 9, 6) })
        {
            var row = Row(result, id);
            Assert.Equal(value, row.Value); Assert.Equal(CharacterDisplayStatCategory.Main, row.Category);
            Assert.Equal(CharacterDisplayStatQualification.ObservedMatch, row.Qualification);
            Assert.Equal(nativeId, Assert.Single(row.Inputs).NativeId);
        }
        var unknown = Row(result, "Intelligence");
        Assert.Null(unknown.Value); Assert.Equal(CombatKnowledgeStatus.Unknown, unknown.Status);
        Assert.Equal(CharacterDisplayStatQualification.Unknown, unknown.Qualification);
        Assert.Equal("UnqualifiedMapping", unknown.Reason); Assert.Equal(new ushort[] { 3 }, unknown.MissingInputIds);
        values[3] = 0; // A previously absent wire key alone cannot retrospectively qualify its mapping.
        Assert.Null(Row(CharacterDisplayStatProjection.Project(input), "Intelligence").Value);
        values[3] = 17;
        Assert.Null(Row(CharacterDisplayStatProjection.Project(input), "Intelligence").Value);
        values.Remove(1);
        Assert.Null(Row(CharacterDisplayStatProjection.Project(input), "Might").Value);
        Assert.Null(CombatEffectCatalog.Default.NormalizeNative(1, 83));
    }

    [Theory]
    [MemberData(nameof(NamedGodPanelVectors))]
    public void NamedGodPanelValuesUseTheirExplicitIdentityAndRemainObservedDisplayMatches(string id, ushort nativeId, int raw)
    {
        var row = Row(CharacterDisplayStatProjection.Project(Input(new Dictionary<ushort, int> { [nativeId] = raw })), id);
        Assert.Equal(raw, row.Value); Assert.Equal(raw, row.UnroundedValue);
        Assert.Equal(CombatKnowledgeStatus.Complete, row.Status);
        Assert.Equal(CharacterDisplayStatCategory.Main, row.Category); Assert.Equal(CombatStatUnit.Points, row.Unit);
        Assert.Equal(CharacterDisplayStatQualification.ObservedMatch, row.Qualification);
        var input = Assert.Single(row.Inputs); Assert.Equal(nativeId, input.NativeId); Assert.Equal(raw, input.RawValue);
        Assert.Equal(1m, input.Coefficient); Assert.Equal($"raw[{nativeId}]", row.Formula);
        Assert.Equal(At, row.ObservedAt); Assert.Equal("own-full-stat-list", row.Source);
        Assert.Contains("explicit-named-god-panel-vector-2026-11-09", row.Evidence);
        Assert.Contains("historical-estat-numeric-enum-crosscheck-2026-11-09", row.Evidence);
        Assert.Contains("current-god-semantic-localization-crosscheck-2026-11-09", row.Evidence);
        Assert.Contains("reference-contract-build-scope-only", row.Evidence);
        Assert.Null(CombatEffectCatalog.Default.NormalizeNative(nativeId, raw));
    }

    [Fact]
    public void GodScopeIsExactlyTenNamedRulesAndDoesNotPromoteLightDarkOrACombatCatalogMapping()
    {
        var gods = CharacterDisplayStatProjection.Rules.Where(rule => rule.Id.Contains("-god-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(10, gods.Length);
        Assert.Equal(new ushort[] { 7, 8, 9, 10, 11, 13, 14, 15, 16, 17 }, gods.Select(rule => Assert.Single(rule.FlatInputs).NativeId));
        Assert.Equal(new[] { "Justice", "Freedom", "Illusion", "Life", "Time", "Destruction", "Death", "Wisdom", "Destiny", "Space" },
            gods.Select(rule => rule.OutputId));
        Assert.All(gods, rule =>
        {
            Assert.Equal($"character-display-god-{rule.OutputId.ToLowerInvariant()}-observed-2026-11-09-v1", rule.Id);
            Assert.Equal(rule.OutputId, rule.LabelStatId); Assert.Equal("Cleric", rule.RequiredClassId);
            Assert.Equal(26262663, rule.ClientBuild); Assert.Equal(1m, rule.Divisor); Assert.Equal(CombatRounding.None, rule.Rounding);
            Assert.False(rule.AllowNegative); Assert.Null(rule.IncreaseBasisPointsInput);
        });
        var result = CharacterDisplayStatProjection.Project(Input(new Dictionary<ushort, int> { [12] = 50, [18] = 60, [3] = 99 }));
        Assert.DoesNotContain(result.Rows, row => row.Id is "Light" or "Dark");
        Assert.DoesNotContain(gods.SelectMany(rule => rule.FlatInputs), term => term.NativeId is 12 or 18);
        Assert.Null(Row(result, "Intelligence").Value); Assert.Empty(CombatEffectCatalog.Default.NativeForwardRules);
        Assert.Equal("character-display-26262663-v4-roll-tables20261112", result.Version);
    }

    [Fact]
    public void EqualObservedGodValuesNeverChooseTheIdentityAndSwappingSyntheticValuesKeepsTheBinding()
    {
        var values = new Dictionary<ushort, int> { [11] = 76, [13] = 76, [14] = 38, [17] = 38 };
        var initial = CharacterDisplayStatProjection.Project(Input(values));
        Assert.Equal(76, Row(initial, "Time").Value); Assert.Equal(76, Row(initial, "Destruction").Value);
        Assert.Equal(38, Row(initial, "Death").Value); Assert.Equal(38, Row(initial, "Space").Value);
        foreach (var vector in new[] { new[] { 1, 2, 3, 4 }, new[] { 2, 1, 4, 3 } })
        {
            values[11] = vector[0]; values[13] = vector[1]; values[14] = vector[2]; values[17] = vector[3];
            var changed = CharacterDisplayStatProjection.Project(Input(values));
            Assert.Equal(vector[0], Row(changed, "Time").Value); Assert.Equal(vector[1], Row(changed, "Destruction").Value);
            Assert.Equal(vector[2], Row(changed, "Death").Value); Assert.Equal(vector[3], Row(changed, "Space").Value);
        }
    }

    [Fact]
    public void PartialGodListsQualifyOnlyTheirExplicitKeysIncludingZeroAndRejectNegativePoints()
    {
        foreach (var vector in NamedGodPanelVectors)
        {
            var id = (string)vector[0]; var nativeId = (ushort)vector[1];
            var values = new Dictionary<ushort, int>();
            var input = Input(values);
            input = input with { NativeStats = input.NativeStats! with { Status = CombatKnowledgeStatus.Partial, Source = "own-stat-update" } };
            var missing = Row(CharacterDisplayStatProjection.Project(input), id);
            Assert.Null(missing.Value); Assert.Equal("MissingInputs", missing.Reason);
            Assert.Equal(new[] { nativeId }, missing.MissingInputIds);
            values[nativeId] = 0;
            var zero = Row(CharacterDisplayStatProjection.Project(input), id);
            Assert.Equal(0, zero.Value); Assert.Equal(CombatKnowledgeStatus.Complete, zero.Status);
            Assert.Equal(CharacterDisplayStatQualification.ObservedMatch, zero.Qualification);
            values[nativeId] = -1;
            var negative = Row(CharacterDisplayStatProjection.Project(input), id);
            Assert.Null(negative.Value); Assert.Equal(CombatKnowledgeStatus.Conflict, negative.Status);
            Assert.Equal("InvalidComponentValue", negative.Reason);
        }
    }

    [Theory]
    [InlineData("stale", CombatKnowledgeStatus.Stale, "StaleSource")]
    [InlineData("generation", CombatKnowledgeStatus.Stale, "GenerationMismatch")]
    [InlineData("unknown-generation", CombatKnowledgeStatus.Unknown, "MissingGeneration")]
    [InlineData("source", CombatKnowledgeStatus.Unknown, "UnqualifiedSource")]
    [InlineData("class", CombatKnowledgeStatus.Unknown, "UnqualifiedClass")]
    [InlineData("missing-class", CombatKnowledgeStatus.Unknown, "UnqualifiedClass")]
    public void GodMatchesKeepTheirOwnSourceGenerationAndClassBoundaries(string variant, CombatKnowledgeStatus status, string reason)
    {
        var values = NamedGodPanelVectors.ToDictionary(row => (ushort)row[1], row => (int)row[2]);
        var input = Input(values);
        input = variant switch
        {
            "stale" => input with { NativeStats = input.NativeStats! with { IsFresh = false } },
            "generation" => input with { NativeStats = input.NativeStats! with { Generation = 6 } },
            "unknown-generation" => input with { Generation = 0 },
            "source" => input with { NativeStats = input.NativeStats! with { Source = "unqualified-source" } },
            "class" => input with { ClassId = "Assassin" },
            "missing-class" => input with { ClassId = null },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var result = CharacterDisplayStatProjection.Project(input);
        foreach (var vector in NamedGodPanelVectors)
        {
            var row = Row(result, (string)vector[0]);
            Assert.Null(row.Value); Assert.Equal(status, row.Status); Assert.Equal(reason, row.Reason);
        }
        if (variant == "class")
        {
            values[1] = 83;
            Assert.Equal(83, Row(CharacterDisplayStatProjection.Project(input), "Might").Value);
        }
    }

    [Theory]
    [InlineData(26425393)]
    [InlineData(26429009)]
    public void MetadataAndInstalledBuildNumbersDoNotReplaceTheExplicitProjectionReferenceBuild(int otherBuild)
    {
        Assert.Equal(26262663, CharacterDisplayStatProjection.QualifiedClientBuild);
        var result = CharacterDisplayStatProjection.Project(Input(new Dictionary<ushort, int> { [7] = 51 }) with { ClientBuild = otherBuild });
        var row = Row(result, "Justice"); Assert.Null(row.Value); Assert.Equal("UnqualifiedBuild", row.Reason);
        Assert.Equal(CharacterDisplayStatQualification.Unknown, row.Qualification);
    }

    public static IEnumerable<object[]> NamedGodPanelVectors =>
    [
        new object[] { "Justice", (ushort)7, 51 }, new object[] { "Freedom", (ushort)8, 63 },
        new object[] { "Illusion", (ushort)9, 50 }, new object[] { "Life", (ushort)10, 52 },
        new object[] { "Time", (ushort)11, 76 }, new object[] { "Destruction", (ushort)13, 76 },
        new object[] { "Death", (ushort)14, 38 }, new object[] { "Wisdom", (ushort)15, 31 },
        new object[] { "Destiny", (ushort)16, 25 }, new object[] { "Space", (ushort)17, 38 }
    ];

    [Theory]
    [InlineData("HPMax", 193, 100, 25409)]
    [InlineData("MPMax", 199, 50, 4876)]
    [InlineData("Defense", 52, 30, 16971)]
    [InlineData("Critical", 128, 5, 910)]
    public void ControlledComponentReturnMatchesBeforeRefundAndRestoredStates(string id, int nativeId, int amount, int expected)
    {
        var values = MainVector(); var before = CharacterDisplayStatProjection.Project(Input(values));
        values[(ushort)nativeId] -= amount;
        var refunded = CharacterDisplayStatProjection.Project(Input(values));
        Assert.Equal(expected, Row(refunded, id).Value);
        values[(ushort)nativeId] += amount;
        var restored = CharacterDisplayStatProjection.Project(Input(values));
        Assert.Equal(Row(before, id).Value, Row(restored, id).Value);
        Assert.Equal(Row(before, id).Value - Row(refunded, id).Value, Row(restored, id).Value - Row(refunded, id).Value);
    }

    [Fact]
    public void MissingRatioIsNeverDefaultedToZeroAndPartialListsCanProveIndividualRows()
    {
        var values = new Dictionary<ushort, int> { [193] = 100 };
        var input = Input(values) with { NativeStats = Input(values).NativeStats! with { Status = CombatKnowledgeStatus.Partial } };
        var missing = Row(CharacterDisplayStatProjection.Project(input), "HPMax");
        Assert.Null(missing.Value); Assert.Equal(CombatKnowledgeStatus.Partial, missing.Status);
        Assert.Equal(new ushort[] { 196 }, missing.MissingInputIds);
        values[196] = 0;
        var known = Row(CharacterDisplayStatProjection.Project(input), "HPMax");
        Assert.Equal(100, known.Value); Assert.Equal(CombatKnowledgeStatus.Complete, known.Status);
        values[193] = 0;
        Assert.Equal(0, Row(CharacterDisplayStatProjection.Project(input), "HPMax").Value);
        Assert.Null(Row(CharacterDisplayStatProjection.Project(input), "MPMax").Value);
    }

    [Theory]
    [InlineData(CombatKnowledgeStatus.Complete, false, 7, CombatKnowledgeStatus.Stale, "StaleSource")]
    [InlineData(CombatKnowledgeStatus.Stale, true, 7, CombatKnowledgeStatus.Stale, "StaleSource")]
    [InlineData(CombatKnowledgeStatus.Complete, true, 6, CombatKnowledgeStatus.Stale, "GenerationMismatch")]
    [InlineData(CombatKnowledgeStatus.Conflict, true, 7, CombatKnowledgeStatus.Conflict, "ConflictingSource")]
    [InlineData(CombatKnowledgeStatus.Unknown, false, 7, CombatKnowledgeStatus.Unknown, "MissingSource")]
    public void SourceFreshnessGenerationAndConflictGateEveryDependency(CombatKnowledgeStatus sourceStatus,
        bool fresh, long generation, CombatKnowledgeStatus expected, string reason)
    {
        var input = Input(MainVector());
        input = input with { NativeStats = input.NativeStats! with { Status = sourceStatus, IsFresh = fresh, Generation = generation } };
        var row = Row(CharacterDisplayStatProjection.Project(input), "HPMax");
        Assert.Null(row.Value); Assert.Equal(expected, row.Status); Assert.Equal(reason, row.Reason);
    }

    [Fact]
    public void BuildClassTimestampAndSourceCodeAreIndependentGates()
    {
        var input = Input(MainVector());
        var build = CharacterDisplayStatProjection.Project(input with { ClientBuild = 26262664 });
        Assert.All(build.Rows, row => { Assert.Null(row.Value); Assert.Equal("UnqualifiedBuild", row.Reason); });
        var otherClass = CharacterDisplayStatProjection.Project(input with { ClassId = "Gladiator" });
        Assert.Equal("UnqualifiedClass", Row(otherClass, "Attack").Reason); Assert.Null(Row(otherClass, "Attack").Value);
        var missingAt = CharacterDisplayStatProjection.Project(input with { NativeStats = input.NativeStats! with { ObservedAt = null } });
        Assert.Equal("MissingTimestamp", Row(missingAt, "HPMax").Reason);
        var unknownSource = CharacterDisplayStatProjection.Project(input with { NativeStats = input.NativeStats! with { Source = "manual-values" } });
        Assert.Equal("UnqualifiedSource", Row(unknownSource, "HPMax").Reason);
        Assert.Null(Row(unknownSource, "HPMax").Value);
    }

    [Fact]
    public void DirectResourceMaximaAreClassIndependentAuthoritativeAndKeepSeparateFactTimes()
    {
        var input = Input(MainVector()) with
        {
            ClassId = "Assassin", HpMaximum = Maximum(27449, At, CharacterDisplayVitalSourceKind.Own),
            MpMaximum = Maximum(4945, At.AddSeconds(12), CharacterDisplayVitalSourceKind.StateUpdate8D00)
        };
        var result = CharacterDisplayStatProjection.Project(input);
        var hp = Row(result, "HPMax"); var mp = Row(result, "MPMax");
        Assert.Equal(27449, hp.Value); Assert.Equal(4945, mp.Value);
        Assert.Equal(At, hp.ObservedAt); Assert.Equal(At.AddSeconds(12), mp.ObservedAt);
        Assert.Equal("own-entry-resource-maxima", hp.Source); Assert.Equal("own-resource-maxima-update", mp.Source);
        Assert.Equal(CharacterDisplayStatQualification.NativeDirect, hp.Qualification);
        Assert.Equal(CombatKnowledgeStatus.Complete, hp.Status); Assert.Null(hp.ForwardValue);
        Assert.Equal("UnqualifiedClass", hp.ForwardReason);
        Assert.Null(Row(result, "Attack").Value);
    }

    [Fact]
    public void DirectNativeMismatchIsVisibleWithoutReplacingItsAuthoritativeValue()
    {
        var input = Input(MainVector()) with { HpMaximum = Maximum(27449, At, CharacterDisplayVitalSourceKind.Own) };
        var hp = Row(CharacterDisplayStatProjection.Project(input), "HPMax");
        Assert.Equal(27449, hp.Value); Assert.Equal(25543, hp.ForwardValue);
        Assert.Equal(CombatKnowledgeStatus.Conflict, hp.ComparisonStatus); Assert.Equal("NativeForwardMismatch", hp.Reason);
        Assert.Equal(CombatKnowledgeStatus.Complete, hp.Status);
        var agreement = Row(CharacterDisplayStatProjection.Project(input with { HpMaximum = input.HpMaximum! with { Value = 25543 } }), "HPMax");
        Assert.Equal(CombatKnowledgeStatus.Complete, agreement.ComparisonStatus); Assert.Null(agreement.Reason);
    }

    [Fact]
    public void UnqualifiedOrStaleMaximumNeverAcquiresCurrentAuthorityAndNativeConflictStaysVisible()
    {
        var input = Input(new Dictionary<ushort, int>()) with { HpMaximum = Maximum(100, At, CharacterDisplayVitalSourceKind.Own) };
        var stale = Row(CharacterDisplayStatProjection.Project(input with { HpMaximum = input.HpMaximum! with { Generation = 6 } }), "HPMax");
        Assert.Null(stale.Value); Assert.Equal(CombatKnowledgeStatus.Stale, stale.Status);
        var wrongPair = Row(CharacterDisplayStatProjection.Project(input with { HpMaximum = input.HpMaximum! with { Kind = CharacterDisplayVitalSourceKind.StateUpdate8D00 } }), "HPMax");
        Assert.Null(wrongPair.Value); Assert.NotEqual(CharacterDisplayStatQualification.NativeDirect, wrongPair.Qualification);
        var invalid = Row(CharacterDisplayStatProjection.Project(input with { HpMaximum = input.HpMaximum! with { Value = -1 } }), "HPMax");
        Assert.Null(invalid.Value); Assert.Equal(CombatKnowledgeStatus.Conflict, invalid.Status);
        var future = CharacterDisplayStatProjection.Project(input with { ClientBuild = 26262664 });
        Assert.Null(Row(future, "HPMax").Value);
    }

    [Fact]
    public void SignedPercentagesAndObservedTieRoundingKeepExactUnroundedValues()
    {
        var values = new Dictionary<ushort, int> { [386] = -310, [385] = -500, [215] = 1000,
            [379] = 2285, [235] = 1575, [218] = 9666, [28] = 2639, [205] = 630, [282] = 2203 };
        var result = CharacterDisplayStatProjection.Project(Input(values) with { ClassId = "Assassin" });
        Assert.Equal(-3.1m, Row(result, "MPUseIncrease").Value); Assert.Equal(-15m, Row(result, "Cooldown").Value);
        Assert.Equal(22.8m, Row(result, "PvEAmplifyDamage").Value); Assert.Equal(22.85m, Row(result, "PvEAmplifyDamage").UnroundedValue);
        Assert.Equal(15.8m, Row(result, "AmplifyHpHealGet").Value); Assert.Equal(96.7m, Row(result, "HpHealRate").Value);
        Assert.Equal(26.4m, Row(result, "AmplifyAllDamage").Value); Assert.Equal(26.39m, Row(result, "AmplifyAllDamage").UnroundedValue);
        Assert.Equal(6.3m, Row(result, "MoveSpeed").Value); Assert.Equal(22m, Row(result, "CombatSpeed").Value);
        Assert.Equal(CharacterDisplayStatCategory.Main, Row(result, "CombatSpeed").Category);
        Assert.Equal(CharacterDisplayStatQualification.ObservedMatch, Row(result, "MPUseIncrease").Qualification);
    }

    [Fact]
    public void CompoundRegenerationRequiresEveryExplicitAddendAndScaledResourcesAreNotPercentages()
    {
        var values = new Dictionary<ushort, int> { [194] = 185, [275] = 35, [195] = 60, [200] = 118,
            [276] = 35, [201] = 255, [255] = 468, [432] = 870, [242] = 167600, [298] = 815200, [277] = 5000 };
        var result = CharacterDisplayStatProjection.Project(Input(values));
        Assert.Equal(220, Row(result, "CombatHPRegen").Value); Assert.Equal(245, Row(result, "RestHPRegen").Value);
        Assert.Equal(153, Row(result, "CombatMPRegen").Value); Assert.Equal(373, Row(result, "RestMPRegen").Value);
        Assert.Equal(508, Row(result, "Block").Value);
        Assert.Equal(1676, Row(result, "SPMax").Value); Assert.Equal(8152, Row(result, "FPMax").Value);
        Assert.Equal(CombatStatUnit.Points, Row(result, "SPMax").Unit); Assert.Equal(50, Row(result, "BattleSPRegen").Value);
        values.Remove(275);
        Assert.Null(Row(CharacterDisplayStatProjection.Project(Input(values)), "CombatHPRegen").Value);
        Assert.Equal(new ushort[] { 275 }, Row(CharacterDisplayStatProjection.Project(Input(values)), "CombatHPRegen").MissingInputIds);
    }

    [Fact]
    public void UnqualifiedGodPrimaryAndUiZerosNeverCreateMappingsOrChangeTheScoreCatalog()
    {
        var result = CharacterDisplayStatProjection.Project(Input(new Dictionary<ushort, int> { [564] = 320, [569] = 25 }));
        Assert.All(result.Rows, row => Assert.Null(row.Value));
        Assert.DoesNotContain(CharacterDisplayStatProjection.Rules.SelectMany(rule => rule.FlatInputs), term => term.NativeId is 564 or 569);
        Assert.Empty(CombatEffectCatalog.Default.NativeForwardRules);
        Assert.Null(CombatEffectCatalog.Default.NormalizeNative(193, 19059));
    }

    [Fact]
    public void SnapshotsAreImmutableAndCorruptGermanLabelsFallBackToReadableEnglish()
    {
        var values = MainVector(); var result = CharacterDisplayStatProjection.Evaluate(Input(values));
        values[193] = 1;
        Assert.Equal(25543, Row(result, "HPMax").Value);
        Assert.Equal(19059, Row(result, "HPMax").Inputs.Single(input => input.NativeId == 193).RawValue);
        Assert.Throws<NotSupportedException>(() => ((IList<CharacterDisplayStatRow>)result.Rows).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CharacterDisplayStatDependency>)Row(result, "HPMax").Inputs).Clear());
        Assert.All(result.Rows, row => { Assert.DoesNotContain('\uFFFD', row.LabelDe); Assert.DoesNotContain('\uFFFD', row.LabelEn); });
        Assert.Equal(CharacterDisplayStatProjection.Version, result.Version);
        Assert.Equal(131, result.Rows.Count); Assert.Equal(131, result.Rows.Select(row => row.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("AmplifyBackAttack", (ushort)102, 160, "1.6")]
    [InlineData("AdditionalHitResistRate", (ushort)147, 140, "1.4")]
    public void ControlledTitleDisplayEffectsPreserveExplicitReturnToZeroAndUnknownAbsence(
        string id, ushort nativeId, int titleRaw, string expectedPercent)
    {
        var values = new Dictionary<ushort, int>();
        var missing = Row(CharacterDisplayStatProjection.Project(Input(values)), id);
        Assert.Null(missing.Value);
        Assert.Equal("MissingInputs", missing.Reason);
        Assert.Equal(new[] { nativeId }, missing.MissingInputIds);

        values[nativeId] = titleRaw;
        var equipped = Row(CharacterDisplayStatProjection.Project(Input(values)), id);
        Assert.Equal(decimal.Parse(expectedPercent, System.Globalization.CultureInfo.InvariantCulture), equipped.Value);
        Assert.Equal(CombatStatUnit.Percent, equipped.Unit);
        Assert.Equal(CharacterDisplayStatQualification.ControlledChange, equipped.Qualification);
        Assert.Equal(CombatKnowledgeStatus.Complete, equipped.Status);
        Assert.DoesNotContain("current-native-component-panel-vector-2026-11-03", equipped.Evidence);
        Assert.Contains("independent-source-metadata-crosscheck-2026-11-09", equipped.Evidence);
        Assert.DoesNotContain(equipped.Evidence, entry => entry.Contains("26429009") || entry.Contains("26425393"));

        values[nativeId] = 0;
        var removed = Row(CharacterDisplayStatProjection.Project(Input(values)), id);
        Assert.Equal(0, removed.Value);
        Assert.Equal(CombatKnowledgeStatus.Complete, removed.Status);
        values.Clear();
        Assert.Null(Row(CharacterDisplayStatProjection.Project(Input(values)), id).Value);

        values[nativeId] = titleRaw;
        var wrongClass = Row(CharacterDisplayStatProjection.Project(Input(values) with { ClassId = "Assassin" }), id);
        Assert.Null(wrongClass.Value); Assert.Equal("UnqualifiedClass", wrongClass.Reason);
        var staleInput = Input(values);
        var stale = Row(CharacterDisplayStatProjection.Project(staleInput with
        {
            NativeStats = staleInput.NativeStats! with { IsFresh = false }
        }), id);
        Assert.Null(stale.Value); Assert.Equal(CombatKnowledgeStatus.Stale, stale.Status);
        Assert.Null(CombatEffectCatalog.Default.NormalizeNative(nativeId, titleRaw));
    }

    [Theory]
    [MemberData(nameof(IndependentObservedSubstatVectors))]
    public void ExplicitSemanticSubstatMatchesItsIndependentPanelVector(string id, ushort nativeId, int raw, decimal displayed)
    {
        var row = Row(CharacterDisplayStatProjection.Project(Input(new Dictionary<ushort, int> { [nativeId] = raw })), id);
        Assert.Equal(displayed, row.Value);
        Assert.Equal(CombatKnowledgeStatus.Complete, row.Status);
        Assert.Equal(CharacterDisplayStatQualification.ObservedMatch, row.Qualification);
        Assert.Single(row.Inputs);
    }

    public static IEnumerable<object[]> IndependentObservedSubstatVectors =>
    [
        new object[] { "DefensePierce", (ushort)284, 1400, 1400m },
        new object[] { "SealStoneAddDamage", (ushort)69, 113, 113m },
        new object[] { "CriticalAddDamage", (ushort)38, 23, 23m },
        new object[] { "CriticalDamageDefense", (ushort)41, 708, 708m },
        new object[] { "BackAttackDamage", (ushort)98, 23, 23m },
        new object[] { "BackAttackDefense", (ushort)99, 276, 276m },
        new object[] { "AmplifyAllDamage", (ushort)28, 2639, 26.4m },
        new object[] { "DecreaseDamage", (ushort)70, 834, 8.3m },
        new object[] { "AmplifyWeaponDamage", (ushort)32, 2644, 26.4m },
        new object[] { "AmplifyCriticalDamage", (ushort)44, 3302, 33.0m },
        new object[] { "DecreaseCriticalDamage", (ushort)47, 1050, 10.5m },
        new object[] { "DecreaseBackAttack", (ushort)103, 300, 3.0m },
        new object[] { "AdditionalHitRate", (ushort)146, 2294, 22.9m },
        new object[] { "BackAttackCritical", (ushort)100, 115, 115m },
        new object[] { "BackAttackCriticalResist", (ushort)101, 40, 40m },
        new object[] { "BlockPierce", (ushort)256, 10, 10m },
        new object[] { "IgnoreRestoration", (ushort)448, 380, 3.8m },
        new object[] { "Restoration", (ushort)444, 1287, 12.9m },
        new object[] { "Perfect", (ushort)442, 1010, 10.1m },
        new object[] { "PerfectResist", (ushort)446, 760, 7.6m },
        new object[] { "HardHit", (ushort)443, 900, 9.0m },
        new object[] { "HardHitResist", (ushort)447, 750, 7.5m },
        new object[] { "IgnoreIronWall", (ushort)449, 500, 5.0m },
        new object[] { "IronWall", (ushort)445, 539, 5.4m },
        new object[] { "PvPAddDamage", (ushort)58, 28, 28m },
        new object[] { "PvPDamageDefense", (ushort)61, 400, 400m },
        new object[] { "PvPAmplifyDamage", (ushort)65, 320, 3.2m },
        new object[] { "PvPDecreaseDamage", (ushort)76, 770, 7.7m },
        new object[] { "PvPAccuracy", (ushort)113, 65, 65m },
        new object[] { "PvPEvasion", (ushort)125, 60, 60m },
        new object[] { "PvPCritical", (ushort)132, 25, 25m },
        new object[] { "PvPCriticalResist", (ushort)136, 20, 20m },
        new object[] { "PvEAddDamage", (ushort)56, 383, 383m },
        new object[] { "PvEDamageDefense", (ushort)57, 480, 480m },
        new object[] { "PvEAccuracy", (ushort)110, 333, 333m },
        new object[] { "PvEEvasion", (ushort)122, 50, 50m },
        new object[] { "PvEAmplifyDamage", (ushort)379, 2285, 22.8m },
        new object[] { "PvEDecreaseDamage", (ushort)380, 450, 4.5m },
        new object[] { "BossNpcAddDamage", (ushort)50, 102, 102m },
        new object[] { "BossNpcDefense", (ushort)51, 690, 690m },
        new object[] { "BossNpcAmplifyDamage", (ushort)520, 400, 4.0m },
        new object[] { "BossNpcDecreaseDamage", (ushort)521, 400, 4.0m },
        new object[] { "IntellectDefense", (ushort)394, 38, 38m },
        new object[] { "IntellectAccuracy", (ushort)395, 12, 12m },
        new object[] { "IntellectEvasion", (ushort)396, 7, 7m },
        new object[] { "FeralDefense", (ushort)402, 40, 40m },
        new object[] { "FeralEvasion", (ushort)404, 10, 10m },
        new object[] { "FeralCriticalResist", (ushort)406, 5, 5m },
        new object[] { "NatureDefense", (ushort)410, 38, 38m },
        new object[] { "NatureCriticalResist", (ushort)414, 9, 9m },
        new object[] { "NatureBlock", (ushort)416, 27, 27m },
        new object[] { "TransCriticalResist", (ushort)422, 5, 5m },
        new object[] { "TransBlock", (ushort)424, 33, 33m },
        new object[] { "FlySpeed", (ushort)214, 50, 0.5m },
        new object[] { "GroundVehicleSpeed", (ushort)208, 456, 456m },
        new object[] { "VehicleSprintCost", (ushort)210, 1170, 11.7m },
        new object[] { "HpHealRate", (ushort)218, 9666, 96.7m },
        new object[] { "AmplifyHpHealGet", (ushort)235, 1575, 15.8m },
        new object[] { "HpPotionRegen", (ushort)221, 10, 10m },
        new object[] { "HpPotionRate", (ushort)220, 4050, 40.5m },
        new object[] { "MPUseIncrease", (ushort)386, -310, -3.1m },
        new object[] { "SPMax", (ushort)242, 167600, 1676m },
        new object[] { "FPMax", (ushort)298, 815200, 8152m },
        new object[] { "BattleSPRegen", (ushort)277, 5000, 50m },
        new object[] { "RestSPRegen", (ushort)244, 22000, 220m },
        new object[] { "BattleFPRegen", (ushort)300, 2000, 20m },
        new object[] { "RestFPRegen", (ushort)301, 4000, 40m },
        new object[] { "MoveSpeed", (ushort)205, 630, 6.3m },
        new object[] { "CombatSpeed", (ushort)282, 2203, 22.0m },
    ];

    private static CharacterDisplayStatRow Row(CharacterDisplayStats result, string id) => Assert.Single(result.Rows, row => row.Id == id);
    private static CharacterDisplayStatsInput Input(IReadOnlyDictionary<ushort, int> values) => new()
    {
        ClientBuild = 26262663, ClassId = "Cleric", Generation = 7,
        NativeStats = new(values, CombatKnowledgeStatus.Complete, true, 7, At, "own-full-stat-list")
    };
    private static CharacterDisplayMaximumFact Maximum(long value, DateTimeOffset at, CharacterDisplayVitalSourceKind kind) =>
        new(value, CombatKnowledgeStatus.Complete, true, 7, at,
            kind == CharacterDisplayVitalSourceKind.Own ? "own-entry-resource-maxima" : "own-resource-maxima-update", kind);
    private static Dictionary<ushort, int> MainVector() => new()
    {
        [193] = 19059, [196] = 3402, [199] = 3596, [197] = 3750,
        [317] = 1660, [31] = 625, [33] = 476, [19] = 377, [425] = 1730,
        [52] = 2380, [307] = 10029, [426] = 3710, [104] = 503, [318] = 176,
        [427] = 1040, [116] = 214, [312] = 12, [428] = 1120, [128] = 849,
        [429] = 790, [133] = 601, [430] = 490
    };
}
