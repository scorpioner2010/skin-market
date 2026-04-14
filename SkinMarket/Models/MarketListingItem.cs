namespace SkinMarket.Models;

public class MarketListingItem
{
    public GameType GameType { get; set; } = GameType.CS2;
    public Guid? SourceTradeOperationId { get; set; }
    public Guid? SellerAppUserId { get; set; }
    public int AppId { get; set; }
    public string ContextId { get; set; } = "2";
    public string AssetId { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? MarketHashName { get; set; }
    public string? IconUrl { get; set; }
    public decimal Price { get; set; }
    public bool? Tradable { get; set; }
    public bool? Marketable { get; set; }
}
