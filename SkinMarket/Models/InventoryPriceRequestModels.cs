namespace SkinMarket.Models;

public class InventoryPriceRefreshRequest
{
    public GameType GameType { get; set; } = GameType.CS2;
    public List<InventoryPriceItemRequest> Items { get; set; } = new();
}

public class InventoryPriceItemRequest
{
    public string AssetId { get; set; } = string.Empty;
    public string MarketHashName { get; set; } = string.Empty;
}

public class InventoryPriceStatusItem
{
    public string AssetId { get; set; } = string.Empty;
    public string MarketHashName { get; set; } = string.Empty;
    public bool HasPrice { get; set; }
    public decimal? Price { get; set; }
    public decimal? PriceUsd { get; set; }
    public string DisplayPrice { get; set; } = "No reliable price";
    public string Currency { get; set; } = "USD";
    public string Source { get; set; } = "Unavailable";
    public string PriceType { get; set; } = PriceTypeNames.Unavailable;
    public string Status { get; set; } = "Unavailable";
    public bool IsCached { get; set; }
    public bool IsEstimated { get; set; }
    public bool IsStale { get; set; }
    public decimal ConfidenceScore { get; set; }
    public string ConfidenceLabel { get; set; } = "Unavailable";
    public DateTime? LastUpdatedUtc { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public decimal? OriginalPrice { get; set; }
    public string? OriginalCurrency { get; set; }
    public decimal? FxRate { get; set; }
    public string? FailureReason { get; set; }
}
