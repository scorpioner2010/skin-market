namespace SkinMarket.Models;

public class SteamInventorySnapshotResult
{
    public DateTime LastSuccessRefreshUtc { get; set; }
    public List<SteamInventoryItemDto> Items { get; set; } = new();
}

public class SteamInventoryRefreshStatus
{
    public DateTime? LastSuccessRefreshUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public bool IsRefreshing { get; set; }
    public bool IsRateLimited { get; set; }
    public DateTime? NextAllowedRefreshUtc { get; set; }
    public string? LastErrorMessage { get; set; }
}

public class SteamInventoryRefreshRequest
{
    public GameType GameType { get; set; } = GameType.CS2;
}

public enum SteamInventoryRefreshPriority
{
    Normal = 1,
    High = 0
}
