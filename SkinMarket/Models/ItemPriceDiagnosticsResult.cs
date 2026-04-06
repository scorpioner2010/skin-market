namespace SkinMarket.Models;

public class ItemPriceDiagnosticsResult
{
    public string ItemName { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string? MarketHashName { get; set; }
    public string? MarketName { get; set; }
    public decimal? SteamPrice { get; set; }
    public decimal? SkinportPrice { get; set; }
    public decimal? FinalPrice { get; set; }
    public string FinalSource { get; set; } = "Unavailable";
    public string? SteamError { get; set; }
    public string? SkinportError { get; set; }
}
