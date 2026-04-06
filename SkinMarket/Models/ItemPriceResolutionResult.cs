namespace SkinMarket.Models;

public class ItemPriceResolutionResult
{
    public bool HasPrice { get; set; }
    public decimal? Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string Source { get; set; } = "Unavailable";
    public string Status { get; set; } = "Unavailable";
    public bool IsCached { get; set; }
    public bool IsEstimated { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
    public string? FailureReason { get; set; }
    public string? ResolvedMarketHashName { get; set; }
    public bool NeedsRefresh { get; set; }
    public PriceSourceResult? SteamResult { get; set; }
    public PriceSourceResult? CsFloatResult { get; set; }
    public PriceSourceResult? SkinportResult { get; set; }
}
