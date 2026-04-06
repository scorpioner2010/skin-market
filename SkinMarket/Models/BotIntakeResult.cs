namespace SkinMarket.Models;

public class BotIntakeResult
{
    public bool Success { get; set; }
    public string NewStatus { get; set; } = string.Empty;
    public string? TradeOfferId { get; set; }
    public string Message { get; set; } = string.Empty;
}
