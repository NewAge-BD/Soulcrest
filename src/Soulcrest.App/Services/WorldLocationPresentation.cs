namespace Soulcrest.App.Services;

/// <summary>Presentation of the transient network location, independent of progress baselines.</summary>
public sealed record WorldLocationDisplay(string Name, string Detail, string Status, string State);

public static class WorldLocationPresentation
{
    /// <param name="mapLabel">Map-package label for a nameless public world classification; never a guessed area.</param>
    public static WorldLocationDisplay Build(WorldContextSnapshot snapshot, bool enabled, Func<string, string?>? mapLabel = null)
    {
        if (!enabled)
            return new(UiText.T("Netzwerk-Tracking ausgeschaltet"), "", UiText.T("Aus"), "off");
        if (snapshot.Status == WorldContextStatus.Transitioning)
            return new(UiText.T("Gebietswechsel läuft …"), UiText.T("Warte auf die Bestätigung des neuen Aufenthaltsorts."),
                UiText.T("Gebietswechsel"), "transitioning");
        if (snapshot.Status != WorldContextStatus.Confirmed)
            return new(UiText.T("Aufenthalt noch nicht bestätigt"), UiText.T("Neue Gebietsdaten stehen noch aus."),
                UiText.T("Nicht bestätigt"), snapshot.Status == WorldContextStatus.Stale ? "stale" : "unknown");

        var name = (UiText.Language == "de" ? snapshot.NameDe ?? snapshot.NameEn : snapshot.NameEn ?? snapshot.NameDe)
            ?? (snapshot.SoulcrestMapId is { Length: > 0 } mapId ? mapLabel?.Invoke(mapId) : null);
        var categoryName = UiText.Language == "de" ? snapshot.CategoryNameDe ?? snapshot.CategoryNameEn
            : snapshot.CategoryNameEn ?? snapshot.CategoryNameDe;
        var category = categoryName ?? (snapshot.Kind switch
        {
            WorldContextKind.Instance => UiText.T("Instanz"),
            WorldContextKind.OpenWorld => UiText.T("Offenes Gebiet"),
            _ => UiText.T("Gebietsart noch nicht zugeordnet")
        });
        var mode = UiText.Language == "de" ? snapshot.ModeNameDe ?? snapshot.ModeNameEn
            : snapshot.ModeNameEn ?? snapshot.ModeNameDe;
        return new(name ?? UiText.T("Gebiet noch nicht zugeordnet"),
            mode is null || mode == category ? category : category + " · " + mode, UiText.T("Bestätigt"), "confirmed");
    }

}
