using System.Globalization;

namespace Soulcrest.App.Services;

/// <summary>Public reference-price labels; never relabels legacy own-market or wallet income.</summary>
public static class ExchangeMarketPresentation
{
    public static string TradeabilityHint => UiText.T("Individuelle Bindung und Handelbarkeit sind nicht bestätigt; die Schätzung setzt Handelbarkeit voraus.");

    public static string Faction(string value) => value.ToLowerInvariant() switch
    {
        "elyos" => "Elyos",
        "asmodian" or "asmodians" => "Asmodian",
        _ => value,
    };

    public static string Amount(long? value) => value is >= 0
        ? value.Value.ToString("N0", Culture) : "—";

    public static string ObservedAt(ExchangeLootValuationSummary summary) => summary.OldestObservedAt is { } oldest
        ? summary.NewestObservedAt is { } newest && newest != oldest
            ? UiText.F("Preisstand: {0} bis {1}", oldest.ToLocalTime().ToString("g", Culture), newest.ToLocalTime().ToString("g", Culture))
            : UiText.F("Preisstand: {0}", oldest.ToLocalTime().ToString("g", Culture))
        : UiText.T("Kein Preisbeleg vorhanden.");

    public static string Coverage(ExchangeLootValuationSummary summary) =>
        UiText.F("{0} / {1} Positionen bewertet", summary.PricedRows, summary.EligibleRows)
        + (summary.PendingRows > 0 ? " · " + UiText.F("{0} ausstehend", summary.PendingRows) : "");

    public static string QuantityCoverage(ExchangeLootValuationSummary summary) =>
        UiText.F("{0} / {1} Einheiten bewertet · {2} unbekannt", Amount(summary.PricedQuantity),
            Amount(summary.EligibleQuantity), Amount(summary.UnknownQuantity))
        + (summary.PendingQuantity is > 0 ? " · " + UiText.F("davon {0} ausstehend", Amount(summary.PendingQuantity)) : "");

    public static string Market(ExchangeLootValuationBasis basis) =>
        $"{basis.ServerName ?? "#" + basis.ServerId.ToString(CultureInfo.InvariantCulture)} · {basis.Region} · {Faction(basis.Faction)}";

    public static string Fees(ExchangeLootValuationBasis basis) => UiText.F("Gebührenannahme: {0}% Verkauf + {1}% Einstellung",
        basis.SalesTaxPercent.ToString("0.############################", Culture),
        basis.RegistrationFeePercent.ToString("0.############################", Culture));

    public static string Basis(ExchangeLootValuationBasis basis) => Market(basis) + " · " + Fees(basis);

    public static string Hint(ExchangeLootValuationSummary summary) => ObservedAt(summary) + " · " + Coverage(summary)
        + " · " + QuantityCoverage(summary) + (summary.Bases.IsDefaultOrEmpty ? "" : " · " + string.Join(" | ", summary.Bases.Select(Basis)))
        + " · " + TradeabilityHint;

    private static CultureInfo Culture => CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US");
}
