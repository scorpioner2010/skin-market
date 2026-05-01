namespace SkinMarket.Infrastructure;

public class SteamInventoryRefreshOptions
{
    public const string SectionName = "SteamInventoryRefresh";

    public int SnapshotFreshnessMinutes { get; set; } = 10;
    public int DelayBetweenSteamRequestsSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 25;
    public int FailedRefreshCooldownMinutes { get; set; } = 2;
}
