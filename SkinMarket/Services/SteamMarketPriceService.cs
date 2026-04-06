using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamMarketPriceService : ISteamMarketPriceService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SteamMarketPriceService> _logger;
    private readonly IGameCatalog _gameCatalog;
    private readonly PricingOptions _options;

    public SteamMarketPriceService(
        HttpClient httpClient,
        IMemoryCache memoryCache,
        ILogger<SteamMarketPriceService> logger,
        IGameCatalog gameCatalog,
        IOptions<PricingOptions> options)
    {
        _httpClient = httpClient;
        _memoryCache = memoryCache;
        _logger = logger;
        _gameCatalog = gameCatalog;
        _options = options.Value;
    }

    public async Task<PriceSourceResult> ProbePriceAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default)
    {
        var normalizedName = MarketHashNameUtility.Normalize(marketHashName);
        var game = _gameCatalog.Get(gameType);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return Failure("Steam", "Unavailable", "MissingMarketHashName", normalizedName);
        }

        if (!game.SupportsSteamMarketPricing)
        {
            return Failure("Steam", "Unavailable", $"Steam market pricing is not configured for {game.DisplayName}.", normalizedName);
        }

        var cacheKey = $"steam-market-price::{game.Key}::{normalizedName}";
        if (_memoryCache.TryGetValue<PriceSourceResult>(cacheKey, out var cachedResult) && cachedResult is not null)
        {
            cachedResult.IsCached = true;
            return cachedResult;
        }

        var requestUri =
            $"https://steamcommunity.com/market/priceoverview/?country=US&currency=1&appid={game.SteamAppId}&market_hash_name={Uri.EscapeDataString(normalizedName)}";

        for (var attempt = 0; attempt <= Math.Max(0, _options.SteamRetryCount); attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var forbidden = Failure("Steam", "Forbidden", "Steam market denied the request.", normalizedName);
                    Cache(cacheKey, forbidden);
                    return forbidden;
                }

                if (response.StatusCode == (HttpStatusCode)429)
                {
                    if (attempt < _options.SteamRetryCount)
                    {
                        await DelayForRetryAsync(cancellationToken);
                        continue;
                    }

                    var rateLimited = Failure("Steam", "RateLimited", "Steam market is rate limiting requests.", normalizedName);
                    Cache(cacheKey, rateLimited);
                    return rateLimited;
                }

                if ((int)response.StatusCode >= 500 && attempt < _options.SteamRetryCount)
                {
                    await DelayForRetryAsync(cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var failed = Failure("Steam", "HttpError", $"Steam market returned HTTP {(int)response.StatusCode}.", normalizedName);
                    Cache(cacheKey, failed);
                    return failed;
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
                    var unavailable = Failure("Steam", "Unavailable", "Steam market did not return a price.", normalizedName);
                    Cache(cacheKey, unavailable);
                    return unavailable;
                }

                var parsedPrice = ParsePrice(payload.LowestPrice) ?? ParsePrice(payload.MedianPrice);
                if (!parsedPrice.HasValue || parsedPrice <= 0)
                {
                    var malformed = Failure("Steam", "MalformedResponse", "Steam market response did not contain a usable price.", normalizedName);
                    Cache(cacheKey, malformed);
                    return malformed;
                }

                var result = new PriceSourceResult
                {
                    Success = true,
                    Price = Math.Round(parsedPrice.Value, 2, MidpointRounding.AwayFromZero),
                    Currency = _options.PreferredCurrency,
                    Source = "Steam",
                    Status = "Live",
                    LastUpdatedUtc = DateTime.UtcNow,
                    ResolvedMarketHashName = normalizedName
                };

                Cache(cacheKey, result);
                return result;
            }
            catch (HttpRequestException exception) when (attempt < _options.SteamRetryCount)
            {
                _logger.LogWarning(exception, "Transient Steam price error for {MarketHashName}. Retrying.", normalizedName);
                await DelayForRetryAsync(cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                var failed = Failure("Steam", "NetworkError", exception.Message, normalizedName);
                Cache(cacheKey, failed);
                return failed;
            }
            catch (JsonException exception)
            {
                var failed = Failure("Steam", "MalformedResponse", exception.Message, normalizedName);
                Cache(cacheKey, failed);
                return failed;
            }
        }

        var exhausted = Failure("Steam", "TransientFailure", "Steam market retry budget exhausted.", normalizedName);
        Cache(exhaustedKey: cacheKey, result: exhausted);
        return exhausted;
    }

    private void Cache(string exhaustedKey, PriceSourceResult result)
    {
        var minutes = result.Success ? _options.SteamCacheMinutes : _options.NegativeCacheMinutes;
        _memoryCache.Set(exhaustedKey, result, TimeSpan.FromMinutes(Math.Max(1, minutes)));
    }

    private Task DelayForRetryAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(Math.Max(50, _options.HttpTransientRetryDelayMilliseconds), cancellationToken);
    }

    private static PriceSourceResult Failure(string source, string status, string failureReason, string? marketHashName)
    {
        return new PriceSourceResult
        {
            Source = source,
            Status = status,
            FailureReason = failureReason,
            Currency = "USD",
            ResolvedMarketHashName = marketHashName,
            LastUpdatedUtc = DateTime.UtcNow
        };
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
}
