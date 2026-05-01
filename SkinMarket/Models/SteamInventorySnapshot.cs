namespace SkinMarket.Models;

public class SteamInventorySnapshot
{
    public Guid Id { get; set; }
    public string SteamId { get; set; } = string.Empty;
    public GameType GameType { get; set; } = GameType.CS2;
    public string ItemsJson { get; set; } = "[]";
    public DateTime? LastSuccessRefreshUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public int RateLimitStrikeCount { get; set; }
    public DateTime? NextAllowedRefreshUtc { get; set; }
    public bool RefreshInProgress { get; set; }
}
