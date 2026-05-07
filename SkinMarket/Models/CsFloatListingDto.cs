using System.Text.Json.Serialization;

namespace SkinMarket.Models;

public class CsFloatListingDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("price")]
    public int? Price { get; set; }

    [JsonPropertyName("item")]
    public CsFloatItemDto? Item { get; set; }
}

public class CsFloatItemDto
{
    [JsonPropertyName("market_hash_name")]
    public string? MarketHashName { get; set; }

    [JsonPropertyName("scm")]
    public CsFloatScmDto? Scm { get; set; }
}

public class CsFloatScmDto
{
    [JsonPropertyName("price")]
    public int? Price { get; set; }

    [JsonPropertyName("volume")]
    public int? Volume { get; set; }
}
