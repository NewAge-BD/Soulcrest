namespace Soulcrest.App.Services;

/// <summary>
/// The player's faction, chosen in the setup (user request 2026-10-07). The guards at the Kibelisks kill
/// players of the other faction: Elyos are in danger on Altgard (Asmodian), Asmodians on Verteron (Elyos).
/// </summary>
public static class Factions
{
    public const string Elyos = "elyos";
    public const string Asmodian = "asmodian";

    /// <summary>Maps whose guard danger zones a player of this faction needs.</summary>
    public static string[] GuardZoneMapsFor(string? faction) => faction switch
    {
        Elyos => ["altgard"],
        Asmodian => ["verteron"],
        _ => [],
    };

    /// <summary>The faction's own map, where its pets are hunted first.</summary>
    public static string? HomeMap(string? faction) => faction switch
    {
        Elyos => "verteron",
        Asmodian => "altgard",
        _ => null,
    };

    /// <summary>Sets the faction and switches the guard zones to the other faction's map.</summary>
    public static void Choose(SettingsService settings, string? faction) => settings.Update(s =>
    {
        s.Faction = faction is Elyos or Asmodian ? faction : null;
        s.GuardZoneMaps = GuardZoneMapsFor(s.Faction);
    });
}
