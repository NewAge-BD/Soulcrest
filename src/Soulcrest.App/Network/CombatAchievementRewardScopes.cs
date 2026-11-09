using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Network;

/// <summary>
/// Explicit build-bound individual reward-prefix membership. This supplies no counter,
/// completion, group-reward or availability interpretation.
/// </summary>
internal sealed class CombatAchievementRewardScopes
{
    internal const int SupportedMetadataBuild = 26425393;
    private const int MaximumJsonCharacters = 512 * 1024;
    private const int MaximumFamilies = 10000;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<CombatAchievementRewardScopes> Embedded = new(LoadEmbedded);
    internal static CombatAchievementRewardScopes Default => Embedded.Value;
    internal int MetadataBuild { get; }
    internal IReadOnlyDictionary<int, byte> Families { get; }

    private CombatAchievementRewardScopes(int build, Dictionary<int, byte> families)
    {
        MetadataBuild = build;
        Families = families.ToFrozenDictionary();
    }

    internal bool Allows(int familyId, byte step) => step > 0
        && Families.TryGetValue(familyId, out var maximum) && step <= maximum;

    internal static CombatAchievementRewardScopes FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonCharacters)
            throw new InvalidDataException("Achievement reward scopes exceed the size limit.");
        try
        {
            var file = JsonSerializer.Deserialize<ScopeFile>(json, Options);
            if (file is not { Schema: 1, MetadataBuild: SupportedMetadataBuild,
                    Families: { Length: > 0 and <= MaximumFamilies } })
                throw new InvalidDataException("Unsupported achievement reward scopes.");
            var families = new Dictionary<int, byte>();
            foreach (var family in file.Families)
                if (family is null || family.Id <= 0 || family.MaxOrdinal is < 1 or > byte.MaxValue
                    || !families.TryAdd(family.Id, (byte)family.MaxOrdinal))
                    throw new InvalidDataException("Invalid or duplicate achievement reward scope.");
            return new(file.MetadataBuild, families);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid achievement reward scope data.", error);
        }
    }

    private static CombatAchievementRewardScopes LoadEmbedded()
    {
        using var stream = typeof(CombatAchievementRewardScopes).Assembly
            .GetManifestResourceStream("Soulcrest.CombatAchievementRewardScopes.json")
            ?? throw new InvalidDataException("Missing achievement reward scopes.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record ScopeFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required FamilyFile?[] Families { get; init; }
    }
    private sealed record FamilyFile
    {
        public required int Id { get; init; }
        public required int MaxOrdinal { get; init; }
    }
}
