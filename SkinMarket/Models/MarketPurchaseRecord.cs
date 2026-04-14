namespace SkinMarket.Models;

public class MarketPurchaseRecord
{
    public Guid Id { get; set; }
    public GameType GameType { get; set; } = GameType.CS2;
    public Guid? SourceTradeOperationId { get; set; }
    public Guid? BuyerAppUserId { get; set; }
    public int AppId { get; set; } = 730;
    public string ContextId { get; set; } = "2";
    public string AssetId { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? MarketHashName { get; set; }
    public string? IconUrl { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PurchasedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? DeliveryStatus { get; set; }
    public string? DeliveryTradeOfferId { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public string? DeliveryErrorMessage { get; set; }

    public TradeOperation? SourceTradeOperation { get; set; }
    public AppUser? BuyerAppUser { get; set; }
}
