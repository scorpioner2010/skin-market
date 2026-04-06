using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SkinportPricingService : ISkinportPricingService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SkinportPricingService> _logger;
    private readonly IGameCatalog _gameCatalog;
    private readonly PricingOptions _options;

    public SkinportPricingService(
        HttpClient httpClient,
        IMemoryCache memoryCache,
        ILogger<SkinportPricingService> logger,
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

        if (!game.SupportsSkinportPricing)
        {
            return Failure($"Skinport pricing is not configured for {game.DisplayName}.", normalizedName);
        }

        var historyMap = await GetSalesHistoryAsync([normalizedName], gameType, cancellationToken);
        if (historyMap.TryGetValue(normalizedName, out var historyItem))
        {
            var historyResult = ResolveFromHistory(historyItem, normalizedName);
            if (historyResult.Success)
            {
                return historyResult;
            }
        }

        var outOfStockMap = await GetOutOfStockPriceMapAsync(gameType, cancellationToken);
        if (outOfStockMap.TryGetValue(normalizedName, out var outOfStockItem))
        {
            var outOfStockResult = ResolveFromOutOfStock(outOfStockItem, normalizedName);
            if (outOfStockResult.Success)
            {
                return outOfStockResult;
            }
        }

        return Failure("Skinport did not return a usable history or out-of-stock price.", normalizedName);
    }

    public async Task<IReadOnlyDictionary<string, SkinportSalesHistoryDto>> GetSalesHistoryAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
        var normalizedNames = marketHashNames
            .Select(MarketHashNameUtility.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalizedNames.Count == 0)
        {
            return EmptyHistoryMap();
        }

        var game = _gameCatalog.Get(gameType);
        var cacheKey = $"skinport-history::{game.Key}::{string.Join('|', normalizedNames)}::{_options.PreferredCurrency}";
        if (_memoryCache.TryGetValue<IReadOnlyDictionary<string, SkinportSalesHistoryDto>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var endpoint =
            $"https://api.skinport.com/v1/sales/history?app_id={game.SteamAppId}&currency={Uri.EscapeDataString(_options.PreferredCurrency)}&market_hash_name={Uri.EscapeDataString(string.Join(',', normalizedNames))}";

        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Skinport history request failed with status code {StatusCode}.", (int)response.StatusCode);
                return EmptyHistoryMap();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<SkinportSalesHistoryDto>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            var result = payload?
                .Where(item => !string.IsNullOrWhiteSpace(item.MarketHashName))
                .ToDictionary(item => item.MarketHashName, item => item, StringComparer.Ordinal)
                ?? new Dictionary<string, SkinportSalesHistoryDto>(StringComparer.Ordinal);

            _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Max(1, _options.SkinportHistoryCacheMinutes)));
            return result;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            _logger.LogWarning(exception, "Skinport history lookup failed.");
            return EmptyHistoryMap();
        }
    }

    public async Task<IReadOnlyDictionary<string, SkinportOutOfStockItemDto>> GetOutOfStockPriceMapAsync(GameType gameType, CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(gameType);
        var cacheKey = $"skinport-out-of-stock::{game.Key}::{_options.PreferredCurrency}";
        if (_memoryCache.TryGetValue<IReadOnlyDictionary<string, SkinportOutOfStockItemDto>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var endpoint =
            $"https://api.skinport.com/v1/items?app_id={game.SteamAppId}&currency={Uri.EscapeDataString(_options.PreferredCurrency)}&tradable=0";

        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Skinport items request failed with status code {StatusCode}.", (int)response.StatusCode);
                return EmptyOutOfStockMap();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<SkinportOutOfStockItemDto>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            var result = payload?
                .Where(item => !string.IsNullOrWhiteSpace(item.MarketHashName))
                .ToDictionary(item => item.MarketHashName, item => item, StringComparer.Ordinal)
                ?? new Dictionary<string, SkinportOutOfStockItemDto>(StringComparer.Ordinal);

            _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Max(1, _options.SkinportItemsCacheMinutes)));
            return result;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            _logger.LogWarning(exception, "Skinport out-of-stock lookup failed.");
            return EmptyOutOfStockMap();
        }
    }

    private PriceSourceResult ResolveFromHistory(SkinportSalesHistoryDto item, string marketHashName)
    {
        var candidates = new (SkinportSalesWindowDto? Window, string Status)[]
        {
            (item.Last7Days, "SkinportHistory7d"),
            (item.Last30Days, "SkinportHistory30d"),
            (item.Last90Days, "SkinportHistory90d")
        };

        foreach (var candidate in candidates)
        {
            if (candidate.Window?.Median is decimal median && median > 0 && (candidate.Window.Volume ?? 0) > 0)
            {
                return new PriceSourceResult
                {
                    Success = true,
                    Price = Math.Round(median, 2, MidpointRounding.AwayFromZero),
                    Currency = item.Currency,
                    Source = "Skinport",
                    Status = "Live",
                    LastUpdatedUtc = DateTime.UtcNow,
                    ResolvedMarketHashName = marketHashName
                };
            }
        }

        return Failure("Skinport history did not contain a usable median.", marketHashName);
    }

    private PriceSourceResult ResolveFromOutOfStock(SkinportOutOfStockItemDto item, string marketHashName)
    {
        var price = item.SuggestedPrice ?? item.AvgSalePrice;
        if (!price.HasValue || price <= 0)
        {
            return Failure("Skinport out-of-stock data did not contain a usable price.", marketHashName);
        }

        return new PriceSourceResult
        {
            Success = true,
            Price = Math.Round(price.Value, 2, MidpointRounding.AwayFromZero),
            Currency = item.Currency,
            Source = "Skinport",
            Status = "Estimated",
            IsEstimated = true,
            LastUpdatedUtc = DateTime.UtcNow,
            ResolvedMarketHashName = marketHashName
        };
    }

    private static PriceSourceResult Failure(string failureReason, string? marketHashName)
    {
        return new PriceSourceResult
        {
            Source = "Skinport",
            Status = "Unavailable",
            FailureReason = failureReason,
            Currency = "USD",
            LastUpdatedUtc = DateTime.UtcNow,
            ResolvedMarketHashName = marketHashName
        };
    }

    private static IReadOnlyDictionary<string, SkinportSalesHistoryDto> EmptyHistoryMap()
    {
        return new Dictionary<string, SkinportSalesHistoryDto>(StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, SkinportOutOfStockItemDto> EmptyOutOfStockMap()
    {
        return new Dictionary<string, SkinportOutOfStockItemDto>(StringComparer.Ordinal);
    }
}
