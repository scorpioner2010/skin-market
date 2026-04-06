namespace SkinMarket.Models;

public class PurchaseHistoryItem
{
    public string ItemName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? DeliveryStatus { get; set; }
    public DateTime? PurchasedAtUtc { get; set; }
    public string? DeliveryTradeOfferId { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public string? DeliveryErrorMessage { get; set; }
}
