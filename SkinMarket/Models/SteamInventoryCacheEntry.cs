namespace SkinMarket.Models;

public class SteamInventoryCacheEntry
{
    public Guid Id { get; set; }
    public string SteamId { get; set; } = string.Empty;
    public GameType GameType { get; set; } = GameType.CS2;
    public int AppId { get; set; }
    public string ContextId { get; set; } = string.Empty;
    public string ItemsJson { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public DateTime FetchedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
