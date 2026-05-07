namespace SkinMarket.Infrastructure;

public class PricingOptions
{
    public const string SectionName = "Pricing";

    public string PreferredCurrency { get; set; } = "USD";
    public string CsFloatApiKey { get; set; } = string.Empty;
    public bool EnableSteamSource { get; set; } = true;
    public bool EnableCsFloatSource { get; set; } = true;
    public bool EnableSkinportSource { get; set; } = true;
    public bool EnableDMarketSource { get; set; } = true;
    public bool EnableBitSkinsSource { get; set; }
    public int SteamCacheMinutes { get; set; } = 10;
    public int CsFloatCacheMinutes { get; set; } = 10;
    public int SkinportHistoryCacheMinutes { get; set; } = 10;
    public int SkinportItemsCacheMinutes { get; set; } = 5;
    public int SkinportOutOfStockCacheMinutes { get; set; } = 60;
    public int DMarketLiveCacheMinutes { get; set; } = 5;
    public int DMarketSalesHistoryCacheMinutes { get; set; } = 60;
    public int SnapshotCacheHours { get; set; } = 24;
    public int NegativeCacheMinutes { get; set; } = 5;
    public int StaleSnapshotDays { get; set; } = 14;
    public int StaleSnapshotHours { get; set; } = 24;
    public int MaxConcurrentPriceLookups { get; set; } = 4;
    public bool AllowStaleSnapshotFallback { get; set; } = true;
    public int SteamRetryCount { get; set; } = 2;
    public int HttpTransientRetryDelayMilliseconds { get; set; } = 400;
    public int RefreshTimeoutSeconds { get; set; } = 75;
    public int RefreshingStateTimeoutSeconds { get; set; } = 90;
    public decimal ConfidenceHighThreshold { get; set; } = 0.8m;
    public decimal ConfidenceMediumThreshold { get; set; } = 0.55m;
    public int PricingDiagnosticsRetentionDays { get; set; } = 14;
    public bool EnablePriceProblemDiagnostics { get; set; } = true;
    public bool EnablePriceDiagnostics { get; set; } = true;
    public bool EnableVerbosePriceDiagnostics { get; set; } = false;
    public int PriceDiagnosticsMemoryLimit { get; set; } = 2000;
    public bool PriceDiagnosticsFileEnabled { get; set; } = true;
    public string PriceDiagnosticsDirectory { get; set; } = "logs/prices";
    public int PriceDiagnosticsMaxFileSizeMb { get; set; } = 10;
    public int PriceDiagnosticsMaxFiles { get; set; } = 7;
    public int PriceDiagnosticsMaxLogsPerMinute { get; set; } = 100;
}
