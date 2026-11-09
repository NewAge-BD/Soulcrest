using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Components;

public enum CombatTitleRosterEstimateStatus { Unavailable, Estimated }
public enum CombatTitleRosterEstimateReason
{
    UnknownOwnership, IncompleteOwnership, EmptyOwnership, InvalidOwnership,
    UnknownTitleId, NoMatchingGroup, AmbiguousGroup, CandidateReconstruction
}

/// <summary>A candidate reconstruction, never a native availability or ownership observation.</summary>
public sealed record CombatTitleRosterEstimateResult(CombatTitleRosterEstimateStatus Status,
    CombatTitleRosterEstimateReason Reason, string? GroupKey, IReadOnlySet<int> RosterIds, IReadOnlySet<int> MissingIds)
{
    public bool IsEstimated => Status == CombatTitleRosterEstimateStatus.Estimated;
    public int? TotalCount => IsEstimated ? RosterIds.Count : null;
}

/// <summary>
/// Estimates one catalog scope only when it covers an entire observed ownership list unambiguously.
/// The metadata selectors remain candidates. This class grants no availability, character-faction,
/// source-effect, combat-score or additional ownership authority.
/// </summary>
public sealed class CombatTitleRosterEstimate
{
    private static readonly FrozenSet<int> Empty = Array.Empty<int>().ToFrozenSet();
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<CombatTitleRosterEstimate> Embedded = new(LoadEmbedded);
    private readonly IReadOnlyList<CandidateGroup> _groups;
    private readonly FrozenSet<int> _knownIds;

    public static CombatTitleRosterEstimate Default => Embedded.Value;
    public int MetadataBuild { get; }

    private CombatTitleRosterEstimate(int metadataBuild, IReadOnlyList<GroupFile> groups)
    {
        if (groups.Count is < 1 or > 64) throw new InvalidDataException("Invalid title estimate groups.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<CandidateGroup>();
        foreach (var group in groups)
        {
            if (group is null || !ValidKey(group.Key) || !keys.Add(group.Key))
                throw new InvalidDataException("Invalid or duplicate title estimate group.");
            var baseline = Ids(group.BaseIds);
            var conditional = Ids(group.ConditionalOwnedIds);
            if (baseline.Count == 0 || baseline.Overlaps(conditional))
                throw new InvalidDataException("Invalid title estimate membership.");
            entries.Add(new(group.Key, baseline, conditional, baseline.Union(conditional).ToFrozenSet()));
        }
        MetadataBuild = metadataBuild;
        _groups = Array.AsReadOnly(entries.ToArray());
        _knownIds = entries.SelectMany(group => group.ScopeIds).ToFrozenSet();
    }

    public CombatTitleRosterEstimateResult Estimate(IEnumerable<int>? ownedIds, bool ownershipComplete)
    {
        if (ownedIds is null) return Unavailable(CombatTitleRosterEstimateReason.UnknownOwnership);
        if (!ownershipComplete) return Unavailable(CombatTitleRosterEstimateReason.IncompleteOwnership);
        var owned = ownedIds.ToHashSet();
        if (owned.Count == 0) return Unavailable(CombatTitleRosterEstimateReason.EmptyOwnership);
        if (owned.Any(id => id <= 0)) return Unavailable(CombatTitleRosterEstimateReason.InvalidOwnership);
        if (!owned.IsSubsetOf(_knownIds)) return Unavailable(CombatTitleRosterEstimateReason.UnknownTitleId);
        var matching = _groups.Where(group => owned.IsSubsetOf(group.ScopeIds)).ToArray();
        if (matching.Length == 0) return Unavailable(CombatTitleRosterEstimateReason.NoMatchingGroup);
        if (matching.Length != 1) return Unavailable(CombatTitleRosterEstimateReason.AmbiguousGroup);
        var group = matching[0];
        var roster = group.BaseIds.Union(owned.Where(group.ConditionalOwnedIds.Contains)).ToFrozenSet();
        return new(CombatTitleRosterEstimateStatus.Estimated, CombatTitleRosterEstimateReason.CandidateReconstruction,
            group.Key, roster, roster.Except(owned).ToFrozenSet());
    }

    private static CombatTitleRosterEstimateResult Unavailable(CombatTitleRosterEstimateReason reason) =>
        new(CombatTitleRosterEstimateStatus.Unavailable, reason, null, Empty, Empty);

    internal static CombatTitleRosterEstimate FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file is not { Schema: 1, MetadataBuild: > 0, Qualification: "Candidate",
                EstablishesNativeAvailability: false, Groups: not null })
                throw new InvalidDataException("Unsupported title estimate schema.");
            return new(file.MetadataBuild, file.Groups);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid title estimate data.", error); }
    }

    private static FrozenSet<int> Ids(int[]? values)
    {
        if (values is null || values.Length > 100000 || values.Any(id => id <= 0))
            throw new InvalidDataException("Invalid title estimate IDs.");
        var result = values.ToFrozenSet();
        if (result.Count != values.Length) throw new InvalidDataException("Duplicate title estimate ID.");
        return result;
    }

    private static bool ValidKey(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static CombatTitleRosterEstimate LoadEmbedded()
    {
        using var stream = typeof(CombatTitleRosterEstimate).Assembly.GetManifestResourceStream("Soulcrest.CombatTitleRosterEstimate.json")
            ?? throw new InvalidDataException("Missing embedded title estimate data.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record CandidateGroup(string Key, FrozenSet<int> BaseIds,
        FrozenSet<int> ConditionalOwnedIds, FrozenSet<int> ScopeIds);
    private sealed record GroupFile
    {
        public required string Key { get; init; }
        public required int[] BaseIds { get; init; }
        public required int[] ConditionalOwnedIds { get; init; }
    }
    private sealed record CatalogFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required string Qualification { get; init; }
        public required bool EstablishesNativeAvailability { get; init; }
        public required GroupFile[] Groups { get; init; }
    }
}
