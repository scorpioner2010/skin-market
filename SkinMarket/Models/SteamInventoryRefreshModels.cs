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
    public bool IsForced { get; set; }
    public DateTime? NextAllowedRefreshUtc { get; set; }
    public string? LastErrorMessage { get; set; }
    public string? RefreshReason { get; set; }
}

public class SteamInventoryRefreshRequest
{
    public GameType GameType { get; set; } = GameType.CS2;
    public bool ForceFreshness { get; set; }
    public string? Reason { get; set; }
}

public enum SteamInventoryRefreshPriority
{
    Normal = 1,
    High = 0
}

public static class SteamInventoryRefreshReasons
{
    public const string AutoStale = "AutoStale";
    public const string InitialLoad = "InitialLoad";
    public const string Manual = "Manual";
    public const string TradeCreated = "TradeCreated";
    public const string TradeAccepted = "TradeAccepted";
    public const string UserSoldItem = "UserSoldItem";
    public const string UserBoughtItem = "UserBoughtItem";
    public const string ItemDelivered = "ItemDelivered";
    public const string ItemCredited = "ItemCredited";

    public static bool IsTradeRelated(string? reason)
    {
        return reason is TradeCreated or TradeAccepted or UserSoldItem or UserBoughtItem or ItemDelivered or ItemCredited;
    }
}
