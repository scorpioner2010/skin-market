namespace SkinMarket.Models;

public class MarketPurchaseRequest
{
    public GameType GameType { get; set; } = GameType.CS2;
    public int AppId { get; set; }
    public string ContextId { get; set; } = "2";
    public string AssetId { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public string? MarketHashName { get; set; }
}
