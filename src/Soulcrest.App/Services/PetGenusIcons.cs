namespace Soulcrest.App.Services;

/// <summary>Shared genus symbols from the existing local gaming.tools map data package.</summary>
public static class PetGenusIcons
{
    public static string? IconUrl(string genus)
    {
        var slot = genus?.ToLowerInvariant() switch
        {
            "cogni" => "intellect",
            "natura" => "nature",
            "varian" => "trans",
            "fera" => "feral",
            _ => null,
        };
        return slot is null ? null
            : $"https://{MainForm.MapDataHost}/icons/gamingtools/ui/resource/sprite/fwindow_vehicle/ut_fwindow_vehicle_creatureslot_{slot}_sprite.png";
    }
}
