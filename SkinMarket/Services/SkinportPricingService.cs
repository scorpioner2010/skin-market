using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SkinportPricingService : ISkinportPricingService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SkinportPricingService> _logger;
    private readonly IGameCatalog _gameCatalog;

    public SkinportPricingService(HttpClient httpClient, IMemoryCache memoryCache, ILogger<SkinportPricingService> logger, IGameCatalog gameCatalog)
    {
        _httpClient = httpClient;
        _memoryCache = memoryCache;
        _logger = logger;
        _gameCatalog = gameCatalog;
    }

    public async Task<IReadOnlyDictionary<string, SkinportItemDto>> GetPriceMapAsync(GameType gameType, CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(gameType);
        if (!game.SupportsSkinportPricing)
        {
            _logger.LogInformation("Skinport pricing is not configured for game {GameKey}.", game.Key);
            return EmptyPriceMap();
        }

        var cacheKey = $"skinport-prices-usd-{game.SteamAppId}-tradable-0";
        var normalizedCacheKey = $"{cacheKey}-normalized";
        var endpoint = $"https://api.skinport.com/v1/items?app_id={game.SteamAppId}&currency=USD&tradable=0";

        if (_memoryCache.TryGetValue<IReadOnlyDictionary<string, SkinportItemDto>>(cacheKey, out var cachedPrices) &&
            cachedPrices is not null)
        {
            _logger.LogDebug("Using cached Skinport price map.");
            return cachedPrices;
        }

        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Skinport pricing request failed with status code {StatusCode}.", (int)response.StatusCode);
                return EmptyPriceMap();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var items = await JsonSerializer.DeserializeAsync<List<SkinportItemDto>>(
                stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                },
                cancellationToken);

            if (items is null || items.Count == 0)
            {
                _logger.LogWarning("Skinport pricing response was empty.");
                return EmptyPriceMap();
            }

            var priceMap = items
                .Where(item => !string.IsNullOrWhiteSpace(item.MarketHashName))
                .GroupBy(item => item.MarketHashName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var normalizedPriceMap = priceMap
                .Values
                .GroupBy(item => NormalizeName(item.MarketHashName), StringComparer.Ordinal)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            _memoryCache.Set(cacheKey, priceMap, CacheDuration);
            _memoryCache.Set(normalizedCacheKey, normalizedPriceMap, CacheDuration);
            _logger.LogInformation("Skinport price map loaded with {Count} items and cached for {Minutes} minutes.", priceMap.Count, CacheDuration.TotalMinutes);
            return priceMap;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "Skinport pricing HTTP request failed.");
            return EmptyPriceMap();
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Skinport pricing response parsing failed.");
            return EmptyPriceMap();
        }
    }

    public async Task<ResolvedItemPrice> ResolvePriceAsync(string? itemName, GameType gameType, CancellationToken cancellationToken = default)
    {
        var probe = await ProbePriceAsync(itemName, gameType, cancellationToken);
        return new ResolvedItemPrice
        {
            RealPriceUsd = probe.Price,
            Source = probe.Source,
            IsFallback = !probe.Success
        };
    }

    public async Task<ResolvedItemPrice> ResolvePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        var probe = await ProbePriceAsync(item, cancellationToken);
        return new ResolvedItemPrice
        {
            RealPriceUsd = probe.Price,
            Source = probe.Source,
            IsFallback = !probe.Success
        };
    }

    public async Task<PriceSourceResult> ProbePriceAsync(string? itemName, GameType gameType, CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(gameType);
        if (!game.SupportsSkinportPricing)
        {
            return new PriceSourceResult
            {
                Source = "GamePricingNotConfigured",
                ErrorMessage = $"Skinport pricing is not configured for {game.DisplayName}."
            };
        }

        if (string.IsNullOrWhiteSpace(itemName))
        {
            return new PriceSourceResult
            {
                Source = "MissingItemName",
                ErrorMessage = "Item name is missing."
            };
        }

        var cacheKey = $"skinport-prices-usd-{game.SteamAppId}-tradable-0-normalized";
        var priceMap = await GetPriceMapAsync(gameType, cancellationToken);
        var trimmedName = itemName.Trim();
        if (!priceMap.TryGetValue(trimmedName, out var item))
        {
            _logger.LogInformation("Skinport exact match not found for item name {ItemName}.", trimmedName);

            var normalizedMap = _memoryCache.TryGetValue<IReadOnlyDictionary<string, SkinportItemDto>>(cacheKey, out var cachedNormalizedMap) &&
                                cachedNormalizedMap is not null
                ? cachedNormalizedMap
                : EmptyPriceMap();

            var normalizedName = NormalizeName(trimmedName);
            if (normalizedMap.TryGetValue(normalizedName, out item))
            {
                _logger.LogInformation("Using Skinport fallback price via normalized match for {ItemName}.", trimmedName);
                return CreateResolvedPrice(item, "SkinportNormalizedMatch");
            }

            _logger.LogInformation("Skinport normalized match not found for item name {ItemName}.", trimmedName);
            return new PriceSourceResult
            {
                Source = "ExactMatchNotFound",
                ErrorMessage = "Skinport exact/normalized match not found."
            };
        }

        _logger.LogInformation("Using Skinport fallback price via exact match for {ItemName}.", trimmedName);
        return CreateResolvedPrice(item, "SkinportExactMatch");
    }

    public async Task<PriceSourceResult> ProbePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in GetCandidates(item))
        {
            var result = await ProbePriceAsync(candidate.Value, item.GameType, cancellationToken);
            if (result.Success)
            {
                _logger.LogInformation("Skinport match used {CandidateType} for {ItemName}.", candidate.Key, item.Name);
                return result;
            }
        }

        return new PriceSourceResult
        {
            Source = "ExactMatchNotFound",
            ErrorMessage = "Skinport exact/normalized match not found."
        };
    }

    private PriceSourceResult CreateResolvedPrice(SkinportItemDto item, string source)
    {
        var resolvedValue = item.SuggestedPrice ?? item.MeanPrice ?? item.MedianPrice ?? item.MinPrice;
        if (!resolvedValue.HasValue || resolvedValue.Value <= 0)
        {
            _logger.LogInformation("Skinport item {ItemName} did not contain a usable price.", item.MarketHashName);
            return new PriceSourceResult
            {
                Source = "PriceUnavailable",
                ErrorMessage = "Skinport price fields were empty."
            };
        }

        return new PriceSourceResult
        {
            Success = true,
            Price = Math.Round(resolvedValue.Value, 2, MidpointRounding.AwayFromZero),
            Source = source,
            ErrorMessage = null
        };
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            value.Trim()
                .Replace('\u00A0', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static IReadOnlyDictionary<string, SkinportItemDto> EmptyPriceMap()
    {
        return new Dictionary<string, SkinportItemDto>(StringComparer.Ordinal);
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
}
