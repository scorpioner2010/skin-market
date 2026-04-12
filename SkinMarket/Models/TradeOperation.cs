namespace SkinMarket.Models;

public class TradeOperation
{
    public Guid Id { get; set; }
    public Guid AppUserId { get; set; }
    public string SteamId { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public int AppId { get; set; }
    public string ContextId { get; set; } = "2";
    public string ItemName { get; set; } = string.Empty;
    public string? MarketHashName { get; set; }
    public string? IconUrl { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? TradeOfferId { get; set; }
    public string? BotTradeUrl { get; set; }
    public string? BotAssetId { get; set; }
    public string? BotClassId { get; set; }
    public string? BotInstanceId { get; set; }
    public string? ErrorMessage { get; set; }
    public decimal CreditAmount { get; set; }
    public DateTime? ReceivedByBotAtUtc { get; set; }
    public DateTime? CreditedAtUtc { get; set; }

    public AppUser? AppUser { get; set; }
}
