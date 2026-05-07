using System.Text.Json.Serialization;

namespace SkinMarket.Models;

public class SkinportItemDto
{
    [JsonPropertyName("market_hash_name")]
    public string MarketHashName { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("suggested_price")]
    public decimal? SuggestedPrice { get; set; }

    [JsonPropertyName("min_price")]
    public decimal? MinPrice { get; set; }

    [JsonPropertyName("mean_price")]
    public decimal? MeanPrice { get; set; }

    [JsonPropertyName("median_price")]
    public decimal? MedianPrice { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("updated_at")]
    public long? UpdatedAt { get; set; }
}
