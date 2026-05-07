namespace SkinMarket.Models;

public class ItemPriceResolutionResult
{
    public bool HasPrice { get; set; }
    public decimal? Price { get; set; }
    public decimal? PriceUsd
    {
        get => Price;
        set => Price = value;
    }
    public decimal? DisplayPriceUsd => Price;
    public string Currency { get; set; } = "USD";
    public string Source { get; set; } = "Unavailable";
    public string PriceType { get; set; } = PriceTypeNames.Unavailable;
    public string Status { get; set; } = "Unavailable";
    public bool IsCached { get; set; }
    public bool IsEstimated { get; set; }
    public bool IsStale { get; set; }
    public decimal ConfidenceScore { get; set; }
    public string ConfidenceLabel => ConfidenceScore >= 0.8m ? "High" : ConfidenceScore >= 0.55m ? "Medium" : ConfidenceScore > 0 ? "Low" : "Unavailable";
    public DateTime? LastUpdatedUtc { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public decimal? OriginalPrice { get; set; }
    public string? OriginalCurrency { get; set; }
    public decimal? FxRate { get; set; }
    public int? Quantity { get; set; }
    public int? Volume { get; set; }
    public int? SalesCount { get; set; }
    public decimal? BestBidUsd { get; set; }
    public decimal? BestAskUsd { get; set; }
    public string? Provenance { get; set; }
    public string? ResolvedMarketHashName { get; set; }
    public bool NeedsRefresh { get; set; }
    public PriceSourceResult? SteamResult { get; set; }
    public PriceSourceResult? CsFloatResult { get; set; }
    public PriceSourceResult? SkinportResult { get; set; }
    public PriceSourceResult? DMarketResult { get; set; }
}
