namespace SkinMarket.Models;

public class ItemPriceDiagnosticsResult
{
    public string ItemName { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string? MarketHashName { get; set; }
    public string? MarketName { get; set; }
    public string? ResolvedMarketHashName { get; set; }
    public decimal? SteamPrice { get; set; }
    public string? SteamStatus { get; set; }
    public decimal? SkinportPrice { get; set; }
    public string? SkinportStatus { get; set; }
    public decimal? CsFloatPrice { get; set; }
    public string? CsFloatStatus { get; set; }
    public decimal? DMarketPrice { get; set; }
    public string? DMarketStatus { get; set; }
    public decimal? FinalPrice { get; set; }
    public string FinalSource { get; set; } = "Unavailable";
    public string FinalPriceType { get; set; } = PriceTypeNames.Unavailable;
    public decimal ConfidenceScore { get; set; }
    public string FinalStatus { get; set; } = "Unavailable";
    public string? SteamError { get; set; }
    public string? SkinportError { get; set; }
    public string? CsFloatError { get; set; }
    public string? DMarketError { get; set; }
    public string? FailureReason { get; set; }
}
