namespace SkinMarket.Models;

public class PriceSnapshot
{
    public Guid Id { get; set; }
    public int AppId { get; set; }
    public string MarketHashName { get; set; } = string.Empty;
    public string Currency { get; set; } = "USD";
    public string Source { get; set; } = "Unavailable";
    public decimal? Price { get; set; }
    public string Status { get; set; } = "Unavailable";
    public bool HasPrice { get; set; }
    public bool IsEstimated { get; set; }
    public string? FailureReason { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
