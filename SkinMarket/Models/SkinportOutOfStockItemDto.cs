using System.Text.Json.Serialization;

namespace SkinMarket.Models;

public class SkinportOutOfStockItemDto
{
    [JsonPropertyName("market_hash_name")]
    public string MarketHashName { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("suggested_price")]
    public decimal? SuggestedPrice { get; set; }

    [JsonPropertyName("avg_sale_price")]
    public decimal? AvgSalePrice { get; set; }

    [JsonPropertyName("sales_last_90d")]
    public int? SalesLast90Days { get; set; }
}
