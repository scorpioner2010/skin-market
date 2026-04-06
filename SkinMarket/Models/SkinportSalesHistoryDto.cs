using System.Text.Json.Serialization;

namespace SkinMarket.Models;

public class SkinportSalesHistoryDto
{
    [JsonPropertyName("market_hash_name")]
    public string MarketHashName { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("last_7_days")]
    public SkinportSalesWindowDto? Last7Days { get; set; }

    [JsonPropertyName("last_30_days")]
    public SkinportSalesWindowDto? Last30Days { get; set; }

    [JsonPropertyName("last_90_days")]
    public SkinportSalesWindowDto? Last90Days { get; set; }
}

public class SkinportSalesWindowDto
{
    [JsonPropertyName("median")]
    public decimal? Median { get; set; }

    [JsonPropertyName("volume")]
    public int? Volume { get; set; }
}
