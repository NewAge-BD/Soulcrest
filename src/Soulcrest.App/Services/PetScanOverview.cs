using Soulcrest.Core.Pets;

namespace Soulcrest.App.Services;

public enum PetScanEntryStatus { Ready, UnknownPet, UnreadableValue, ConflictingValue, UncertainPet }

public sealed record PetScanOverviewRow(ScanEntry Entry, string Name, PetScanEntryStatus Status)
{
    // Preserve the existing explicit-apply policy: uncertainty needs review, but does not
    // prevent the user from accepting a named pet with a readable value.
    public bool Eligible => Entry.PetId is not null && Entry.Reading is not null;
    public bool NeedsReview => Status != PetScanEntryStatus.Ready;
}

public sealed record PetScanOverviewSnapshot(int Total, int Ready, int Review, int Selected,
    int Unlocked, IReadOnlyList<PetScanOverviewRow> Rows);

/// <summary>
/// User request 2026-10-11: make scanned pets easy to review and filter. Operates on one
/// service snapshot; filtering never changes selection, recognition, or saved progress.
/// </summary>
public static class PetScanOverview
{
    public static PetScanOverviewSnapshot Build(IReadOnlyList<ScanEntry> entries, PetCatalog catalog,
        string language, string search = "", string status = "all")
    {
        var rows = entries.Select(entry => new PetScanOverviewRow(entry,
            entry.PetId is { } id ? catalog.Find(id)?.DisplayName(language) ?? id : entry.SuggestedName ?? "",
            Status(entry))).ToArray();
        var terms = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var visible = rows.Where(row => status switch
            {
                "review" => row.NeedsReview,
                "ready" => !row.NeedsReview,
                "selected" => row.Eligible && row.Entry.Apply,
                _ => true,
            })
            .Where(row => Matches(row.Entry, catalog, terms))
            .OrderByDescending(row => row.NeedsReview)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Entry.Key, StringComparer.Ordinal)
            .ToArray();
        var ready = rows.Count(row => !row.NeedsReview);
        return new(entries.Count, ready, entries.Count - ready,
            rows.Count(row => row.Eligible && row.Entry.Apply),
            entries.Count(entry => entry.Reading?.Level >= 1), visible);
    }

    private static PetScanEntryStatus Status(ScanEntry entry) => entry.HasValueConflict
        ? PetScanEntryStatus.ConflictingValue
        : entry.PetId is null ? PetScanEntryStatus.UnknownPet
        : entry.Reading is null ? PetScanEntryStatus.UnreadableValue
        : entry.Confidence is ScanConfidence.Unknown or ScanConfidence.Uncertain ? PetScanEntryStatus.UncertainPet
        : PetScanEntryStatus.Ready;

    private static bool Matches(ScanEntry entry, PetCatalog catalog, IReadOnlyList<string> terms)
    {
        var pet = entry.PetId is { } id ? catalog.Find(id) : null;
        return terms.All(term => Contains(pet?.En, term) || Contains(pet?.De, term)
            || Contains(entry.PetId, term) || Contains(entry.SuggestedName, term));
    }

    private static bool Contains(string? text, string term) =>
        text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
}
