namespace SkinMarket.Models;

public class MarketDeliveryResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? DeliveryTradeOfferId { get; set; }
    public string NewStatus { get; set; } = string.Empty;
}
