namespace SkinMarket.Models;

public class BalanceHistoryItem
{
    public string Type { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
