namespace Soulcrest.Core.Daevanion;

/// <summary>Moves relative to the complete class list, even when the UI displays a filtered subset.</summary>
public static class DaevanionPriorityOrdering
{
    public static DaevanionOptimizationRequest Apply(DaevanionOptimizationRequest request,
        DaevanionPriorityKind kind, string key, string? beforeKey, string? afterKey, bool ignored,
        DaevanionBudgetPool? pool = null, bool required = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ignored && required) throw new ArgumentException("A priority cannot be both ignored and required.");
        if (pool is { } selectedPool)
        {
            var settings = DaevanionPoolSettings.ForPool(request, selectedPool);
            var scoped = request with { Priorities = settings.Priorities, Weights = settings.Weights };
            var moved = Apply(scoped, kind, key, beforeKey, afterKey, ignored, required: required);
            return ReferenceEquals(scoped, moved) ? request
                : DaevanionPoolSettings.WithPool(request, settings with { Priorities = moved.Priorities });
        }
        if (beforeKey is not null && afterKey is not null || beforeKey == key || afterKey == key) return request;
        var group = request.Priorities.Where(item => item.Kind == kind).ToList();
        var source = group.FirstOrDefault(item => item.Key == key);
        if (source is null) return request;
        var anchor = beforeKey ?? afterKey;
        bool InSection(DaevanionPriority item) => item.Ignored == ignored && item.Required == required;
        if (anchor is not null && !group.Any(item => item.Key == anchor && InSection(item))) return request;
        group.Remove(source);
        var insertion = anchor is null ? group.FindLastIndex(InSection) + 1
            : group.FindIndex(item => item.Key == anchor) + (afterKey is null ? 0 : 1);
        if (anchor is null && insertion == 0) insertion = group.Count;
        group.Insert(insertion, source with { Ignored = ignored, Required = required });
        var original = request.Priorities.Where(item => item.Kind == kind).ToArray();
        if (new[] { (Ignored: false, Required: true), (Ignored: false, Required: false), (Ignored: true, Required: false) }
            .All(section => group.Where(item => item.Ignored == section.Ignored && item.Required == section.Required)
                .SequenceEqual(original.Where(item => item.Ignored == section.Ignored && item.Required == section.Required)))) return request;
        var position = 0;
        var priorities = request.Priorities.Select(item => item.Kind == kind ? group[position++] : item).ToArray();
        return priorities.SequenceEqual(request.Priorities) ? request : request with { Priorities = Array.AsReadOnly(priorities) };
    }
}
