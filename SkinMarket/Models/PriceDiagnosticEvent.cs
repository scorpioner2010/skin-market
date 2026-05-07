namespace SkinMarket.Models;

public class PriceDiagnosticEvent
{
    public Guid Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime TimestampUtc
    {
        get => CreatedAtUtc;
        set => CreatedAtUtc = value;
    }
    public string Level { get; set; } = "Info";
    public string EventType { get; set; } = string.Empty;
    public int? GameType { get; set; }
    public int? AppId { get; set; }
    public string? MarketHashName { get; set; }
    public string? NormalizedMarketHashName { get; set; }
    public string? AssetId { get; set; }
    public string? Source { get; set; }
    public string? PriceType { get; set; }
    public string? Status { get; set; }
    public decimal? PriceUsd { get; set; }
    public decimal? OriginalPrice { get; set; }
    public string? OriginalCurrency { get; set; }
    public decimal? FxRate { get; set; }
    public decimal? ConfidenceScore { get; set; }
    public bool? IsEstimated { get; set; }
    public bool? IsCached { get; set; }
    public bool? IsStale { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? Endpoint { get; set; }
    public long? DurationMs { get; set; }
    public string? FailureReason { get; set; }
    public string? DetailsJson { get; set; }
}
