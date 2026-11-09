using System.Collections.Immutable;
using Soulcrest.Core.Achievements;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class AchievementDependencyEvaluatorTests
{
    [Theory]
    [InlineData(AchievementDependencyOperator.All)]
    [InlineData(AchievementDependencyOperator.Any)]
    [InlineData(AchievementDependencyOperator.AtLeast)]
    public void EveryThreeValuedCombinationMatchesAllConsistentBooleanCompletions(AchievementDependencyOperator operation)
    {
        foreach (var first in Enum.GetValues<AchievementTruth>())
        foreach (var second in Enum.GetValues<AchievementTruth>())
        foreach (var third in Enum.GetValues<AchievementTruth>())
        {
            var rule = Rule("parent", operation, "a", "b", "c") with { RequiredCount = 2 };
            var facts = ImmutableDictionary<string, AchievementTruth>.Empty.Add("a", first).Add("b", second).Add("c", third);
            var result = AchievementDependencyEvaluator.Evaluate([rule], facts);
            var required = operation == AchievementDependencyOperator.All ? 3 : operation == AchievementDependencyOperator.Any ? 1 : 2;
            Assert.Equal(Enumerate([first, second, third], required), result.Values["parent"]);
            Assert.Empty(result.Issues);
        }
    }

    [Fact]
    public void AllMembersOfOverlappingCyclesAreDetectedAndDoNotGrantCompletion()
    {
        var rules = ImmutableArray.Create(
            Rule("a", AchievementDependencyOperator.All, "b", "c"),
            Rule("b", AchievementDependencyOperator.All, "d"),
            Rule("c", AchievementDependencyOperator.All, "d"),
            Rule("d", AchievementDependencyOperator.All, "a"),
            Rule("tail", AchievementDependencyOperator.All, "a"),
            Rule("independent-any", AchievementDependencyOperator.Any, "tail", "known"));
        var result = AchievementDependencyEvaluator.Evaluate(rules,
            ImmutableDictionary<string, AchievementTruth>.Empty.Add("known", AchievementTruth.True));
        Assert.True(result.HasCycles);
        Assert.Equal(new[] { "a", "b", "c", "d" }, result.Issues.Where(issue => issue.Kind == AchievementDependencyIssueKind.Cycle)
            .Select(issue => issue.Key).Order(StringComparer.Ordinal));
        Assert.All(new[] { "a", "b", "c", "d", "tail" }, key => Assert.Equal(AchievementTruth.Unknown, result.Values[key]));
        Assert.Equal(AchievementTruth.True, result.Values["independent-any"]);
    }

    [Fact]
    public void SelfReferenceIsACycleWhilePrimaryOwnFactsRemainIndependentlyKnown()
    {
        var rule = Rule("self", AchievementDependencyOperator.All, "self");
        var result = AchievementDependencyEvaluator.Evaluate([rule]);
        Assert.True(result.HasCycles);
        Assert.Equal(AchievementTruth.Unknown, result.Values["self"]);
        result = AchievementDependencyEvaluator.Evaluate([rule],
            ImmutableDictionary<string, AchievementTruth>.Empty.Add("self", AchievementTruth.True));
        Assert.True(result.HasCycles);
        Assert.Equal(AchievementTruth.True, result.Values["self"]);
    }

    [Fact]
    public void DeepExplicitDependenciesDoNotUseTheCallStack()
    {
        var rules = Enumerable.Range(0, 10_000).Select(index =>
            Rule($"node-{index:D5}", AchievementDependencyOperator.All, index == 0 ? "known" : $"node-{index - 1:D5}"))
            .ToImmutableArray();
        var result = AchievementDependencyEvaluator.Evaluate(rules,
            ImmutableDictionary<string, AchievementTruth>.Empty.Add("known", AchievementTruth.True));
        Assert.Equal(AchievementTruth.True, result.Values["node-09999"]);
        Assert.Equal(10_001, result.Values.Count);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void RepeatedChildCannotCountTwiceForAThreshold()
    {
        var rule = Rule("parent", AchievementDependencyOperator.AtLeast, "same", "same") with { RequiredCount = 2 };
        var result = AchievementDependencyEvaluator.Evaluate([rule],
            ImmutableDictionary<string, AchievementTruth>.Empty.Add("same", AchievementTruth.True));
        Assert.Equal(AchievementTruth.Unknown, result.Values["parent"]);
        Assert.Contains(result.Issues, issue => issue.Kind == AchievementDependencyIssueKind.DuplicateChild);
    }

    [Fact]
    public void UnqualifiedDuplicateAndEmptyDefinitionsRemainUnknown()
    {
        var unqualified = Rule("unqualified", AchievementDependencyOperator.All, "known") with { IsQualified = false };
        var duplicate = Rule("duplicate", AchievementDependencyOperator.All, "known");
        var empty = Rule("empty", AchievementDependencyOperator.All);
        var defaultChildren = new AchievementDependencyRule("default", default, AchievementDependencyOperator.AtLeast)
            { IsQualified = true, RequiredCount = 1 };
        var result = AchievementDependencyEvaluator.Evaluate([unqualified, duplicate, duplicate, empty, defaultChildren],
            ImmutableDictionary<string, AchievementTruth>.Empty.Add("known", AchievementTruth.True));
        Assert.All(new[] { "unqualified", "duplicate", "empty", "default" }, key => Assert.Equal(AchievementTruth.Unknown, result.Values[key]));
        Assert.Contains(result.Issues, issue => issue.Kind == AchievementDependencyIssueKind.UnqualifiedRule);
        Assert.Contains(result.Issues, issue => issue.Kind == AchievementDependencyIssueKind.DuplicateDefinition);
        Assert.Contains(result.Issues, issue => issue.Kind == AchievementDependencyIssueKind.InvalidChildren);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    public void InvalidRequiredCountsCannotCreateVacuousCompletion(int required)
    {
        var rule = Rule("parent", AchievementDependencyOperator.AtLeast, "a", "b") with { RequiredCount = required };
        var result = AchievementDependencyEvaluator.Evaluate([rule],
            ImmutableDictionary<string, AchievementTruth>.Empty.Add("a", AchievementTruth.True).Add("b", AchievementTruth.True));
        Assert.Equal(AchievementTruth.Unknown, result.Values["parent"]);
        Assert.Contains(result.Issues, issue => issue.Kind == AchievementDependencyIssueKind.InvalidThreshold);
    }

    [Fact]
    public void MissingFactsRemainUnknownAndNamesDoNotCreateAnyImplicitEdges()
    {
        var rule = Rule("parent", AchievementDependencyOperator.All, "a,b");
        var result = AchievementDependencyEvaluator.Evaluate([rule],
            ImmutableDictionary<string, AchievementTruth>.Empty.Add("a", AchievementTruth.True).Add("b", AchievementTruth.True));
        Assert.Equal(AchievementTruth.Unknown, result.Values["parent"]);
        Assert.Equal(AchievementTruth.Unknown, result.Values["a,b"]);
        Assert.Empty(result.Issues);
    }

    private static AchievementDependencyRule Rule(string key, AchievementDependencyOperator operation, params string[] children) =>
        new(key, children.ToImmutableArray(), operation) { IsQualified = true };

    private static AchievementTruth Enumerate(AchievementTruth[] values, int required)
    {
        var possible = new HashSet<bool>();
        for (var mask = 0; mask < 1 << values.Length; mask++)
        {
            var valid = true;
            var count = 0;
            for (var index = 0; index < values.Length; index++)
            {
                var truth = (mask & 1 << index) != 0;
                if (values[index] != AchievementTruth.Unknown && truth != (values[index] == AchievementTruth.True)) valid = false;
                if (truth) count++;
            }
            if (valid) possible.Add(count >= required);
        }
        return possible.Count != 1 ? AchievementTruth.Unknown : possible.Contains(true) ? AchievementTruth.True : AchievementTruth.False;
    }
}
