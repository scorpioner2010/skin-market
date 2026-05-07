using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    private readonly IAppLogService _appLogService;

    public CsFloatPriceService(
        HttpClient httpClient,
        IMemoryCache memoryCache,
        ILogger<CsFloatPriceService> logger,
        IGameCatalog gameCatalog,
        IOptions<PricingOptions> options,
        IAppLogService appLogService)
    {
        _httpClient = httpClient;
        _memoryCache = memoryCache;
        _logger = logger;
        _gameCatalog = gameCatalog;
        _options = options.Value;
        _appLogService = appLogService;
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

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("CSFloat price lookup started for {GameType} / {MarketHashName}.", gameType, normalizedName);
        await _appLogService.WriteAsync("Info", $"Start. Url={requestUri}; GameType={(int)gameType}; MarketHashName={normalizedName}", nameof(CsFloatPriceService), cancellationToken: cancellationToken);
        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "CSFloat price lookup finished for {GameType} / {MarketHashName} with HTTP {StatusCode} in {ElapsedMs}ms.",
                gameType,
                normalizedName,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);
            await _appLogService.WriteAsync("Info", $"End. Url={requestUri}; Http={(int)response.StatusCode}; ElapsedMs={stopwatch.ElapsedMilliseconds}; MarketHashName={normalizedName}", nameof(CsFloatPriceService), cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failed = Failure($"CSFloat returned HTTP {(int)response.StatusCode}.", normalizedName);
                await _appLogService.WriteAsync("Warning", $"Fail. Url={requestUri}; Http={(int)response.StatusCode}; MarketHashName={normalizedName}; Reason={failed.FailureReason}", nameof(CsFloatPriceService), cancellationToken: CancellationToken.None);
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

            var listingPriceUsd = TryGetTopLevelListingPriceUsd(listing);
            if (!listingPriceUsd.HasValue)
            {
                var failed = Failure("CSFloat listing price is missing.", normalizedName);
                await _appLogService.WriteAsync("Info", $"No price. Url={requestUri}; MarketHashName={normalizedName}; Reason={failed.FailureReason}", nameof(CsFloatPriceService), cancellationToken: CancellationToken.None);
                Cache(cacheKey, failed);
                return failed;
            }

            var observedAtUtc = DateTime.UtcNow;
            var priceUsd = listingPriceUsd.Value;
            var result = new PriceSourceResult
            {
                Success = true,
                Price = priceUsd,
                Currency = _options.PreferredCurrency,
                Source = PriceSourceNames.CSFloat,
                SourceItemId = listing?.Id,
                PriceType = PriceTypeNames.LowestListing,
                Status = "Live",
                LastUpdatedUtc = observedAtUtc,
                ObservedAtUtc = observedAtUtc,
                ExpiresAtUtc = observedAtUtc.AddMinutes(Math.Max(1, _options.CsFloatCacheMinutes)),
                TtlSeconds = Math.Max(1, _options.CsFloatCacheMinutes) * 60,
                OriginalPrice = priceUsd,
                OriginalCurrency = "USD",
                FxRate = 1m,
                BestAskUsd = priceUsd,
                ConfidenceScore = 0.86m,
                ResolvedMarketHashName = normalizedName,
                ProvenanceJson = JsonSerializer.Serialize(new
                {
                    usedField = "price",
                    ignoredSteamReferenceField = "item.scm.price",
                    scmPriceCents = listing?.Item?.Scm?.Price,
                    listingPriceCents = listing?.Price,
                    scmVolume = listing?.Item?.Scm?.Volume
                }),
                RawPayloadHash = Hash(JsonSerializer.Serialize(listing))
            };

            await _appLogService.WriteAsync("Info", $"Success. Url={requestUri}; MarketHashName={normalizedName}; Price={result.Price}; Currency={result.Currency}", nameof(CsFloatPriceService), cancellationToken: CancellationToken.None);
            Cache(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                exception,
                "CSFloat price lookup timed out for {GameType} / {MarketHashName} after {ElapsedMs}ms.",
                gameType,
                normalizedName,
                stopwatch.ElapsedMilliseconds);
            var failed = Failure($"CSFloat request timed out after {stopwatch.ElapsedMilliseconds}ms.", normalizedName);
            await _appLogService.WriteAsync("Warning", $"Timeout. Url={requestUri}; MarketHashName={normalizedName}; ElapsedMs={stopwatch.ElapsedMilliseconds}; ExceptionType={exception.GetType().Name}; Reason={failed.FailureReason}", nameof(CsFloatPriceService), exception, CancellationToken.None);
            Cache(cacheKey, failed);
            return failed;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            stopwatch.Stop();
            _logger.LogWarning(exception, "CSFloat price lookup failed for {MarketHashName}.", normalizedName);
            var failed = Failure(exception.Message, normalizedName);
            await _appLogService.WriteAsync("Error", $"Fail. Url={requestUri}; MarketHashName={normalizedName}; ExceptionType={exception.GetType().Name}; Reason={failed.FailureReason}", nameof(CsFloatPriceService), exception, CancellationToken.None);
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
            Source = PriceSourceNames.CSFloat,
            Status = "Unavailable",
            PriceType = PriceTypeNames.Unavailable,
            FailureReason = failureReason,
            Currency = "USD",
            LastUpdatedUtc = DateTime.UtcNow,
            ResolvedMarketHashName = marketHashName
        };
    }

    internal static decimal? TryGetTopLevelListingPriceUsd(CsFloatListingDto? listing)
    {
        return listing?.Price is > 0
            ? Math.Round(listing.Price.Value / 100m, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
