namespace SkinMarket.Models;

public class GroupedInventoryStatusItem
{
    public bool IsReady { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal CreditAmountTotal { get; set; }
}
