namespace SkinMarket.Models;

public class ResolvedItemPrice
{
    public decimal? RealPriceUsd { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool IsFallback { get; set; }
    public string Status { get; set; } = "Unavailable";
    public bool IsCached { get; set; }
    public bool IsEstimated { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
    public string? FailureReason { get; set; }
    public string? ResolvedMarketHashName { get; set; }
}
