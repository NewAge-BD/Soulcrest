using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Soulcrest.App.Services;

/// <summary>A static point milestone. This does not assert a current competitive class rank.</summary>
public sealed record AbyssRankStage(string Id, string NameDe, string NameEn, long Threshold, string IconAsset)
{
    public string Name(string language = "en") => language == "de" ? NameDe : NameEn;
}

/// <summary>Offline bundled original labels, point thresholds and explicit icon bindings.</summary>
public sealed class AbyssRankCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Regex SafeId = new(@"\Arank-[0-9]{2}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SafeAsset = new(@"\Aabyss-rank-icons/rank-[0-9]{2}\.png\z", RegexOptions.CultureInvariant);
    private static readonly Lazy<AbyssRankCatalog> Embedded = new(LoadEmbedded);

    public static AbyssRankCatalog Default => Embedded.Value;
    /// <summary>Strictly ascending point thresholds, beginning at zero.</summary>
    public IReadOnlyList<AbyssRankStage> Stages { get; }

    private AbyssRankCatalog(AbyssRankStage[] stages)
    {
        if (stages.Length is < 1 or > 100)
            throw new InvalidDataException("Invalid Abyss point-stage count.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var assets = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < stages.Length; index++)
        {
            var stage = stages[index];
            if (stage is null || stage.Id is null || !SafeId.IsMatch(stage.Id)
                || string.IsNullOrWhiteSpace(stage.NameDe) || string.IsNullOrWhiteSpace(stage.NameEn)
                || stage.IconAsset is null || !SafeAsset.IsMatch(stage.IconAsset)
                || !ids.Add(stage.Id) || !assets.Add(stage.IconAsset)
                || stage.Threshold < 0 || (index == 0 ? stage.Threshold != 0 : stage.Threshold <= stages[index - 1].Threshold))
                throw new InvalidDataException("Invalid or ambiguous Abyss point stage.");
        }
        Stages = Array.AsReadOnly((AbyssRankStage[])stages.Clone());
    }

    public AbyssRankStage? FindStage(long points) => points < 0
        ? null : Stages.LastOrDefault(stage => stage.Threshold <= points);

    internal static AbyssRankCatalog FromJson(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (document?.SchemaVersion != 1 || document.Stages is null)
                throw new InvalidDataException("Unsupported Abyss point-stage catalog.");
            return new(document.Stages);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid Abyss point-stage catalog.", error); }
    }

    private static AbyssRankCatalog LoadEmbedded()
    {
        using var stream = typeof(AbyssRankCatalog).Assembly.GetManifestResourceStream("Soulcrest.AbyssRanks.json")
            ?? throw new InvalidDataException("Missing bundled Abyss point stages.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record CatalogFile(int SchemaVersion, AbyssRankStage[]? Stages);
}
