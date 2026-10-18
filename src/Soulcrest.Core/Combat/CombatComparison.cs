namespace Soulcrest.Core.Combat;

public enum CombatComparisonPresence { Both, LeftOnly, RightOnly }

public sealed record CombatMetricComparison(decimal? Left, decimal? Right)
{
    public decimal? Difference => Left.HasValue && Right.HasValue ? Right - Left : null;
    public decimal? RelativeDifference
    {
        get
        {
            if (Left is null or 0 || !Right.HasValue) return null;
            try { return Difference / Left; }
            catch (OverflowException) { return null; }
        }
    }
}

public sealed record CombatSkillComparison(string SkillId, CombatMetricComparison Damage,
    CombatMetricComparison Hits, CombatMetricComparison PeriodicTicks, CombatMetricComparison Casts,
    CombatMetricComparison Healing, CombatMetricComparison MaximumHit, CombatMetricComparison AverageHit,
    IReadOnlyDictionary<string, CombatMetricComparison> Rates);

public sealed record CombatBuffComparison(string BuffId, CombatMetricComparison Applications,
    CombatMetricComparison ActiveTicks, CombatMetricComparison Uptime);

public sealed record CombatParticipantComparison(string ParticipantId, CombatComparisonPresence Presence,
    CombatParticipant? LeftParticipant, CombatParticipant? RightParticipant,
    CombatMetricComparison Damage, CombatMetricComparison Dps, CombatMetricComparison Healing,
    CombatMetricComparison Hps, CombatMetricComparison Deaths,
    IReadOnlyList<CombatSkillComparison> Skills, IReadOnlyList<CombatBuffComparison> Buffs);

public sealed record CombatComparisonResult(CombatAttemptSummary Left, CombatAttemptSummary Right,
    IReadOnlyList<string> ExclusionReasons, IReadOnlyList<string> Warnings,
    IReadOnlyList<CombatParticipantComparison> Participants)
{
    public bool IsComparable => ExclusionReasons.Count == 0;
}

/// <summary>
/// Derived local views, never a second statistics store. Matches scoped stable identities, not names,
/// damage rank or party slot. Missing members/skills remain missing instead of becoming invented zeroes.
/// </summary>
public static class CombatComparison
{
    public static CombatComparisonResult Compare(CombatAttemptDocument left, CombatAttemptDocument right,
        bool includeIncomplete = false)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftSummary = CombatProjection.Project(left);
        var rightSummary = CombatProjection.Project(right);
        var excluded = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(left.BossKey) || string.IsNullOrWhiteSpace(right.BossKey))
            excluded.Add("encounter-unknown");
        else if (!string.Equals(left.BossKey, right.BossKey, StringComparison.Ordinal))
            excluded.Add("encounter-mismatch");
        if (string.IsNullOrWhiteSpace(left.EncounterVariantKey) || string.IsNullOrWhiteSpace(right.EncounterVariantKey))
            excluded.Add("encounter-variant-unknown");
        else if (!string.Equals(left.EncounterVariantKey, right.EncounterVariantKey, StringComparison.Ordinal))
            excluded.Add("encounter-variant-mismatch");
        if (left.Kind != right.Kind) excluded.Add("attempt-kind-mismatch");
        if (left.DurationTicks is not > 0 || right.DurationTicks is not > 0 ||
            left.EndReason is null || right.EndReason is null)
            excluded.Add("attempt-not-finished");
        if (left.Kind == CombatAttemptKind.TrainingDummy && right.Kind == CombatAttemptKind.TrainingDummy &&
            left.DurationTicks != right.DurationTicks)
            excluded.Add("dummy-duration-mismatch");

        CheckProfile(left.MeasurementProfile, right.MeasurementProfile, excluded);
        if (!leftSummary.IsComplete || !rightSummary.IsComplete)
        {
            if (includeIncomplete) warnings.Add("incomplete-data-included");
            else excluded.Add("incomplete-data");
        }
        if (left.EndReason != right.EndReason) warnings.Add("end-reason-differs");
        if (left.Capabilities != right.Capabilities) warnings.Add("capabilities-differ");
        if (excluded.Count > 0)
            return new(leftSummary, rightSummary, excluded.AsReadOnly(), warnings.AsReadOnly(), []);

        var leftPlayers = leftSummary.Players.ToDictionary(p => p.Participant.ParticipantId, StringComparer.Ordinal);
        var rightPlayers = rightSummary.Players.ToDictionary(p => p.Participant.ParticipantId, StringComparer.Ordinal);
        var participants = leftPlayers.Keys.Union(rightPlayers.Keys, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => CompareParticipant(id, leftPlayers.GetValueOrDefault(id), rightPlayers.GetValueOrDefault(id),
                leftSummary.Buffs, rightSummary.Buffs)).ToArray();
        return new(leftSummary, rightSummary, excluded.AsReadOnly(), warnings.AsReadOnly(), participants);
    }

    private static void CheckProfile(CombatMeasurementProfile? left, CombatMeasurementProfile? right,
        List<string> excluded)
    {
        if (left is null || right is null || string.IsNullOrWhiteSpace(left.RulesetVersion) ||
            string.IsNullOrWhiteSpace(right.RulesetVersion) || string.IsNullOrWhiteSpace(left.GameBuild) ||
            string.IsNullOrWhiteSpace(right.GameBuild) || string.IsNullOrWhiteSpace(left.DecoderVersion) ||
            string.IsNullOrWhiteSpace(right.DecoderVersion))
        {
            excluded.Add("measurement-profile-unknown");
            return;
        }
        if (left != right) excluded.Add("measurement-profile-mismatch");
    }

    private static CombatParticipantComparison CompareParticipant(string id,
        CombatPlayerSummary? left, CombatPlayerSummary? right,
        IReadOnlyList<CombatBuffSummary> leftBuffs, IReadOnlyList<CombatBuffSummary> rightBuffs)
    {
        var leftSkills = left?.Skills.ToDictionary(s => s.SkillId, StringComparer.Ordinal) ?? [];
        var rightSkills = right?.Skills.ToDictionary(s => s.SkillId, StringComparer.Ordinal) ?? [];
        var skills = leftSkills.Keys.Union(rightSkills.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => CompareSkill(key, leftSkills.GetValueOrDefault(key), rightSkills.GetValueOrDefault(key)))
            .ToArray();
        var leftEffects = leftBuffs.Where(b => b.TargetParticipantId == id)
            .ToDictionary(b => b.BuffId, StringComparer.Ordinal);
        var rightEffects = rightBuffs.Where(b => b.TargetParticipantId == id)
            .ToDictionary(b => b.BuffId, StringComparer.Ordinal);
        var buffs = leftEffects.Keys.Union(rightEffects.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => CompareBuff(key, leftEffects.GetValueOrDefault(key), rightEffects.GetValueOrDefault(key)))
            .ToArray();
        return new(id, left is null ? CombatComparisonPresence.RightOnly :
                right is null ? CombatComparisonPresence.LeftOnly : CombatComparisonPresence.Both,
            left?.Participant, right?.Participant, new(left?.Damage, right?.Damage), new(left?.Dps, right?.Dps),
            new(left?.Healing, right?.Healing), new(left?.Hps, right?.Hps), new(left?.Deaths, right?.Deaths), skills, buffs);
    }

    private static CombatSkillComparison CompareSkill(string id, CombatSkillSummary? left, CombatSkillSummary? right)
    {
        var rates = (left?.Rates.Keys ?? []).Union(right?.Rates.Keys ?? [], StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToDictionary(key => key, key => new CombatMetricComparison(
                left?.Rates.GetValueOrDefault(key)?.Fraction, right?.Rates.GetValueOrDefault(key)?.Fraction),
                StringComparer.Ordinal);
        return new(id, new(left?.Damage, right?.Damage), new(left?.Hits, right?.Hits),
            new(left?.PeriodicTicks, right?.PeriodicTicks), new(left?.Casts, right?.Casts),
            new(left?.Healing, right?.Healing), new(left?.MaximumHit, right?.MaximumHit),
            new(left?.AverageHit, right?.AverageHit), rates);
    }

    private static CombatBuffComparison CompareBuff(string id, CombatBuffSummary? left, CombatBuffSummary? right) =>
        new(id, new(left?.Applications, right?.Applications), new(left?.ActiveTicks, right?.ActiveTicks),
            new(left?.Uptime, right?.Uptime));
}
