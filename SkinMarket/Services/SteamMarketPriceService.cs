using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamMarketPriceService : ISteamMarketPriceService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SteamMarketPriceService> _logger;
    private readonly IGameCatalog _gameCatalog;

    public SteamMarketPriceService(HttpClient httpClient, IMemoryCache memoryCache, ILogger<SteamMarketPriceService> logger, IGameCatalog gameCatalog)
    {
        _httpClient = httpClient;
        _memoryCache = memoryCache;
        _logger = logger;
        _gameCatalog = gameCatalog;
    }

    public async Task<ResolvedItemPrice> ResolvePriceAsync(string? itemName, GameType gameType, CancellationToken cancellationToken = default)
    {
        var probe = await ProbePriceAsync(itemName, gameType, cancellationToken);
        return new ResolvedItemPrice
        {
            RealPriceUsd = probe.Price,
            Source = probe.Source,
            IsFallback = true
        };
    }

    public async Task<ResolvedItemPrice> ResolvePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        var probe = await ProbePriceAsync(item, cancellationToken);
        return new ResolvedItemPrice
        {
            RealPriceUsd = probe.Price,
            Source = probe.Source,
            IsFallback = true
        };
    }

    public async Task<PriceSourceResult> ProbePriceAsync(string? itemName, GameType gameType, CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(gameType);
        if (!game.SupportsSteamMarketPricing)
        {
            return new PriceSourceResult
            {
                Source = "GamePricingNotConfigured",
                ErrorMessage = $"Steam market pricing is not configured for {game.DisplayName}."
            };
        }

        if (string.IsNullOrWhiteSpace(itemName))
        {
            return new PriceSourceResult
            {
                Source = "SteamUnavailable",
                ErrorMessage = "Item name is missing."
            };
        }

        var normalizedName = itemName.Trim();
        var cacheKey = $"steam-market-price::{game.Key}::{normalizedName}";
        if (_memoryCache.TryGetValue<PriceSourceResult>(cacheKey, out var cachedResult) && cachedResult is not null)
        {
            _logger.LogDebug("Steam market price cache hit for {ItemName}.", normalizedName);
            return cachedResult;
        }

        _logger.LogDebug("Steam market price cache miss for {ItemName}.", normalizedName);

        var requestUri =
            $"https://steamcommunity.com/market/priceoverview/?country=US&currency=1&appid={game.SteamAppId}&market_hash_name={Uri.EscapeDataString(normalizedName)}";

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Steam market price request failed for {ItemName} with status code {StatusCode}. URL: {RequestUrl}", normalizedName, (int)response.StatusCode, requestUri);
                return Unavailable(cacheKey, $"HTTP {(int)response.StatusCode}");
            }

            var rawContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<SteamMarketPriceResponse>(
                rawContent,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (payload?.Success != true)
            {
                _logger.LogInformation("Steam market price unavailable for {ItemName}. URL: {RequestUrl}. Response: {ResponseSnippet}", normalizedName, requestUri, GetSnippet(rawContent));
                return Unavailable(cacheKey, "Steam market did not return a price.");
            }

            var parsedPrice = ParsePrice(payload.LowestPrice) ??
                              ParsePrice(payload.MedianPrice);

            if (!parsedPrice.HasValue || parsedPrice.Value <= 0)
            {
                _logger.LogInformation("Steam market price payload did not contain a usable price for {ItemName}. URL: {RequestUrl}. Response: {ResponseSnippet}", normalizedName, requestUri, GetSnippet(rawContent));
                return Unavailable(cacheKey, "Steam market price payload was empty.");
            }

            var result = new PriceSourceResult
            {
                Success = true,
                Price = Math.Round(parsedPrice.Value, 2, MidpointRounding.AwayFromZero),
                Source = "Steam",
                ErrorMessage = null
            };

            _logger.LogInformation("Steam market price found for {ItemName}.", normalizedName);
            _memoryCache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "Steam market price HTTP request failed for {ItemName}.", normalizedName);
            return Unavailable(cacheKey, exception.Message);
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Steam market price parsing failed for {ItemName}. URL: {RequestUrl}", normalizedName, requestUri);
            return Unavailable(cacheKey, "Steam market response parsing failed.");
        }
    }

    public async Task<PriceSourceResult> ProbePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in GetCandidates(item))
        {
            var result = await ProbePriceAsync(candidate.Value, item.GameType, cancellationToken);
            if (result.Success)
            {
                _logger.LogInformation("Steam market match used {CandidateType} for {ItemName}.", candidate.Key, item.Name);
                return result;
            }
        }

        return new PriceSourceResult
        {
            Source = "SteamUnavailable",
            ErrorMessage = "Steam market price was not found for market hash name, market name or item name."
        };
    }

    private PriceSourceResult Unavailable(string cacheKey, string errorMessage)
    {
        var result = new PriceSourceResult
        {
            Source = "SteamUnavailable",
            ErrorMessage = errorMessage
        };

        _memoryCache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    private static decimal? ParsePrice(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var cleaned = new string(rawValue.Where(character => char.IsDigit(character) || character is '.' or ',').ToArray());
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        if (cleaned.Count(character => character == ',') == 1 && !cleaned.Contains('.'))
        {
            cleaned = cleaned.Replace(',', '.');
        }
        else
        {
            cleaned = cleaned.Replace(",", string.Empty);
        }

        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private sealed class SteamMarketPriceResponse
    {
        public bool Success { get; set; }
        public string? LowestPrice { get; set; }
        public string? MedianPrice { get; set; }
    }

    private static IEnumerable<KeyValuePair<string, string>> GetCandidates(SteamInventoryItemDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.MarketHashName))
        {
            yield return new KeyValuePair<string, string>("MarketHashName", item.MarketHashName);
        }

        if (!string.IsNullOrWhiteSpace(item.MarketName))
        {
            yield return new KeyValuePair<string, string>("MarketName", item.MarketName);
        }

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            yield return new KeyValuePair<string, string>("Name", item.Name);
        }
    }

    private static string GetSnippet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        return value.Length <= 200 ? value : value[..200];
    }
}
