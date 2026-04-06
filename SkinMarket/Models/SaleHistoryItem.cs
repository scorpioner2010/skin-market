namespace SkinMarket.Models;

public class SaleHistoryItem
{
    public string ItemName { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal CreditAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CreditedAtUtc { get; set; }
    public string? TradeOfferId { get; set; }
    public string? ErrorMessage { get; set; }
}
