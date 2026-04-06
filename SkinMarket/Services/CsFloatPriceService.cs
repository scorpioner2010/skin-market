using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class CsFloatPriceService : ICsFloatPriceService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<CsFloatPriceService> _logger;
    private readonly IGameCatalog _gameCatalog;
    private readonly PricingOptions _options;

    public CsFloatPriceService(
        HttpClient httpClient,
        IMemoryCache memoryCache,
        ILogger<CsFloatPriceService> logger,
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
            return Failure("MissingMarketHashName", normalizedName);
        }

        if (game.SteamAppId != 730)
        {
            return Failure("CSFloat pricing is supported only for CS2.", normalizedName);
        }

        var cacheKey = $"csfloat-price::{normalizedName}";
        if (_memoryCache.TryGetValue<PriceSourceResult>(cacheKey, out var cachedResult) && cachedResult is not null)
        {
            cachedResult.IsCached = true;
            return cachedResult;
        }

        var requestUri =
            $"https://csfloat.com/api/v1/listings?limit=1&sort_by=lowest_price&type=buy_now&market_hash_name={Uri.EscapeDataString(normalizedName)}";

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failed = Failure($"CSFloat returned HTTP {(int)response.StatusCode}.", normalizedName);
                Cache(cacheKey, failed);
                return failed;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<CsFloatListingDto>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            var listing = payload?.FirstOrDefault(item =>
                string.Equals(
                    MarketHashNameUtility.Normalize(item.Item?.MarketHashName),
                    normalizedName,
                    StringComparison.Ordinal));

            var scmPrice = listing?.Item?.Scm?.Price;
            if (!scmPrice.HasValue || scmPrice <= 0)
            {
                var failed = Failure("CSFloat item.scm.price is missing.", normalizedName);
                Cache(cacheKey, failed);
                return failed;
            }

            var result = new PriceSourceResult
            {
                Success = true,
                Price = Math.Round(scmPrice.Value / 100m, 2, MidpointRounding.AwayFromZero),
                Currency = _options.PreferredCurrency,
                Source = "CSFloat",
                Status = "Live",
                LastUpdatedUtc = DateTime.UtcNow,
                ResolvedMarketHashName = normalizedName
            };

            Cache(cacheKey, result);
            return result;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            _logger.LogWarning(exception, "CSFloat price lookup failed for {MarketHashName}.", normalizedName);
            var failed = Failure(exception.Message, normalizedName);
            Cache(cacheKey, failed);
            return failed;
        }
    }

    private void Cache(string cacheKey, PriceSourceResult result)
    {
        var minutes = result.Success ? _options.CsFloatCacheMinutes : _options.NegativeCacheMinutes;
        _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Max(1, minutes)));
    }

    private static PriceSourceResult Failure(string failureReason, string? marketHashName)
    {
        return new PriceSourceResult
        {
            Source = "CSFloat",
            Status = "Unavailable",
            FailureReason = failureReason,
            Currency = "USD",
            LastUpdatedUtc = DateTime.UtcNow,
            ResolvedMarketHashName = marketHashName
        };
    }
}
