using System.Collections.Immutable;

namespace Soulcrest.Core.Achievements;

public enum AchievementDependencyOperator { Unknown, All, Any, AtLeast }
public enum AchievementDependencyIssueKind
{
    UnqualifiedRule, InvalidKey, DuplicateDefinition, InvalidChildren, DuplicateChild, InvalidThreshold,
    UnknownOperator, Cycle
}

/// <summary>Only explicit, independently qualified child edges. No parsing of tokens or arguments.</summary>
public sealed record AchievementDependencyRule(string Key, ImmutableArray<string> Children,
    AchievementDependencyOperator Operator)
{
    public bool IsQualified { get; init; }
    public int RequiredCount { get; init; }
}

public sealed record AchievementDependencyIssue(AchievementDependencyIssueKind Kind, string Key);
public sealed record AchievementDependencyEvaluation(ImmutableDictionary<string, AchievementTruth> Values,
    ImmutableArray<AchievementDependencyIssue> Issues)
{
    public bool HasCycles => Issues.Any(issue => issue.Kind == AchievementDependencyIssueKind.Cycle);
}

/// <summary>
/// Three-valued evaluation over explicit edges. Independently qualified own facts are primary;
/// dependency deductions fill only unknown facts. Iterative graph walks also support deep catalogues.
/// </summary>
public static class AchievementDependencyEvaluator
{
    public static AchievementDependencyEvaluation Evaluate(ImmutableArray<AchievementDependencyRule> definitions,
        ImmutableDictionary<string, AchievementTruth>? qualifiedFacts = null)
    {
        var issues = ImmutableArray.CreateBuilder<AchievementDependencyIssue>();
        var rules = new Dictionary<string, AchievementDependencyRule>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in definitions.IsDefault ? ImmutableArray<AchievementDependencyRule>.Empty : definitions)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Key))
            {
                issues.Add(new(AchievementDependencyIssueKind.InvalidKey, ""));
                continue;
            }
            if (!rules.TryAdd(rule.Key, rule))
            {
                invalid.Add(rule.Key);
                issues.Add(new(AchievementDependencyIssueKind.DuplicateDefinition, rule.Key));
                continue;
            }
            if (!rule.IsQualified)
            {
                invalid.Add(rule.Key);
                issues.Add(new(AchievementDependencyIssueKind.UnqualifiedRule, rule.Key));
            }
            if (rule.Children.IsDefaultOrEmpty || rule.Children.Any(string.IsNullOrWhiteSpace))
            {
                invalid.Add(rule.Key);
                issues.Add(new(AchievementDependencyIssueKind.InvalidChildren, rule.Key));
            }
            else if (rule.Children.Distinct(StringComparer.Ordinal).Count() != rule.Children.Length)
            {
                invalid.Add(rule.Key);
                issues.Add(new(AchievementDependencyIssueKind.DuplicateChild, rule.Key));
            }
            if (!Enum.IsDefined(rule.Operator) || rule.Operator == AchievementDependencyOperator.Unknown)
            {
                invalid.Add(rule.Key);
                issues.Add(new(AchievementDependencyIssueKind.UnknownOperator, rule.Key));
            }
            if (rule.Operator == AchievementDependencyOperator.AtLeast
                && (rule.Children.IsDefaultOrEmpty || rule.RequiredCount <= 0 || rule.RequiredCount > rule.Children.Length))
            {
                invalid.Add(rule.Key);
                issues.Add(new(AchievementDependencyIssueKind.InvalidThreshold, rule.Key));
            }
        }

        var keys = rules.Keys.Order(StringComparer.Ordinal).ToArray();
        var adjacency = keys.ToDictionary(key => key, key => invalid.Contains(key) ? Array.Empty<string>()
            : rules[key].Children.Where(rules.ContainsKey).ToArray(), StringComparer.Ordinal);
        var cycles = FindCycleMembers(keys, adjacency);
        foreach (var key in cycles.Order(StringComparer.Ordinal))
            issues.Add(new(AchievementDependencyIssueKind.Cycle, key));

        var values = ImmutableDictionary.CreateBuilder<string, AchievementTruth>(StringComparer.Ordinal);
        if (qualifiedFacts is not null)
            foreach (var pair in qualifiedFacts)
                if (!string.IsNullOrWhiteSpace(pair.Key)) values[pair.Key] = ValidTruth(pair.Value);
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var direct) && direct != AchievementTruth.Unknown) continue;
            if (invalid.Contains(key) || cycles.Contains(key)) values[key] = AchievementTruth.Unknown;
        }

        var evaluated = new HashSet<string>(values.Where(pair => pair.Value != AchievementTruth.Unknown
            || invalid.Contains(pair.Key) || cycles.Contains(pair.Key)).Select(pair => pair.Key), StringComparer.Ordinal);
        foreach (var root in keys)
        {
            if (evaluated.Contains(root)) continue;
            var stack = new Stack<(string Key, bool Exit)>();
            stack.Push((root, false));
            while (stack.TryPop(out var item))
            {
                if (evaluated.Contains(item.Key)) continue;
                var rule = rules[item.Key];
                if (!item.Exit)
                {
                    stack.Push((item.Key, true));
                    foreach (var child in rule.Children)
                    {
                        if (rules.ContainsKey(child) && !evaluated.Contains(child)) stack.Push((child, false));
                        else if (!values.ContainsKey(child)) values[child] = AchievementTruth.Unknown;
                    }
                }
                else
                {
                    values[item.Key] = Combine(rule, rule.Children.Select(child => values.GetValueOrDefault(child, AchievementTruth.Unknown)));
                    evaluated.Add(item.Key);
                }
            }
        }
        return new(values.ToImmutable(), issues.ToImmutable());
    }

    private static AchievementTruth ValidTruth(AchievementTruth value) => Enum.IsDefined(value) ? value : AchievementTruth.Unknown;

    private static AchievementTruth Combine(AchievementDependencyRule rule, IEnumerable<AchievementTruth> children)
    {
        var knownTrue = 0;
        var unknown = 0;
        var count = 0;
        foreach (var truth in children)
        {
            count++;
            if (truth == AchievementTruth.True) knownTrue++;
            else if (truth == AchievementTruth.Unknown) unknown++;
        }
        var required = rule.Operator switch
        {
            AchievementDependencyOperator.All => count,
            AchievementDependencyOperator.Any => 1,
            _ => rule.RequiredCount
        };
        return knownTrue >= required ? AchievementTruth.True : knownTrue + unknown < required
            ? AchievementTruth.False : AchievementTruth.Unknown;
    }

    // Kosaraju with explicit stacks: no recursion depth or accidental truth from cyclic definitions.
    private static HashSet<string> FindCycleMembers(string[] keys, Dictionary<string, string[]> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var finished = new List<string>();
        foreach (var root in keys)
        {
            if (visited.Contains(root)) continue;
            var stack = new Stack<(string Key, bool Exit)>();
            stack.Push((root, false));
            while (stack.TryPop(out var item))
            {
                if (item.Exit) { finished.Add(item.Key); continue; }
                if (!visited.Add(item.Key)) continue;
                stack.Push((item.Key, true));
                foreach (var child in adjacency[item.Key])
                    if (!visited.Contains(child)) stack.Push((child, false));
            }
        }
        var reverse = keys.ToDictionary(key => key, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var pair in adjacency)
            foreach (var child in pair.Value) reverse[child].Add(pair.Key);
        visited.Clear();
        var cycles = new HashSet<string>(StringComparer.Ordinal);
        for (var index = finished.Count - 1; index >= 0; index--)
        {
            var root = finished[index];
            if (!visited.Add(root)) continue;
            var component = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.TryPop(out var key))
            {
                component.Add(key);
                foreach (var parent in reverse[key])
                    if (visited.Add(parent)) stack.Push(parent);
            }
            if (component.Count > 1 || adjacency[root].Contains(root, StringComparer.Ordinal)) cycles.UnionWith(component);
        }
        return cycles;
    }
}
