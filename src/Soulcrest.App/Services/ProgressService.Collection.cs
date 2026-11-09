using Soulcrest.Core.Progress;

namespace Soulcrest.App.Services;

/// <summary>One displayed entry of the global collection, independent of any character or server profile.</summary>
public sealed record PetCollectionEntry(string Id, string Genus, int Level, bool IsKnown,
    bool IsCurrent, bool IsManual, DateTimeOffset? ObservedAt);

public sealed record PetCollectionSnapshot(IReadOnlyList<PetCollectionEntry> Pets)
{
    public static PetCollectionSnapshot Empty { get; } = new(Array.AsReadOnly(Array.Empty<PetCollectionEntry>()));
}

public sealed partial class ProgressService
{
    /// <summary>
    /// Atomically reads the same verified roster and acquired/manual levels displayed by the main Pets tab.
    /// Historical values remain visible without becoming current; this projection never writes progress.
    /// </summary>
    public PetCollectionSnapshot ReadCollectionSnapshot()
    {
        lock (_gate)
        {
            var rows = VerifiedPets.Select(pet =>
            {
                var entry = EntrySnapshot(pet.Id);
                var raw = RawFor(pet.Id);
                var legacyRecorded = raw is null && (entry.Souls > 0 || entry.UpdatedAt != default)
                    && ProfileIds(CanonicalId(pet.Id)).Any(id => _profile.Pets.ContainsKey(id));
                // Stale alone is not a level witness: clearing a manual correction can mark
                // an unobserved zero seed stale without the game ever supplying its level.
                var known = entry.ManualOverride || raw is { LevelObserved: true } || raw is { Level: > 0 } || legacyRecorded;
                // Persisted LevelObserved and a newly bound session cannot refresh a saved level.
                // Match the main collection's full-baseline freshness; level-only acquisitions stay unconfirmed.
                var current = !entry.ManualOverride && _petContext is not null
                    && raw is { LevelObserved: true, HasBaseline: true } && entry.Freshness == ProgressFreshness.Fresh;
                return new PetCollectionEntry(pet.Id, pet.Genus.ToLowerInvariant(), Level(pet.Id), known, current,
                    entry.ManualOverride, entry.UpdatedAt == default ? null : entry.UpdatedAt);
            }).ToArray();
            return new(Array.AsReadOnly(rows));
        }
    }
}
