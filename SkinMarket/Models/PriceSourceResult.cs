namespace SkinMarket.Models;

public class PriceSourceResult
{
    public bool Success { get; set; }
    public decimal? Price { get; set; }
    public decimal? PriceUsd
    {
        get => Price;
        set => Price = value;
    }
    public string Currency { get; set; } = "USD";
    public string Source { get; set; } = string.Empty;
    public string? SourceItemId { get; set; }
    public string PriceType { get; set; } = PriceTypeNames.Unavailable;
    public string Status { get; set; } = "Unavailable";
    public bool IsCached { get; set; }
    public bool IsStale { get; set; }
    public bool IsEstimated { get; set; }
    public decimal ConfidenceScore { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public int? TtlSeconds { get; set; }
    public string? FailureReason { get; set; }
    public string? ResolvedMarketHashName { get; set; }
    public decimal? OriginalPrice { get; set; }
    public string? OriginalCurrency { get; set; }
    public decimal? FxRate { get; set; }
    public int? Quantity { get; set; }
    public int? Volume { get; set; }
    public int? SalesCount { get; set; }
    public decimal? BestBidUsd { get; set; }
    public decimal? BestAskUsd { get; set; }
    public string? RawPayloadHash { get; set; }
    public string? ProvenanceJson { get; set; }
    public string? ErrorMessage
    {
        get => FailureReason;
        set => FailureReason = value;
    }

    public bool IsLiveCurrent =>
        Success &&
        Price.HasValue &&
        !IsEstimated &&
        !IsStale &&
        PriceType is PriceTypeNames.LowestListing or PriceTypeNames.BestBid;
}
