namespace Soulcrest.Core.Combat;

/// <summary>Aggregates all additive level/stat components before applying derived curves, multipliers and rounding.</summary>
public static class CombatStatDerivation
{
    public static CombatDerivation Derive(CombatSourceGraph graph, CombatEffectCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        catalog ??= CombatEffectCatalog.Default;
        ValidateGraph(graph);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var sums = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var skills = new Dictionary<int, SkillAccumulator>();
        var passives = new List<CombatAppliedPassive>();
        var conflict = graph.Status == CombatKnowledgeStatus.Conflict;
        foreach (var source in graph.Sources)
        {
            if (source.Status != CombatKnowledgeStatus.Complete)
                missing.Add(source.Reason ?? "Source scope is not complete: " + source.Id + ".");
            conflict |= source.Status == CombatKnowledgeStatus.Conflict;
            var observed = source.Status is CombatKnowledgeStatus.Complete or CombatKnowledgeStatus.Partial;
            foreach (var effect in source.Effects)
                Add(effect, observed, source.Id);
            foreach (var skill in source.Skills)
            {
                if (skill.Application == CombatApplication.Inactive) continue;
                if (!skills.TryGetValue(skill.SkillId, out var total)) skills[skill.SkillId] = total = new(skill.Kind);
                if (total.Kind != skill.Kind) { total.Unknown = true; conflict = true; }
                if (!observed || skill.Level is null || skill.Application != CombatApplication.Confirmed && skill.Level != 0)
                {
                    total.Unknown = true;
                    missing.Add("Unknown skill component: " + source.Id + "/" + skill.SkillId + ".");
                }
                else total.Level = checked(total.Level + skill.Level.Value);
            }
        }
        foreach (var rule in catalog.PassiveEffects)
        {
            if (!skills.TryGetValue(rule.SkillId, out var skill)) continue;
            if (!skill.Unknown && skill.Level == 0) continue;
            if (rule.Application == CombatApplication.Inactive) continue;
            var amount = skill.Unknown ? null : rule.ValueAt(skill.Level);
            var qualified = IsPassiveApplicable(graph, rule);
            var applied = qualified && amount is not null;
            var effect = amount is { } value ? new CombatEffect(rule.StatId, value, rule.Operation,
                qualified ? CombatApplication.Confirmed : rule.Application, rule.RuleId) : null;
            passives.Add(new(rule.SkillId, rule.RuleId, effect,
                applied ? CombatKnowledgeStatus.Complete : CombatKnowledgeStatus.Unknown));
            if (applied) Add(effect!, true, rule.RuleId);
            else
            {
                Get(rule.StatId).Unknown = true;
                missing.Add("Passive application or level curve is not qualified: " + rule.RuleId + ".");
            }
        }
        // PcStatSecond uses discrete rows. No interpolation, extrapolation or guessed ratio target is performed.
        var primaryIds = catalog.PrimaryStatIds;
        foreach (var curve in catalog.PrimaryStatCurves)
        {
            if (!sums.TryGetValue(curve.PrimaryStatId, out var primary)) continue;
            decimal? level = primary.Unknown ? null : Round(catalog.GetStat(curve.PrimaryStatId)!, primary.Flat * (1 + primary.Increase / 100));
            if (level == 0) continue; // A confirmed zero contribution is identical to an absent additive component.
            var row = level is { } known && known >= int.MinValue && known <= int.MaxValue && decimal.Truncate(known) == known
                ? catalog.GetPrimaryLevel(curve.PrimaryStatId, (int)known) : null;
            if (row is null)
            {
                foreach (var id in curve.Values.SelectMany(value => value.Effects).Select(effect => effect.StatId).Distinct(StringComparer.Ordinal))
                    Get(id).Unknown = true;
                missing.Add("Missing primary-stat curve row: " + curve.PrimaryStatId + ".");
                continue;
            }
            foreach (var effect in row.Effects)
            {
                if (primaryIds.Contains(effect.StatId))
                    throw new InvalidDataException("Primary-stat curves must not contain recursive primary dependencies.");
                var normalized = catalog.NormalizeEffect(effect.StatId, effect.RawValue);
                if (normalized is null) { Get(effect.StatId).Unknown = true; continue; }
                Add(new(effect.StatId, normalized.Value, effect.Operation, CombatApplication.Confirmed), true, "primary:" + curve.PrimaryStatId);
            }
        }
        foreach (var relation in catalog.StatRelations)
        {
            if (!sums.TryGetValue(relation.ModifierStatId, out var modifier)) continue;
            var target = Get(relation.StatId);
            if (modifier.Unknown) target.Unknown = true;
            else
            {
                var value = modifier.Flat * (1 + modifier.Increase / 100);
                if (relation.Operation == CombatEffectOperation.IncreasePercent) target.Increase += value;
                else target.Flat += value;
            }
        }
        if (graph.Status != CombatKnowledgeStatus.Complete)
            missing.Add("The complete character source scope is not known.");
        var isComplete = graph.Status == CombatKnowledgeStatus.Complete && missing.Count == 0;
        var status = conflict ? CombatKnowledgeStatus.Conflict : isComplete ? CombatKnowledgeStatus.Complete
            : graph.Status == CombatKnowledgeStatus.Stale ? CombatKnowledgeStatus.Stale : CombatKnowledgeStatus.Partial;
        var stats = sums.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row =>
        {
            var definition = catalog.GetStat(row.Key);
            decimal? value = row.Value.Unknown || definition is null ? null
                : Round(definition, row.Value.Flat * (1 + row.Value.Increase / 100));
            return new CombatDerivedStat(row.Key, definition?.Unit ?? CombatStatUnit.Points, row.Value.Flat, row.Value.Increase,
                value, value is null ? CombatKnowledgeStatus.Unknown : isComplete ? CombatKnowledgeStatus.Complete : CombatKnowledgeStatus.Partial);
        }).ToArray();
        var derivedSkills = skills.OrderBy(row => row.Key).Select(row => new CombatDerivedSkill(row.Key, row.Value.Kind,
            row.Value.Unknown ? null : row.Value.Level, row.Value.Unknown ? CombatKnowledgeStatus.Unknown
                : isComplete ? CombatKnowledgeStatus.Complete : CombatKnowledgeStatus.Partial)).ToArray();
        var statValues = stats.ToDictionary(row => row.StatId, StringComparer.Ordinal);
        var skillValues = derivedSkills.ToDictionary(row => row.SkillId);
        var comparisons = graph.NativeTotals.OrderBy(row => row.Domain).ThenBy(row => row.NativeStatId).Select(native =>
        {
            var definition = catalog.GetNativeStat(native.NativeStatId);
            var component = catalog.GetNativeNormalized(native.NativeStatId, native.RawValue, native.Domain);
            var model = definition is not null ? statValues.GetValueOrDefault(definition.StatId) : null;
            var nativeValue = native.Domain == CombatNativeValueDomain.CharacterStats && component?.Role == CombatNativeStatRole.DisplayTotal
                ? component.Value : (decimal?)null;
            var modelValue = model?.Value;
            return new CombatNativeComparison(native.NativeStatId, definition?.StatId, native.RawValue, nativeValue, modelValue,
                model?.Status == CombatKnowledgeStatus.Complete && modelValue is { } knownModel && nativeValue is { } observed ? observed - knownModel : null,
                nativeValue is null || modelValue is null ? CombatKnowledgeStatus.Unknown : status)
            {
                Domain = native.Domain, Role = component?.Role ?? CombatNativeStatRole.Unknown,
                NormalizedComponent = component?.Value,
                NativeFlat = component?.Role == CombatNativeStatRole.FlatComponent ? component.Value : null,
                NativeIncreasePercent = component?.Role == CombatNativeStatRole.IncreasePercentComponent ? component.Value : null,
                ModelFlat = model?.Flat, ModelIncreasePercent = model?.IncreasePercent
            };
        }).ToArray();
        return new()
        {
            Status = status, Stats = Array.AsReadOnly(stats), Skills = Array.AsReadOnly(derivedSkills), Passives = Array.AsReadOnly(passives.ToArray()),
            NativeComparisons = Array.AsReadOnly(comparisons),
            SkillComparisons = Array.AsReadOnly(graph.NativeSkillTotals.Select(native =>
            {
                var level = skillValues.GetValueOrDefault(native.SkillId)?.Level;
                return new CombatSkillComparison(native.SkillId, native.TotalLevel, level, level is { } value ? native.TotalLevel - value : null);
            }).ToArray()), MissingRequirements = Array.AsReadOnly(missing.Order(StringComparer.Ordinal).ToArray())
        };

        Accumulator Get(string id)
        {
            if (!sums.TryGetValue(id, out var value)) sums[id] = value = new();
            return value;
        }
        void Add(CombatEffect effect, bool observed, string sourceId)
        {
            if (effect.Application == CombatApplication.Inactive || effect.Value == 0) return;
            var total = Get(effect.StatId);
            if (!observed || effect.Application != CombatApplication.Confirmed || catalog.GetStat(effect.StatId) is null)
            {
                total.Unknown = true;
                missing.Add("Unqualified stat effect: " + sourceId + "/" + effect.StatId + ".");
                return;
            }
            if (effect.Operation == CombatEffectOperation.Flat) total.Flat += effect.Value;
            else total.Increase += effect.Value;
        }
    }

    public static decimal Round(CombatStatDefinition definition, decimal value) => definition.Unit == CombatStatUnit.Percent
        ? value : definition.Rounding switch { CombatRounding.Ceiling => decimal.Ceiling(value), CombatRounding.Floor => decimal.Floor(value), _ => value };

    public static bool IsPassiveApplicable(CombatSourceGraph graph, CombatPassiveRule rule) => rule.Application == CombatApplication.Confirmed
        || rule.Application == CombatApplication.Unknown && rule.HasUnconditionalPassiveChain && rule.AbnormalId is { } abnormal
            && graph.ActiveAbnormalStatus is CombatKnowledgeStatus.Complete or CombatKnowledgeStatus.Partial
            && (graph.ActiveAbnormalIds.Contains(abnormal)
                || graph.ActiveAbnormals.Any(witness => witness.AbnormalId == abnormal && !witness.ReadyToRemove));

    internal static void ValidateGraph(CombatSourceGraph graph)
    {
        if (graph.Sources is null || graph.NativeTotals is null || graph.NativeSkillTotals is null || graph.ActiveAbnormalIds is null || graph.ActiveAbnormals is null
            || !Enum.IsDefined(graph.Status) || graph.Sources.Count > 32768 || graph.NativeTotals.Count > 4096
            || graph.NativeSkillTotals.Count > 4096 || graph.ActiveAbnormalIds.Count > 16384 || !Enum.IsDefined(graph.ActiveAbnormalStatus)
            || graph.ActiveAbnormalIds.Any(id => id <= 0) || graph.ActiveAbnormals.Count > 16384
            || graph.ActiveAbnormals.Any(row => row.AbnormalId <= 0 || row.Level is < 0 or > 255)) throw new ArgumentException("Invalid combat source graph.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in graph.Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id) || !ids.Add(source.Id) || !Enum.IsDefined(source.Kind)
                || !Enum.IsDefined(source.Status) || source.Effects is null || source.Skills is null || source.Effects.Count > 4096 || source.Skills.Count > 4096)
                throw new ArgumentException("Invalid or duplicate combat source.");
            if (source.Effects.Any(effect => effect is null || string.IsNullOrWhiteSpace(effect.StatId)
                    || !Enum.IsDefined(effect.Operation) || !Enum.IsDefined(effect.Application) || Math.Abs(effect.Value) > 1_000_000_000_000m)
                || source.Skills.Any(skill => skill is null || skill.SkillId <= 0 || skill.Level is < 0 or > 255
                    || !Enum.IsDefined(skill.Kind) || !Enum.IsDefined(skill.Application)))
                throw new ArgumentException("Invalid combat effect or skill component.");
        }
        if (graph.NativeTotals.Select(row => (row.Domain, row.NativeStatId)).Distinct().Count() != graph.NativeTotals.Count
            || graph.NativeTotals.Any(row => !Enum.IsDefined(row.Domain))
            || graph.NativeSkillTotals.Select(row => row.SkillId).Distinct().Count() != graph.NativeSkillTotals.Count)
            throw new ArgumentException("Duplicate native comparison value.");
    }

    private sealed class Accumulator { public decimal Flat; public decimal Increase; public bool Unknown; }
    private sealed class SkillAccumulator(Daevanion.DaevanionSkillKind kind)
    { public Daevanion.DaevanionSkillKind Kind { get; } = kind; public int Level; public bool Unknown; }
}
