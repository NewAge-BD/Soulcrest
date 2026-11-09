using System.Collections.Frozen;

namespace Soulcrest.Core.Combat;

/// <summary>
/// Explicit forward projection of independently qualified wire components to character-panel values.
/// No inferred absent zeros, visual input, runtime IO, or promotion to a combat-score stat-ID contract occurs.
/// </summary>
public static class CharacterDisplayStatProjection
{
    public const string Version = "character-display-26262663-v3";
    public const int QualifiedClientBuild = 26262663;
    public static IReadOnlyList<CharacterDisplayForwardRule> Rules { get; } = CreateRules();

    public static CharacterDisplayStats Evaluate(CharacterDisplayStatsInput input) => Project(input);

    public static CharacterDisplayStats Project(CharacterDisplayStatsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var stats = input.NativeStats;
        // Copy at the boundary: no mutable caller dictionary can alter a published row or dependency.
        if (stats is not null) stats = stats with { Values = stats.Values.ToFrozenDictionary() };
        var rows = Rules.Select(rule => ProjectRow(input, stats, rule)).ToArray();
        return new()
        {
            ClientBuild = input.ClientBuild, Generation = input.Generation,
            Rows = Array.AsReadOnly(rows), Status = OverallStatus(rows)
        };
    }

    private sealed record ForwardValue(decimal? Value, decimal? UnroundedValue, CombatKnowledgeStatus Status, string? Reason,
        IReadOnlyList<CharacterDisplayStatDependency> Inputs, IReadOnlyList<ushort> MissingInputs,
        DateTimeOffset? ObservedAt, string Source, bool ScopeQualified);

    private static CharacterDisplayStatRow ProjectRow(CharacterDisplayStatsInput input, CharacterDisplayNativeStats? stats,
        CharacterDisplayForwardRule rule)
    {
        var forward = Forward(input, stats, rule);
        var english = rule.LabelEn ?? CombatEffectCatalog.Default.StatLabel(rule.LabelStatId, "en");
        var german = rule.LabelDe ?? CombatEffectCatalog.Default.StatLabel(rule.LabelStatId, "de");
        if (string.IsNullOrWhiteSpace(german) || german.Contains('\uFFFD')) german = english;
        var row = new CharacterDisplayStatRow
        {
            Id = rule.OutputId, LabelDe = german, LabelEn = english, Category = rule.Category,
            Unit = rule.Unit, Value = forward.Value, UnroundedValue = forward.UnroundedValue, Status = forward.Status,
            Qualification = forward.ScopeQualified ? rule.Qualification : CharacterDisplayStatQualification.Unknown,
            RuleId = rule.Id, Formula = rule.Formula, Evidence = rule.Evidence,
            Inputs = forward.Inputs, MissingInputIds = forward.MissingInputs,
            ObservedAt = forward.ObservedAt, Source = forward.Source,
            ForwardValue = forward.Value, ForwardStatus = forward.Status, ForwardReason = forward.Reason,
            Reason = forward.Reason
        };
        var maximum = rule.OutputId switch { "HPMax" => input.HpMaximum, "MPMax" => input.MpMaximum, _ => null };
        if (maximum is null || input.ClientBuild != QualifiedClientBuild || input.Generation <= 0) return row;
        var qualifiedSource = maximum.Kind switch
        {
            CharacterDisplayVitalSourceKind.Own => maximum.Source == "own-entry-resource-maxima",
            CharacterDisplayVitalSourceKind.StateUpdate8D00 => maximum.Source == "own-resource-maxima-update",
            _ => false
        };
        var (status, reason) = EvidenceStatus(maximum.Status, maximum.IsFresh, maximum.Generation,
            input.Generation, maximum.ObservedAt, qualifiedSource);
        if (status == CombatKnowledgeStatus.Complete && maximum.Value < 0)
            (status, reason) = (CombatKnowledgeStatus.Conflict, "InvalidMaximum");
        if (status == CombatKnowledgeStatus.Complete)
        {
            var comparison = forward.Value is { } calculated
                ? calculated == maximum.Value ? CombatKnowledgeStatus.Complete : CombatKnowledgeStatus.Conflict
                : CombatKnowledgeStatus.Unknown;
            var resource = rule.OutputId == "HPMax" ? "hp" : "mp";
            return row with
            {
                Value = maximum.Value, UnroundedValue = maximum.Value, Status = CombatKnowledgeStatus.Complete,
                Qualification = CharacterDisplayStatQualification.NativeDirect,
                RuleId = $"character-display-26262663-{maximum.Kind.ToString().ToLowerInvariant()}-{resource}-maximum-v1",
                Formula = maximum.Kind == CharacterDisplayVitalSourceKind.Own ? $"own.{resource}Max"
                    : $"0x8D00.maximum[{(resource == "hp" ? 7 : 8)}]",
                Evidence = Array.AsReadOnly(rule.Evidence.Concat(new[] { "qualified-own-resource-maximum-2026-11-03" }).ToArray()),
                ObservedAt = maximum.ObservedAt, Source = maximum.Source, ComparisonStatus = comparison,
                Reason = comparison == CombatKnowledgeStatus.Conflict ? "NativeForwardMismatch" : null
            };
        }
        // An explicitly conflicting native maximum cannot be hidden behind a formula fallback.
        if (status == CombatKnowledgeStatus.Conflict)
            return row with { Value = null, Status = status, Reason = reason, Source = maximum.Source,
                ObservedAt = maximum.ObservedAt, Qualification = qualifiedSource
                    ? CharacterDisplayStatQualification.NativeDirect : CharacterDisplayStatQualification.Unknown };
        if (forward.Value is not null) return row;
        if (qualifiedSource && status == CombatKnowledgeStatus.Stale)
            return row with { Status = status, Reason = reason, Source = maximum.Source,
                ObservedAt = maximum.ObservedAt, Qualification = CharacterDisplayStatQualification.NativeDirect };
        return row with { Reason = forward.Reason ?? reason };
    }

    private static ForwardValue Forward(CharacterDisplayStatsInput input, CharacterDisplayNativeStats? stats,
        CharacterDisplayForwardRule rule)
    {
        var dependencies = rule.FlatInputs.Select(term => new CharacterDisplayStatDependency(term.NativeId,
            stats?.Values.TryGetValue(term.NativeId, out var value) == true ? value : null, term.Coefficient)).ToList();
        if (rule.IncreaseBasisPointsInput is { } ratio)
            dependencies.Add(new(ratio, stats?.Values.TryGetValue(ratio, out var value) == true ? value : null,
                1, CharacterDisplayInputRole.IncreaseBasisPoints));
        var frozen = Array.AsReadOnly(dependencies.ToArray());
        var missing = Array.AsReadOnly(dependencies.Where(value => value.RawValue is null).Select(value => value.NativeId).ToArray());
        ForwardValue Result(decimal? value, CombatKnowledgeStatus status, string? reason, bool scope = true, decimal? unrounded = null) =>
            new(value, unrounded, status, reason, frozen, missing, stats?.ObservedAt, stats?.Source ?? "unknown", scope);
        if (input.ClientBuild != rule.ClientBuild) return Result(null, CombatKnowledgeStatus.Unknown, "UnqualifiedBuild", false);
        if (input.Generation <= 0) return Result(null, CombatKnowledgeStatus.Unknown, "MissingGeneration", false);
        if (rule.RequiredClassId is { } requiredClass && input.ClassId != requiredClass)
            return Result(null, CombatKnowledgeStatus.Unknown, "UnqualifiedClass", false);
        if (rule.Qualification == CharacterDisplayStatQualification.Unknown)
            return Result(null, CombatKnowledgeStatus.Unknown, "UnqualifiedMapping", false);
        if (stats is null) return Result(null, CombatKnowledgeStatus.Unknown, "MissingSource");
        var allowedSource = stats.Source is "own-entry-stat-subset" or "own-full-stat-list" or "own-stat-update";
        var (status, reason) = EvidenceStatus(stats.Status, stats.IsFresh, stats.Generation,
            input.Generation, stats.ObservedAt, allowedSource);
        if (status != CombatKnowledgeStatus.Complete) return Result(null, status, reason);
        if (missing.Count != 0)
            return Result(null, missing.Count < dependencies.Count ? CombatKnowledgeStatus.Partial : CombatKnowledgeStatus.Unknown, "MissingInputs");
        var flat = dependencies.Where(value => value.Role == CharacterDisplayInputRole.Flat)
            .Sum(value => value.RawValue!.Value * value.Coefficient) / rule.Divisor;
        var basisPoints = rule.IncreaseBasisPointsInput is not null
            ? dependencies.Single(value => value.Role == CharacterDisplayInputRole.IncreaseBasisPoints).RawValue!.Value : 0;
        if (basisPoints < -10000) return Result(null, CombatKnowledgeStatus.Conflict, "InvalidComponentValue");
        var unrounded = flat * (10000m + basisPoints) / 10000m;
        if (!rule.AllowNegative && unrounded < 0) return Result(null, CombatKnowledgeStatus.Conflict, "InvalidComponentValue");
        var projected = rule.Rounding switch
        {
            CombatRounding.Ceiling => decimal.Ceiling(unrounded),
            CombatRounding.Floor => decimal.Floor(unrounded),
            _ => unrounded
        };
        if (rule.DisplayDecimalPlaces is { } places) projected = decimal.Round(projected, places, MidpointRounding.ToEven);
        return Result(projected, CombatKnowledgeStatus.Complete, null, unrounded: unrounded);
    }

    private static (CombatKnowledgeStatus Status, string? Reason) EvidenceStatus(CombatKnowledgeStatus status,
        bool fresh, long observedGeneration, long currentGeneration, DateTimeOffset? observedAt, bool qualifiedSource)
    {
        if (!qualifiedSource) return (CombatKnowledgeStatus.Unknown, "UnqualifiedSource");
        if (!Enum.IsDefined(status) || status == CombatKnowledgeStatus.Conflict)
            return (CombatKnowledgeStatus.Conflict, "ConflictingSource");
        if (observedGeneration != currentGeneration) return (CombatKnowledgeStatus.Stale, "GenerationMismatch");
        if (status == CombatKnowledgeStatus.Unknown) return (CombatKnowledgeStatus.Unknown, "MissingSource");
        if (status == CombatKnowledgeStatus.Stale || !fresh) return (CombatKnowledgeStatus.Stale, "StaleSource");
        if (observedAt is null) return (CombatKnowledgeStatus.Unknown, "MissingTimestamp");
        // Partial means an incomplete list, not uncertain values for its explicitly present keys.
        return (CombatKnowledgeStatus.Complete, null);
    }

    private static CombatKnowledgeStatus OverallStatus(IReadOnlyList<CharacterDisplayStatRow> rows)
    {
        if (rows.All(row => row.Status == CombatKnowledgeStatus.Complete)) return CombatKnowledgeStatus.Complete;
        if (rows.Any(row => row.Status == CombatKnowledgeStatus.Conflict)) return CombatKnowledgeStatus.Conflict;
        if (rows.Any(row => row.Value is not null)) return CombatKnowledgeStatus.Partial;
        return rows.Any(row => row.Status == CombatKnowledgeStatus.Stale) ? CombatKnowledgeStatus.Stale : CombatKnowledgeStatus.Unknown;
    }

    private static IReadOnlyList<CharacterDisplayForwardRule> CreateRules()
    {
        CharacterDisplayForwardRule Primary(ushort nativeId, string id, string label, bool qualified = true) => new()
        {
            Id = $"character-display-26262663-primary-{id.ToLowerInvariant()}-v1", OutputId = id, LabelStatId = label,
            ClientBuild = QualifiedClientBuild, Category = CharacterDisplayStatCategory.Main,
            FlatInputs = Array.AsReadOnly(new[] { new CharacterDisplayStatTerm(nativeId) }),
            Qualification = qualified ? CharacterDisplayStatQualification.ObservedMatch : CharacterDisplayStatQualification.Unknown,
            Formula = qualified ? $"raw[{nativeId}]" : "unqualified native component",
            Evidence = Array.AsReadOnly(new[] { "historical-enum-current-primary-semantic-localization-crosscheck-2026-11-03",
                qualified ? "explicit-native-primary-panel-vector-2026-11-03" : "missing-native-component-is-not-observed-ui-zero" })
        };
        CharacterDisplayForwardRule Main(string id, string label, CharacterDisplayStatTerm[] terms, ushort ratio,
            CombatRounding rounding, CharacterDisplayStatQualification qualification, string formula) => new()
        {
            Id = $"character-display-26262663-{id.ToLowerInvariant()}-v1", OutputId = id, LabelStatId = label,
            ClientBuild = QualifiedClientBuild, RequiredClassId = "Cleric", Category = CharacterDisplayStatCategory.Main,
            FlatInputs = Array.AsReadOnly(terms), IncreaseBasisPointsInput = ratio, Rounding = rounding,
            Qualification = qualification, Formula = formula,
            Evidence = Array.AsReadOnly(new[] { "current-cleric-panel-component-crosscheck-2026-11-03",
                qualification == CharacterDisplayStatQualification.ControlledChange
                    ? "controlled-component-return-2026-11-03" : "single-state-numeric-match-2026-11-03" })
        };
        CharacterDisplayForwardRule God(ushort nativeId, string id) => new()
        {
            Id = $"character-display-god-{id.ToLowerInvariant()}-observed-2026-11-09-v1", OutputId = id, LabelStatId = id,
            ClientBuild = QualifiedClientBuild, RequiredClassId = "Cleric", Category = CharacterDisplayStatCategory.Main,
            Unit = CombatStatUnit.Points, FlatInputs = Array.AsReadOnly(new[] { new CharacterDisplayStatTerm(nativeId) }),
            Divisor = 1, Rounding = CombatRounding.None, AllowNegative = false,
            Qualification = CharacterDisplayStatQualification.ObservedMatch, Formula = $"raw[{nativeId}]",
            Evidence = Array.AsReadOnly(new[] { "explicit-named-god-panel-vector-2026-11-09",
                "historical-estat-numeric-enum-crosscheck-2026-11-09",
                "current-god-semantic-localization-crosscheck-2026-11-09", "reference-contract-build-scope-only" })
        };
        const CharacterDisplayStatQualification controlled = CharacterDisplayStatQualification.ControlledChange;
        const CharacterDisplayStatQualification observed = CharacterDisplayStatQualification.ObservedMatch;
        var primary = new[]
        {
            Primary(1, "Might", "STR"), Primary(2, "Dexterity", "DEX"),
            Primary(3, "Intelligence", "INT", false), Primary(4, "Constitution", "CON"),
            Primary(5, "Precision", "AGI"), Primary(6, "Willpower", "WIS")
        };
        var gods = new[]
        {
            God(7, "Justice"), God(8, "Freedom"), God(9, "Illusion"), God(10, "Life"), God(11, "Time"),
            God(13, "Destruction"), God(14, "Death"), God(15, "Wisdom"), God(16, "Destiny"), God(17, "Space")
        };
        var main = new[]
        {
            Main("HPMax", "HPMax", [new(193)], 196, CombatRounding.Ceiling, controlled, "ceil(raw[193] * (10000 + raw[196]) / 10000)"),
            Main("MPMax", "MPMax", [new(199)], 197, CombatRounding.Ceiling, controlled, "ceil(raw[199] * (10000 + raw[197]) / 10000)"),
            Main("Attack", "WeaponFixingDamage", [new(317), new(31, .5m), new(33, .5m), new(19)], 425,
                CombatRounding.Floor, observed, "floor((raw[317] + (raw[31] + raw[33]) / 2 + raw[19]) * (10000 + raw[425]) / 10000)"),
            Main("Defense", "ArmorDefense", [new(52), new(307)], 426, CombatRounding.Floor, controlled,
                "floor((raw[52] + raw[307]) * (10000 + raw[426]) / 10000)"),
            Main("Accuracy", "WeaponAccuracy", [new(104), new(318)], 427, CombatRounding.Floor, observed,
                "floor((raw[104] + raw[318]) * (10000 + raw[427]) / 10000)"),
            Main("Evasion", "ArmorEvasion", [new(116), new(312)], 428, CombatRounding.Floor, observed,
                "floor((raw[116] + raw[312]) * (10000 + raw[428]) / 10000)"),
            Main("Critical", "Critical", [new(128)], 429, CombatRounding.Floor, controlled,
                "floor(raw[128] * (10000 + raw[429]) / 10000)"),
            Main("CriticalResist", "CriticalResist", [new(133)], 430, CombatRounding.Floor, observed,
                "floor(raw[133] * (10000 + raw[430]) / 10000)")
        };
        var sub = CharacterDisplaySubstatRules.Create();
        return Array.AsReadOnly(primary.Concat(gods).Concat(main).Concat(sub.Where(rule => rule.Category == CharacterDisplayStatCategory.Main)
                .OrderBy(rule => rule.OutputId == "CombatSpeed" ? 0 : 1))
            .Concat(sub.Where(rule => rule.Category == CharacterDisplayStatCategory.Sub)).ToArray());
    }
}
