namespace SkinMarket.Models;

public class BotPurchaseHistoryItem
{
    public Guid Id { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string SellerDisplayName { get; set; } = string.Empty;
    public string SellerSteamId { get; set; } = string.Empty;
    public string? SellerAvatarUrl { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal CreditAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ReceivedByBotAtUtc { get; set; }
    public DateTime? CreditedAtUtc { get; set; }
    public string? TradeOfferId { get; set; }
    public string? ErrorMessage { get; set; }
}
