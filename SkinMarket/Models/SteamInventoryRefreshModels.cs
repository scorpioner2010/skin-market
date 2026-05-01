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
    public string RefreshState { get; set; } = "Idle";
    public string Reason { get; set; } = "StatusRead";
    public string? QueueStatus { get; set; }
    public string? QueuePriority { get; set; }
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

public enum SteamInventoryRefreshSource
{
    Auto,
    Manual
}

public class SteamInventoryRefreshDebugState
{
    public string SteamId { get; set; } = string.Empty;
    public GameType GameType { get; set; } = GameType.CS2;
    public int ItemCount { get; set; }
    public DateTime? LastSuccessRefreshUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public int RateLimitStrikeCount { get; set; }
    public DateTime? NextAllowedRefreshUtc { get; set; }
    public bool RefreshInProgress { get; set; }
    public string QueueStatus { get; set; } = "NotQueued";
    public string? QueuePriority { get; set; }
}
