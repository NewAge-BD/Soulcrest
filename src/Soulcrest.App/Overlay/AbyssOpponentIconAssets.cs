using System.Globalization;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Explicit opponent metadata only. A native Abyss grade is never inferred from points.</summary>
internal static class AbyssOpponentIconAssets
{
    internal static string? ClassAsset(string? classId) => classId switch
    {
        "Gladiator" or "Templar" or "Ranger" or "Assassin" or "Elementalist" or "Sorcerer" or "Cleric" or "Chanter"
            => $"class-icons/{classId}.png",
        _ => null
    };

    internal static string? RankAsset(int? grade)
    {
        if (grade is not (>= 1 and <= 19)) return null;
        // The bundled IDs carry the actual ordinal: 01=Supreme Commander, 19=Trainee.
        // Stages are ordered in the opposite direction by point threshold; their array index is not a grade.
        var id = "rank-" + grade.Value.ToString("00", CultureInfo.InvariantCulture);
        return AbyssRankCatalog.Default.Stages.FirstOrDefault(stage => stage.Id == id)?.IconAsset;
    }
}
