using System.Collections.ObjectModel;
using Soulcrest.Core.GenusInsight;

namespace Soulcrest.App.Services;

/// <summary>Only anonymous counters of accepted preferences; never opens a file or activates a source.</summary>
internal static class GenusInsightFavoriteDiagnostics
{
    internal static DiagnosticResult Capture(GenusInsightFavoriteService? service, DateTimeOffset now)
    {
        var snapshot = service?.Snapshot;
        var status = service is null ? DiagnosticStatus.Unavailable
            : !service.CanSave || service.SaveFailed ? DiagnosticStatus.Error : DiagnosticStatus.Healthy;
        var facts = new Dictionary<string, object?>
        {
            ["serviceAvailable"] = service is not null,
            ["canSave"] = service?.CanSave,
            ["saveFailed"] = service?.SaveFailed,
            ["catalogStats"] = GenusInsightCatalog.Default.Stats.Count,
            ["globalFavoriteStats"] = snapshot?.GlobalStats.Count,
            ["slotOverrides"] = snapshot?.SlotOverrides.Count,
            ["includedSlotOverrides"] = snapshot?.SlotOverrides.Count(row => row.Value),
            ["excludedSlotOverrides"] = snapshot?.SlotOverrides.Count(row => !row.Value),
            ["effectiveSlotFavorites"] = snapshot?.Favorites.Count
        };
        return new("combat-genus-favorites", status, now.ToUniversalTime(), status switch
        {
            DiagnosticStatus.Healthy => "Zustand verfügbar, keine Auffälligkeit erkannt.",
            DiagnosticStatus.Error => "Ein Fehler wurde erkannt; Details prüfen.",
            _ => "Für diese Funktion ist noch kein laufender Dienst verfügbar."
        }, new ReadOnlyDictionary<string, object?>(facts));
    }
}
