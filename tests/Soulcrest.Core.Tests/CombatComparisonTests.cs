using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CombatComparisonTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly CombatSourceContext Source = new("stream-test", 1);
    private static readonly CombatEntityReference Boss = new("boss-test", 1, "stream-test");
    private static readonly CombatMeasurementProfile Profile = new("boss-damage-v1", "build-test", "decoder-test", "catalog-test");

    [Fact]
    public void MatchesAllFiveMembersByIdentityDespiteDifferentRanksAndRosterOrder()
    {
        var members = Enumerable.Range(1, 5).Select(i => new CombatParticipant("character-" + i, "Player " + i)).ToArray();
        var left = Attempt(members, members.Select((p, i) => Damage(i + 1, p.ParticipantId, (i + 1) * 100)).ToArray());
        var right = Attempt(members.Reverse().ToArray(), members.Select((p, i) => Damage(i + 1, p.ParticipantId, (5 - i) * 200)).ToArray());

        var result = CombatComparison.Compare(left, right);

        Assert.True(result.IsComparable);
        Assert.Equal(5, result.Participants.Count);
        var first = result.Participants.Single(p => p.ParticipantId == "character-1");
        Assert.Equal(CombatComparisonPresence.Both, first.Presence);
        Assert.Equal(100m, first.Damage.Left);
        Assert.Equal(1000m, first.Damage.Right);
        Assert.Equal(90m, first.Dps.Difference);
        Assert.Equal(900m, first.Skills.Single().Damage.Difference);
    }

    [Fact]
    public void SameDisplayNameDoesNotMergeDifferentCharactersOrFillMissingSideWithZero()
    {
        var left = Attempt([new("character-a", "Player")], [Damage(1, "character-a", 100)]);
        var right = Attempt([new("character-b", "Player")], [Damage(1, "character-b", 200)]);

        var result = CombatComparison.Compare(left, right);

        Assert.True(result.IsComparable);
        Assert.Equal(2, result.Participants.Count);
        var former = result.Participants.Single(p => p.ParticipantId == "character-a");
        Assert.Equal(CombatComparisonPresence.LeftOnly, former.Presence);
        Assert.Null(former.Damage.Right);
        Assert.Null(former.Damage.Difference);
        Assert.Null(former.Skills.Single().Damage.Right);
    }

    [Fact]
    public void GapExcludesByDefaultButExplicitInspectionKeepsWarning()
    {
        var complete = Attempt([new("character-a")], [Damage(1, "character-a", 100)]);
        var gap = complete with { Id = Guid.NewGuid(), Events = [Damage(1, "character-a", 100), Envelope(2, 2, new CombatGapEvent(CombatGapReason.Transport))] };

        var excluded = CombatComparison.Compare(complete, gap);
        Assert.False(excluded.IsComparable);
        Assert.Contains("incomplete-data", excluded.ExclusionReasons);
        Assert.Empty(excluded.Participants);

        var inspected = CombatComparison.Compare(complete, gap, includeIncomplete: true);
        Assert.True(inspected.IsComparable);
        Assert.Contains("incomplete-data-included", inspected.Warnings);
    }

    [Theory]
    [InlineData("boss")]
    [InlineData("build")]
    [InlineData("decoder")]
    [InlineData("catalog")]
    [InlineData("ruleset")]
    [InlineData("variant")]
    public void RejectsDifferentEncounterOrMeasurementVersions(string changed)
    {
        var left = Attempt([new("character-a")], [Damage(1, "character-a", 100)]);
        var right = changed switch
        {
            "boss" => left with { BossKey = "other-boss" },
            "build" => left with { MeasurementProfile = Profile with { GameBuild = "different" } },
            "decoder" => left with { MeasurementProfile = Profile with { DecoderVersion = "different" } },
            "catalog" => left with { MeasurementProfile = Profile with { CatalogVersion = "different" } },
            "variant" => left with { EncounterVariantKey = "different-difficulty" },
            _ => left with { MeasurementProfile = Profile with { RulesetVersion = "different" } }
        };

        Assert.False(CombatComparison.Compare(left, right).IsComparable);
    }

    [Fact]
    public void UnknownMeasurementVersionCannotClaimCompatibility()
    {
        var left = Attempt([new("character-a")], [Damage(1, "character-a", 100)]) with
        { MeasurementProfile = new("boss-damage-v1") };
        Assert.Contains("measurement-profile-unknown", CombatComparison.Compare(left, left).ExclusionReasons);
    }

    [Fact]
    public void UnknownEncounterDifficultyCannotClaimCompatibility()
    {
        var attempt = Attempt([new("character-a")], [Damage(1, "character-a", 100)]) with
        { EncounterVariantKey = null };
        Assert.Contains("encounter-variant-unknown", CombatComparison.Compare(attempt, attempt).ExclusionReasons);
    }

    [Fact]
    public void DummyRequiresSameMeasuredDurationWhileBossesCanDiffer()
    {
        var left = Attempt([new("character-a")], [Damage(1, "character-a", 100)]);
        var right = left with { Id = Guid.NewGuid(), DurationTicks = TimeSpan.FromSeconds(20).Ticks };
        Assert.True(CombatComparison.Compare(left, right).IsComparable);
        Assert.Contains("dummy-duration-mismatch", CombatComparison.Compare(
            left with { Kind = CombatAttemptKind.TrainingDummy },
            right with { Kind = CombatAttemptKind.TrainingDummy }).ExclusionReasons);
    }

    [Fact]
    public void ComparesBuffUptimeForAnotherMemberAndKeepsIntervalsInPrimarySummary()
    {
        var members = new CombatParticipant[] { new("character-a", IsSelf: true), new("character-b") };
        var left = Attempt(members, [Damage(1, "character-a", 100), Envelope(2, 2,
            new CombatBuffEvent("buff-test", CombatBuffAction.Apply, "character-b", SourceParticipantId: "character-a", InitialStateKnown: true)),
            Envelope(3, 6, new CombatBuffEvent("buff-test", CombatBuffAction.Remove, "character-b", SourceParticipantId: "character-a", InitialStateKnown: true))]);
        var right = left with { Id = Guid.NewGuid(), Events = [left.Events[0], left.Events[1],
            Envelope(3, 8, new CombatBuffEvent("buff-test", CombatBuffAction.Remove, "character-b", SourceParticipantId: "character-a", InitialStateKnown: true))] };

        var result = CombatComparison.Compare(left, right);

        Assert.True(result.IsComparable);
        var buff = result.Participants.Single(p => p.ParticipantId == "character-b").Buffs.Single();
        Assert.Equal(0.4m, buff.Uptime.Left);
        Assert.Equal(0.6m, buff.Uptime.Right);
        Assert.Equal(0.2m, buff.Uptime.Difference);
        Assert.Single(result.Left.Buffs.Single().Intervals);
    }

    [Fact]
    public void ZeroBaselineHasNoInventedRelativeChange()
    {
        var metric = new CombatMetricComparison(0, 100);
        Assert.Equal(100, metric.Difference);
        Assert.Null(metric.RelativeDifference);
    }

    [Fact]
    public void UnrepresentableRelativeChangeRemainsUnknownInsteadOfCrashingTheReport()
    {
        var metric = new CombatMetricComparison(0.000000000001m, decimal.MaxValue);
        Assert.Null(metric.RelativeDifference);
        Assert.NotNull(metric.Difference);
    }

    private static CombatAttemptDocument Attempt(CombatParticipant[] players, CombatEventEnvelope[] events) => new()
    {
        Id = Guid.NewGuid(), BossKey = "boss-test", EncounterVariantKey = "normal-test", BossTargets = [Boss], StartedAtUtc = Start,
        DurationTicks = TimeSpan.FromSeconds(10).Ticks, EndReason = CombatEndReason.Victory,
        Capabilities = CombatCapabilities.Damage | CombatCapabilities.Buffs,
        MeasurementProfile = Profile, Participants = players, Events = events
    };

    private static CombatEventEnvelope Damage(long sequence, string player, long amount) =>
        Envelope(sequence, sequence, new CombatDamageEvent(player, Boss, "skill-test", amount));

    private static CombatEventEnvelope Envelope(long sequence, double seconds, CombatEvent item) =>
        new(sequence, TimeSpan.FromSeconds(seconds).Ticks, Start.AddSeconds(seconds), Source, item);
}
