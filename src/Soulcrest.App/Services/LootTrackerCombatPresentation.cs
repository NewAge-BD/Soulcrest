namespace Soulcrest.App.Services;

/// <summary>Explicit result and evidence labels shared by the route, replay and timeline.</summary>
public static class LootTrackerCombatPresentation
{
    public static string Label(LootTrackerCombatEvent row) => UiText.T(row.Kind switch
    {
        LootTrackerCombatKind.Kill => "Kill",
        LootTrackerCombatKind.Death => "Tod",
        LootTrackerCombatKind.Assist when row.IsEstimated => "Assist · geschätzt",
        LootTrackerCombatKind.Assist => "Assist",
        _ => "Unbekannt"
    });

    public static string Lane(LootTrackerCombatKind kind) => kind switch
    {
        LootTrackerCombatKind.Kill => "kill", LootTrackerCombatKind.Death => "death",
        LootTrackerCombatKind.Assist => "assist", _ => "unknown"
    };
}
