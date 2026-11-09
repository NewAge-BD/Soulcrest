using Soulcrest.App.Services;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Components;

public sealed record CombatPetEffectTotal(CombatTitlePassiveKey Key, string LabelDe, string LabelEn,
    decimal? Value, decimal? KnownSubtotal, CombatKnowledgeStatus Status);

public sealed record CombatPetCategorySummary(string Genus, int PetCount, int? CompletedStages,
    int MaximumStages, decimal? CompletionPercent, CombatKnowledgeStatus Status);

public sealed record CombatPetsSnapshot
{
    public CombatKnowledgeStatus Status { get; init; } = CombatKnowledgeStatus.Unknown;
    public bool IsHistorical { get; init; }
    public bool IncludesManualValues { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public IReadOnlyList<CombatPetEffectTotal> OwnedEffects { get; init; } = [];
    public IReadOnlyList<CombatPetEffectTotal> MissingEffects { get; init; } = [];
    public IReadOnlyList<CombatPetCategorySummary> Categories { get; init; } = [];
    public static CombatPetsSnapshot Empty { get; } = CombatPetsPresentation.Create(PetCollectionSnapshot.Empty);
}

/// <summary>Owned pet effects only. Genus Insight, souls and active companions do not change acquired pet levels.</summary>
public static class CombatPetsPresentation
{
    public static IReadOnlyList<string> Genera { get; } = Array.AsReadOnly(new[] { "cogni", "fera", "natura", "varian" });

    public static string? GenusForStat(CombatTitlePassiveKey key)
    {
        var matches = Genera.Where(genus => CombatPetOwnedEffectCatalog.Effects(genus, CombatPetOwnedEffectCatalog.MaximumLevel)!
            .Any(effect => CombatTitlesPresentation.Key(effect) == key)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static CombatPetsSnapshot Create(PetCollectionSnapshot collection)
    {
        var owned = new List<CombatPetEffectTotal>();
        var missing = new List<CombatPetEffectTotal>();
        var categories = new List<CombatPetCategorySummary>();
        var relevant = collection.Pets.Where(pet => Genera.Contains(pet.Genus, StringComparer.Ordinal)).ToArray();
        foreach (var genus in Genera)
        {
            var pets = relevant.Where(pet => pet.Genus == genus).ToArray();
            var known = pets.Where(IsKnown).ToArray();
            var complete = pets.Length > 0 && pets.Length == known.Length;
            var status = complete ? CombatKnowledgeStatus.Complete
                : known.Length > 0 ? CombatKnowledgeStatus.Partial : CombatKnowledgeStatus.Unknown;
            var maximumStages = pets.Length * CombatPetOwnedEffectCatalog.MaximumLevel;
            int? completed = complete ? pets.Sum(pet => pet.Level) : null;
            categories.Add(new(genus, pets.Length, completed, maximumStages,
                completed is { } current && maximumStages > 0 ? 100m * current / maximumStages : null, status));
            if (pets.Length == 0) continue;
            foreach (var effect in CombatPetOwnedEffectCatalog.Effects(genus, CombatPetOwnedEffectCatalog.MaximumLevel)!)
            {
                var key = CombatTitlesPresentation.Key(effect);
                var earned = known.Sum(pet => CombatPetOwnedEffectCatalog.Effects(genus, pet.Level)!
                    .Where(row => CombatTitlesPresentation.Key(row) == key).Sum(row => row.Value ?? 0));
                var remaining = known.Length * effect.Value!.Value - earned;
                owned.Add(new(key, effect.LabelDe, effect.LabelEn, complete ? earned : null,
                    known.Length > 0 ? earned : null, status));
                missing.Add(new(key, effect.LabelDe, effect.LabelEn, complete ? remaining : null,
                    known.Length > 0 ? remaining : null, status));
            }
        }
        return new()
        {
            Status = categories.All(row => row.Status == CombatKnowledgeStatus.Complete) ? CombatKnowledgeStatus.Complete
                : relevant.Any(IsKnown) ? CombatKnowledgeStatus.Partial : CombatKnowledgeStatus.Unknown,
            IsHistorical = relevant.Any(pet => IsKnown(pet) && !pet.IsCurrent && !pet.IsManual),
            IncludesManualValues = relevant.Any(pet => pet.IsManual),
            ObservedAt = relevant.Where(IsKnown).Select(pet => pet.ObservedAt).Max(),
            OwnedEffects = Array.AsReadOnly(owned.ToArray()), MissingEffects = Array.AsReadOnly(missing.ToArray()),
            Categories = Array.AsReadOnly(categories.ToArray())
        };
    }

    private static bool IsKnown(PetCollectionEntry pet) => pet.IsKnown && pet.Level is >= 0 and <= CombatPetOwnedEffectCatalog.MaximumLevel;
}
