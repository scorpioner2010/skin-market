namespace SkinMarket.Infrastructure;

public class SteamInventoryRefreshOptions
{
    public const string SectionName = "SteamInventoryRefresh";

    public int SnapshotFreshnessMinutes { get; set; } = 1;
    public int AutoRefreshStaleMinutes { get; set; } = 2;
    public int FailedAutoRefreshAttemptCooldownMinutes { get; set; } = 10;
    public int DelayBetweenSteamRequestsSeconds { get; set; } = 3;
    public int RequestTimeoutSeconds { get; set; } = 25;
    public int FailedRefreshCooldownMinutes { get; set; } = 1;
}
