namespace SkinMarket.Models;

public class BotSaleHistoryItem
{
    public Guid Id { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string BuyerDisplayName { get; set; } = string.Empty;
    public string BuyerSteamId { get; set; } = string.Empty;
    public string? BuyerAvatarUrl { get; set; }
    public string? SourceSellerDisplayName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? DeliveryStatus { get; set; }
    public decimal Price { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PurchasedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? DeliveryTradeOfferId { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public string? DeliveryErrorMessage { get; set; }
}
