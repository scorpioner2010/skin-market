namespace SkinMarket.Models;

public class SteamTradeOfferConfirmationResult
{
    public bool Success { get; set; }
    public string OfferId { get; set; } = string.Empty;
    public string Flow { get; set; } = string.Empty;
    public string? State { get; set; }
    public string Message { get; set; } = string.Empty;
}
