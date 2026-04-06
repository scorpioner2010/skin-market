namespace SkinMarket.Models;

public class HistoryPageData
{
    public decimal CurrentBalance { get; set; }
    public List<SaleHistoryItem> Sales { get; set; } = new();
    public List<PurchaseHistoryItem> Purchases { get; set; } = new();
    public List<BalanceHistoryItem> BalanceTransactions { get; set; } = new();
}
