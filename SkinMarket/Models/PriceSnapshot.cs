namespace SkinMarket.Models;

public class PriceSnapshot
{
    public Guid Id { get; set; }
    public int AppId { get; set; }
    public string MarketHashName { get; set; } = string.Empty;
    public string? VariantKey { get; set; }
    public string Currency { get; set; } = "USD";
    public string Source { get; set; } = "Unavailable";
    public string? SourceItemId { get; set; }
    public string PriceType { get; set; } = PriceTypeNames.Unavailable;
    public decimal? Price { get; set; }
    public decimal? PriceUsd { get; set; }
    public decimal? OriginalPrice { get; set; }
    public string? OriginalCurrency { get; set; }
    public decimal? FxRate { get; set; }
    public DateTime? FxObservedAtUtc { get; set; }
    public int? Quantity { get; set; }
    public int? Volume { get; set; }
    public int? SalesCount { get; set; }
    public decimal? BestBidUsd { get; set; }
    public decimal? BestAskUsd { get; set; }
    public string Status { get; set; } = "Unavailable";
    public bool HasPrice { get; set; }
    public bool IsEstimated { get; set; }
    public decimal ConfidenceScore { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public string? RawPayloadHash { get; set; }
    public string? ProvenanceJson { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public int TtlSeconds { get; set; }

    public bool IsCached => ExpiresAtUtc <= DateTime.UtcNow;
}
