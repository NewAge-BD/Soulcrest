using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.GenusInsight;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GenusInsightMappingCoverageTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 9, 9, 51, TimeSpan.Zero);

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AnExplicitZeroEffectIsNotAnalyzedAndNeverCreatesValuesRangesOrBonuses(bool locked, bool activated)
    {
        var snapshot = GenusInsightProjection.Create(Snapshot(2, 1, new CharacterGenusEffect(0, 0, 0, 0, locked, activated)));
        var slot = snapshot.Boards.Single(board => board.Genus == "cogni").Slots[0];
        Assert.Equal("Nicht analysiert", slot.LabelDe);
        Assert.Equal("Not analyzed", slot.LabelEn);
        Assert.Null(slot.StatId);
        Assert.Null(slot.Value);
        Assert.Null(slot.Unit);
        Assert.Null(slot.Minimum);
        Assert.Null(slot.Maximum);
        Assert.Null(slot.RollFraction);
        Assert.Null(slot.Rank);
        Assert.False(slot.IsCurrent);
        Assert.False(slot.IsMaxRoll);
        Assert.Equal(locked, slot.IsLocked);
        Assert.Equal(activated, slot.IsActivated);
        Assert.Empty(snapshot.AppliedEffects);
        Assert.Equal("—", GenusInsightOverlayRenderer.ValueText(slot));
        Assert.Equal("—", GenusInsightOverlayRenderer.RangeText(slot.Minimum, slot.Unit));
        Assert.False(GenusInsightOverlayRenderer.ShowMax(slot));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(5, 0, 0)]
    [InlineData(0, 65535, 0)]
    [InlineData(0, 0, 1)]
    public void UnsupportedNonemptyEffectsStayUnknownInsteadOfBeingReportedAsUnanalyzed(int grade, int stat, long value)
    {
        var snapshot = GenusInsightProjection.Create(Snapshot(2, 1,
            new CharacterGenusEffect(0, (byte)grade, (ushort)stat, value, false, true)));
        var slot = snapshot.Boards.Single(board => board.Genus == "cogni").Slots[0];
        Assert.Equal("Unbekannter Stat", slot.LabelDe);
        Assert.Equal("Unknown stat", slot.LabelEn);
        Assert.Null(slot.StatId);
        Assert.Null(slot.Value);
        Assert.Null(slot.Minimum);
        Assert.Null(slot.Maximum);
        Assert.Null(slot.RollFraction);
        Assert.False(slot.IsCurrent);
        Assert.False(slot.IsMaxRoll);
        Assert.Empty(snapshot.AppliedEffects);
    }

    [Fact]
    public void AnUnsupportedRankOfAKnownStatRetainsItsKnownLabelWithoutBecomingAnEmptyOrQualifiedRoll()
    {
        var snapshot = GenusInsightProjection.Create(Snapshot(6, 1, new CharacterGenusEffect(0, 0, 19, 0, false, true)));
        var slot = snapshot.Boards.Single(board => board.Genus == "special").Slots[0];
        Assert.Equal("FixingDamage", slot.StatId);
        Assert.Equal(GenusInsightCatalog.Default.FindStat("FixingDamage")!.LabelEn, slot.LabelEn);
        Assert.NotEqual("Not analyzed", slot.LabelEn);
        Assert.False(slot.IsCurrent);
        Assert.False(slot.IsMaxRoll);
        Assert.Null(slot.RollFraction);
        Assert.Empty(snapshot.AppliedEffects);
    }

    [Fact]
    public void ASlotWithoutAnyEffectRemainsUnknownAndDoesNotInventAnUnanalyzedObservation()
    {
        var snapshot = GenusInsightProjection.Create(Snapshot(2, 1));
        var slot = snapshot.Boards.Single(board => board.Genus == "cogni").Slots[0];
        Assert.Equal("Unknown stat", slot.LabelEn);
        Assert.Equal("Unbekannter Stat", slot.LabelDe);
        Assert.Null(slot.Value);
        Assert.False(slot.IsCurrent);
    }

    [Theory]
    [InlineData(2, 0, 4, 38, 12, "CriticalAddDamage")]
    [InlineData(2, 5, 5, 116, 17, "Evasion")]
    [InlineData(2, 6, 1, 394, 60, "IntellectDefense")]
    [InlineData(2, 8, 5, 116, 21, "Evasion")]
    [InlineData(3, 3, 2, 403, 18, "FeralAccuracy")]
    [InlineData(3, 4, 2, 408, 12, "FeralBlock")]
    [InlineData(3, 5, 2, 404, 11, "FeralEvasion")]
    [InlineData(3, 7, 5, 588, 115, "FrontAttackDefense")]
    [InlineData(3, 8, 2, 403, 12, "FeralAccuracy")]
    [InlineData(4, 0, 4, 284, 157, "DefensePierce")]
    [InlineData(4, 1, 3, 51, 113, "BossNpcDefense")]
    [InlineData(4, 2, 5, 116, 22, "Evasion")]
    [InlineData(4, 6, 4, 147, 117, "AdditionalHitResistRate")]
    [InlineData(4, 8, 2, 411, 13, "NatureAccuracy")]
    [InlineData(5, 1, 1, 420, 10, "TransEvasion")]
    [InlineData(5, 3, 2, 418, 62, "TransDefense")]
    [InlineData(5, 5, 5, 116, 26, "Evasion")]
    [InlineData(5, 6, 2, 421, 7, "TransCritical")]
    [InlineData(6, 1, 2, 51, 69, "BossNpcDefense")]
    [InlineData(6, 2, 5, 116, 21, "Evasion")]
    [InlineData(6, 3, 3, 449, 143, "IgnoreIronWall")]
    [InlineData(6, 5, 4, 116, 14, "Evasion")]
    [InlineData(6, 6, 2, 590, 37, "DecreaseFrontAttack")]
    public void TableCheckedPresetTwoRollsKeepTheirStatIdentityAndExactValue(
        int genus, int wireSlot, int grade, int nativeStat, long rawValue, string expectedStat)
    {
        Assert.Equal(expectedStat, GenusInsightNativeBindings.Stats[(ushort)nativeStat]);
        var projection = GenusInsightProjection.Create(Snapshot((byte)genus, 2,
            new CharacterGenusEffect((byte)wireSlot, (byte)grade, (ushort)nativeStat, rawValue, false, true)));
        var slot = projection.Boards.Single(board => board.Genus == Genus((byte)genus)).Slots[wireSlot];
        var definition = GenusInsightCatalog.Default.FindStat(expectedStat)!;
        var overall = GenusInsightCatalog.Default.Range(Genus((byte)genus), wireSlot + 1, expectedStat)!;
        Assert.Equal(expectedStat, slot.StatId);
        Assert.True(slot.IsCurrent);
        Assert.Equal(rawValue / definition.RawDivisor, slot.Value);
        Assert.Equal(overall.Minimum, slot.Minimum);
        Assert.Equal(overall.Maximum, slot.Maximum);
        var applied = Assert.Single(projection.AppliedEffects);
        Assert.Equal(expectedStat, applied.StatId);
        Assert.Equal(slot.Value, applied.Value);
    }

    [Fact]
    public void EveryCatalogOptionHasOneBindingOrAnExplicitlyUnqualifiedStat()
    {
        var catalog = GenusInsightCatalog.Default;
        var bindings = GenusInsightNativeBindings.Stats;
        string[] unqualified = ["AmplifyFrontAttack", "FrontAttackCriticalResist"];
        Assert.Equal(89, catalog.Stats.Count);
        Assert.Equal(87, bindings.Count);
        Assert.Equal(bindings.Count, bindings.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain((ushort)0, bindings.Keys);
        Assert.Equal(unqualified.Order(StringComparer.Ordinal),
            catalog.Stats.Keys.Except(bindings.Values, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.All(bindings.Values, stat => Assert.True(catalog.Stats.ContainsKey(stat)));
        foreach (var option in catalog.Slots.SelectMany(slot => slot.Options))
        {
            var matches = bindings.Where(binding => binding.Value == option.StatId).ToArray();
            if (unqualified.Contains(option.StatId, StringComparer.Ordinal)) Assert.Empty(matches);
            else Assert.Single(matches);
        }
    }

    [Fact]
    public void EveryBoundSlotAndRankOptionPreservesItsExactRawEndpointsAndOverallRange()
    {
        var catalog = GenusInsightCatalog.Default;
        var reverse = GenusInsightNativeBindings.Stats.ToDictionary(binding => binding.Value,
            binding => binding.Key, StringComparer.Ordinal);
        var tested = 0;
        var unqualified = 0;
        foreach (var slotDefinition in catalog.Slots)
        foreach (var option in slotDefinition.Options)
        {
            if (!reverse.TryGetValue(option.StatId, out var nativeStat)) { unqualified++; continue; }
            tested++;
            var definition = catalog.FindStat(option.StatId)!;
            var range = catalog.Range(slotDefinition.Genus, slotDefinition.Slot, option.StatId)!;
            foreach (var endpoint in new[] { option.Minimum, option.Maximum }.Distinct())
            {
                var raw = checked((long)(endpoint * definition.RawDivisor));
                Assert.Equal(endpoint, raw / definition.RawDivisor);
                var source = Snapshot(GenusType(slotDefinition.Genus), 1, new CharacterGenusEffect(
                    (byte)(slotDefinition.Slot - 1), option.Rank, nativeStat, raw, false, true));
                var projection = GenusInsightProjection.Create(source);
                var slot = projection.Boards.Single(board => board.Genus == slotDefinition.Genus)
                    .Slots[slotDefinition.Slot - 1];
                Assert.Equal(option.StatId, slot.StatId);
                Assert.True(slot.IsCurrent);
                Assert.Equal(endpoint, slot.Value);
                Assert.Equal(option.Rank, slot.Rank);
                Assert.Equal(range.Minimum, slot.Minimum);
                Assert.Equal(range.Maximum, slot.Maximum);
                Assert.Equal(endpoint == range.Maximum, slot.IsMaxRoll);
                Assert.Equal(endpoint, Assert.Single(projection.AppliedEffects).Value);
            }
        }
        Assert.True(tested > 0);
        Assert.True(unqualified > 0);
        Assert.Equal(catalog.Slots.Sum(slot => slot.Options.Count), tested + unqualified);
    }

    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void TheSanitized135SlotReferencePreservesEveryQualifiedRawValueAndSeparatesThe36EmptySlots()
    {
        var path = TestPrerequisites.RequiredValue("SOULCREST_GENUS_MAPPING_SNAPSHOT");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var boards = JsonSerializer.Deserialize<ImmutableDictionary<byte, CharacterGenusBoard>>(
            document.RootElement.GetProperty("Boards").GetRawText())!;
        var total = 0;
        var empty = 0;
        var qualified = 0;
        for (byte preset = 1; preset <= 3; preset++)
        {
            var snapshot = GenusInsightProjection.Create(Snapshot(boards, preset));
            var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var rawBoard in boards.Values)
            {
                var genus = Genus(rawBoard.CreatureType);
                var projected = snapshot.Boards.Single(board => board.Genus == genus);
                foreach (var raw in rawBoard.Panels[preset])
                {
                    total++;
                    var slot = projected.Slots.Single(row => row.Slot == raw.Slot + 1);
                    if (raw.Grade == 0 && raw.StatType == 0 && raw.Value == 0)
                    {
                        empty++;
                        Assert.Equal("Not analyzed", slot.LabelEn);
                        Assert.Null(slot.Value);
                        Assert.Null(slot.Minimum);
                        Assert.Null(slot.Maximum);
                        Assert.Null(slot.RollFraction);
                        Assert.False(slot.IsCurrent);
                        Assert.False(slot.IsMaxRoll);
                        continue;
                    }
                    qualified++;
                    Assert.True(GenusInsightNativeBindings.Stats.TryGetValue(raw.StatType, out var statId),
                        $"Unmapped nonempty native stat {raw.StatType}.");
                    var definition = GenusInsightCatalog.Default.FindStat(statId!)!;
                    var range = GenusInsightCatalog.Default.Range(genus, raw.Slot + 1, statId!)!;
                    var expected = raw.Value / definition.RawDivisor;
                    Assert.True(slot.IsCurrent);
                    Assert.Equal(statId, slot.StatId);
                    Assert.Equal(expected, slot.Value);
                    Assert.Equal(range.Minimum, slot.Minimum);
                    Assert.Equal(range.Maximum, slot.Maximum);
                    Assert.Equal(expected == range.Maximum, slot.IsMaxRoll);
                    if (raw.IsActivated) totals[statId!] = totals.GetValueOrDefault(statId!) + expected;
                }
            }
            Assert.Equal(totals.Count, snapshot.AppliedEffects.Count);
            foreach (var (stat, value) in totals)
                Assert.Equal(value, Assert.Single(snapshot.AppliedEffects, effect => effect.StatId == stat).Value);
        }
        Assert.Equal(135, total);
        Assert.Equal(99, qualified);
        Assert.Equal(36, empty);
    }

    private static string Genus(byte type) => type switch
    {
        2 => "cogni", 3 => "fera", 4 => "natura", 5 => "varian", 6 => "special",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static byte GenusType(string genus) => genus switch
    {
        "cogni" => 2, "fera" => 3, "natura" => 4, "varian" => 5, "special" => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(genus))
    };

    private static CharacterCombatSnapshot Snapshot(byte genus, byte preset, params CharacterGenusEffect[] slots) =>
        Snapshot(ImmutableDictionary<byte, CharacterGenusBoard>.Empty.Add(genus, new(genus, 10, 0,
            ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>>.Empty.Add(preset, [.. slots]))), preset);

    private static CharacterCombatSnapshot Snapshot(ImmutableDictionary<byte, CharacterGenusBoard> boards, byte preset) => new()
    {
        ProfileId = "anonymous-mapping-profile", Generation = 5, Sequence = 1, IsFresh = true,
        Pets = new(new PetProgressSnapshot(ImmutableDictionary<int, int>.Empty, ImmutableDictionary<int, int>.Empty)
        {
            SelectedGenusPanel = preset, GenusBoards = boards
        }, NetworkCombatSourceStatus.Complete, true, At, 1, "own-pet-genus-list")
    };
}
