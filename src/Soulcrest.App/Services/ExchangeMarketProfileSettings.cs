namespace Soulcrest.App.Services;

/// <summary>Public-market selection and fee assumptions, scoped to a local profile GUID.</summary>
public sealed record ExchangeMarketProfileSettings
{
    public string? Region { get; init; }
    public string? Faction { get; init; }
    public int? ServerId { get; init; }
    public int? GameServerKey { get; init; }
    public bool? AutoDetectServer { get; init; }
    public decimal SalesTaxPercent { get; init; } = 10m;
    public decimal RegistrationFeePercent { get; init; } = 2m;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasServerSelection => ServerId is > 0 && Text(Region) && Text(Faction);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AutomaticServerSelection => AutoDetectServer != false;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidFees => SalesTaxPercent is >= 0 and <= 100
        && RegistrationFeePercent is >= 0 and <= 100 && SalesTaxPercent + RegistrationFeePercent <= 100;

    internal static bool IsProfileId(string? value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;

    internal static ExchangeMarketProfileSettings Normalize(ExchangeMarketProfileSettings? value)
    {
        value ??= new();
        return value with
        {
            Region = Text(value.Region) ? value.Region!.Trim() : null,
            Faction = Text(value.Faction) ? value.Faction!.Trim() : null,
            ServerId = value.ServerId is > 0 ? value.ServerId : null,
            GameServerKey = value.GameServerKey is > 0 ? value.GameServerKey : null,
            SalesTaxPercent = value.HasValidFees ? value.SalesTaxPercent : 10m,
            RegistrationFeePercent = value.HasValidFees ? value.RegistrationFeePercent : 2m,
        };
    }

    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80
        && !value.Any(char.IsControl);
}
