using System.Collections.Frozen;

namespace Soulcrest.App.Services;

/// <summary>User-curated inactive content. This never deletes source observations or historical facts.</summary>
public static class AchievementContentPolicy
{
    public const int MetadataBuild = 26425393;
    private static readonly FrozenSet<int> Retired = new[]
    {
        239241400, 239241500, 239241600, 239241700, 239241800,
        239241900, 239242000, 239242100, 239242200, 239242300,
        249241400, 249241500, 249241600, 249241700, 249241800,
        249241900, 249242000, 249242100, 249242200, 249242300,
        510340100, 520340100
    }.ToFrozenSet();

    public static bool IsRetired(int familyId, int metadataBuild) =>
        metadataBuild == MetadataBuild && Retired.Contains(familyId);
}
