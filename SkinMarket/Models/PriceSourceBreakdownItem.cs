namespace SkinMarket.Models;

public class PriceSourceBreakdownItem
{
    public string Source { get; set; } = PriceSourceNames.Unavailable;
    public string PriceType { get; set; } = PriceTypeNames.Unavailable;
    public string Status { get; set; } = "Unavailable";
    public bool HasPrice { get; set; }
    public decimal? PriceUsd { get; set; }
    public bool IsEstimated { get; set; }
    public bool IsStale { get; set; }
    public decimal ConfidenceScore { get; set; }
    public int? Quantity { get; set; }
    public int? Volume { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? FailureReason { get; set; }
}
