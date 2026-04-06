namespace SkinMarket.Infrastructure;

public class PricingOptions
{
    public const string SectionName = "Pricing";

    public string PreferredCurrency { get; set; } = "USD";
    public bool EnableSteamSource { get; set; } = true;
    public bool EnableCsFloatSource { get; set; } = true;
    public bool EnableSkinportSource { get; set; } = true;
    public int SteamCacheMinutes { get; set; } = 10;
    public int CsFloatCacheMinutes { get; set; } = 10;
    public int SkinportHistoryCacheMinutes { get; set; } = 10;
    public int SkinportItemsCacheMinutes { get; set; } = 60;
    public int SnapshotCacheHours { get; set; } = 24;
    public int NegativeCacheMinutes { get; set; } = 5;
    public int StaleSnapshotDays { get; set; } = 14;
    public int MaxConcurrentPriceLookups { get; set; } = 4;
    public bool AllowStaleSnapshotFallback { get; set; } = true;
    public int SteamRetryCount { get; set; } = 2;
    public int HttpTransientRetryDelayMilliseconds { get; set; } = 400;
    public int RefreshTimeoutSeconds { get; set; } = 75;
    public int RefreshingStateTimeoutSeconds { get; set; } = 90;
}
