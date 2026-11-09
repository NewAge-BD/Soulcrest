using Soulcrest.App.Services;

namespace Soulcrest.App.OverlayEditorKit;

internal static class GenusInsightOverlayContent
{
    internal static bool Available(GenusInsightSnapshot snapshot) => snapshot.IsCurrent && snapshot.Generation > 0
        && !string.IsNullOrWhiteSpace(snapshot.ProfileId) && snapshot.MenuVisible == true
        && GenusInsightPresentation.KnownGenus(snapshot.SelectedGenus)
        && snapshot.ViewedPreset is >= 1 and <= 3 && snapshot.ViewedBoard?.Genus == snapshot.SelectedGenus;

    internal static bool Dedicated(OverlaySettings settings) => settings.Widgets.Count > 0
        && settings.Widgets.All(widget => widget.Kind == "genus-insight");
}
