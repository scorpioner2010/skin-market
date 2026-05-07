namespace SkinMarket.Models;

public class GroupedInventoryItem
{
    public string GroupKey { get; set; } = string.Empty;
    public List<string> AssetIds { get; set; } = new();
    public string RepresentativeAssetId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? MarketHashName { get; set; }
    public string? IconUrl { get; set; }
    public string ClassId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public bool? Tradable { get; set; }
    public bool? Marketable { get; set; }
    public int Quantity { get; set; }
    public string? SellAssetId { get; set; }
    public string? SellClassId { get; set; }
    public string? SellInstanceId { get; set; }
    public string? SellItemName { get; set; }
    public string? SellMarketHashName { get; set; }
    public string? SellIconUrl { get; set; }
    public Guid? CreateTradeOperationId { get; set; }
    public string? CreateTradeStatus { get; set; }
    public string? AwaitingUserTradeOfferId { get; set; }
    public Guid? ActiveTradeOperationId { get; set; }
    public string? ActiveTradeStatus { get; set; }
    public string? ActiveTradeOfferId { get; set; }
    public bool HasWaitingForCredit { get; set; }
    public bool HasTradeProtected { get; set; }
    public InventoryItemActionDecision ActionDecision { get; set; } = new();
    public bool HasSellableItem => !string.IsNullOrWhiteSpace(SellAssetId);
    public List<GroupedInventoryStatusItem> StatusItems { get; set; } = new();
}
