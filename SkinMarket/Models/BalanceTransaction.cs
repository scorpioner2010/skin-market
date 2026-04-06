namespace SkinMarket.Models;

public class BalanceTransaction
{
    public Guid Id { get; set; }
    public Guid AppUserId { get; set; }
    public decimal Amount { get; set; }
    public string Type { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    public AppUser? AppUser { get; set; }
}
