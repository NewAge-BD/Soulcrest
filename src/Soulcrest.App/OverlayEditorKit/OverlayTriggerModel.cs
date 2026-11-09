using System.Text.Json.Serialization;

namespace Soulcrest.App.OverlayEditorKit;

[JsonConverter(typeof(JsonStringEnumConverter<OverlayTriggerArea>))]
public enum OverlayTriggerArea { OpenWorld, Abyss, Instances }

[JsonConverter(typeof(JsonStringEnumConverter<OverlayTriggerRequirement>))]
public enum OverlayTriggerRequirement { Any, Required, Excluded }

/// <summary>Additional conditions of one editor window; no rule can authorize a data source or native anchor.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OverlayTriggerRules
{
    public IReadOnlyList<OverlayTriggerArea> Areas { get; init; } = [];
    public OverlayTriggerRequirement Foreground { get; init; }
    public OverlayTriggerRequirement SessionActive { get; init; }
    public OverlayTriggerRequirement WorldMap { get; init; }
    public OverlayTriggerRequirement EscMenu { get; init; }
    public OverlayTriggerRequirement MinimapMissing { get; init; }

    public static OverlayTriggerRules? Normalize(OverlayTriggerRules? rules)
    {
        if (rules is null) return null;
        if (!ValidValues(rules)) throw new ArgumentException("Die Sichtbarkeitsbedingungen sind ungültig.", nameof(rules));
        var areas = rules.Areas.Distinct().OrderBy(area => area).ToArray();
        return rules with { Areas = Array.AsReadOnly(areas) };
    }

    /// <summary>Compares meaning rather than collection identity; null and unconstrained rules are equivalent.</summary>
    public static bool Equivalent(OverlayTriggerRules? left, OverlayTriggerRules? right)
    {
        if (ReferenceEquals(left, right)) return true;
        left ??= new();
        right ??= new();
        return ValidValues(left) && ValidValues(right)
            && left.Foreground == right.Foreground && left.SessionActive == right.SessionActive
            && left.WorldMap == right.WorldMap && left.EscMenu == right.EscMenu && left.MinimapMissing == right.MinimapMissing
            && left.Areas.Distinct().Order().SequenceEqual(right.Areas.Distinct().Order());
    }

    internal static void ValidateStored(OverlayTriggerRules? rules)
    {
        if (rules is not null && !Valid(rules))
            throw new InvalidDataException("Die gespeicherten Sichtbarkeitsbedingungen sind ungültig.");
    }

    internal static bool Valid(OverlayTriggerRules rules) => ValidValues(rules)
        && rules.Areas.Count == rules.Areas.Distinct().Count();

    private static bool ValidValues(OverlayTriggerRules rules) => rules.Areas is not null
        && rules.Areas.All(area => Enum.IsDefined(area))
        && Enum.IsDefined(rules.Foreground) && Enum.IsDefined(rules.SessionActive)
        && Enum.IsDefined(rules.WorldMap) && Enum.IsDefined(rules.EscMenu) && Enum.IsDefined(rules.MinimapMissing);
}

/// <summary>Only supplied observations. Null is unknown, never an observed absence or a guessed area.</summary>
public sealed record OverlayTriggerContextSnapshot(OverlayTriggerArea? Area = null, bool? Foreground = null,
    bool? SessionActive = null, bool? WorldMap = null, bool? EscMenu = null, bool? MinimapMissing = null);

/// <summary>Pure additional filter. It neither obtains observations nor changes existing overlay permissions.</summary>
public static class OverlayTriggerPolicy
{
    public static bool Matches(OverlayTriggerRules? rules, OverlayTriggerContextSnapshot context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (rules is null) return true;
        if (!OverlayTriggerRules.Valid(rules)) return false;
        return (rules.Areas.Count == 0 || context.Area is { } area && Enum.IsDefined(area) && rules.Areas.Contains(area))
            && Match(rules.Foreground, context.Foreground)
            && Match(rules.SessionActive, context.SessionActive)
            && Match(rules.WorldMap, context.WorldMap)
            && Match(rules.EscMenu, context.EscMenu)
            && Match(rules.MinimapMissing, context.MinimapMissing);
    }

    private static bool Match(OverlayTriggerRequirement rule, bool? observed) => rule switch
    {
        OverlayTriggerRequirement.Any => true,
        OverlayTriggerRequirement.Required => observed == true,
        OverlayTriggerRequirement.Excluded => observed == false,
        _ => false,
    };
}
