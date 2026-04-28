namespace SkinMarket.Models;

public class AppUser
{
    public Guid Id { get; set; }
    public string SteamId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? PersonaName { get; set; }
    public string? AvatarUrl { get; set; }
    public string? TradeUrl { get; set; }
    public bool IsAdmin { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public ICollection<BalanceTransaction> BalanceTransactions { get; set; } = new List<BalanceTransaction>();
    public ICollection<TradeOperation> TradeOperations { get; set; } = new List<TradeOperation>();
}
