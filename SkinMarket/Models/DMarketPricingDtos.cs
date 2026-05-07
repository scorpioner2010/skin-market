using System.Text.Json.Serialization;

namespace SkinMarket.Models;

public class DMarketAggregatedPricesRequest
{
    [JsonPropertyName("limit")]
    public string Limit { get; set; } = "1";

    [JsonPropertyName("filter")]
    public DMarketAggregatedPricesFilter Filter { get; set; } = new();
}

public class DMarketAggregatedPricesFilter
{
    [JsonPropertyName("game")]
    public string Game { get; set; } = string.Empty;

    [JsonPropertyName("titles")]
    public List<string> Titles { get; set; } = new();
}

public class DMarketAggregatedPricesResponse
{
    [JsonPropertyName("aggregatedPrices")]
    public List<DMarketAggregatedPriceDto> AggregatedPrices { get; set; } = new();
}

public class DMarketAggregatedPriceDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("orderBestPrice")]
    public DMarketMoneyDto? OrderBestPrice { get; set; }

    [JsonPropertyName("orderCount")]
    public string? OrderCount { get; set; }

    [JsonPropertyName("offerBestPrice")]
    public DMarketMoneyDto? OfferBestPrice { get; set; }

    [JsonPropertyName("offerCount")]
    public string? OfferCount { get; set; }

    [JsonPropertyName("suggestedPrice")]
    public DMarketMoneyDto? SuggestedPrice { get; set; }

    [JsonPropertyName("recommendedPrice")]
    public DMarketMoneyDto? RecommendedPrice { get; set; }
}

public class DMarketMoneyDto
{
    [JsonPropertyName("Currency")]
    public string? CurrencyUpper { get; set; }

    [JsonPropertyName("currency")]
    public string? CurrencyLower { get; set; }

    [JsonPropertyName("Amount")]
    public string? AmountUpper { get; set; }

    [JsonPropertyName("amount")]
    public string? AmountLower { get; set; }

    [JsonIgnore]
    public string? Currency => CurrencyUpper ?? CurrencyLower;

    [JsonIgnore]
    public string? Amount => AmountUpper ?? AmountLower;
}
