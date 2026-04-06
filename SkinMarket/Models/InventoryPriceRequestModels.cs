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
    public string Currency { get; set; } = "USD";
    public string Source { get; set; } = "Unavailable";
    public string Status { get; set; } = "Unavailable";
    public bool IsCached { get; set; }
    public bool IsEstimated { get; set; }
    public string? FailureReason { get; set; }
}
