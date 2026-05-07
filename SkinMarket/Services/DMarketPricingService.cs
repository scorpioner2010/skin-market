using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class DMarketPricingService : IDMarketPricingService
{
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<DMarketPricingService> _logger;
    private readonly IGameCatalog _gameCatalog;
    private readonly PricingOptions _options;
    private readonly IAppLogService _appLogService;
    private readonly IPriceDiagnosticLogService _priceDiagnosticLogService;

    public DMarketPricingService(
        HttpClient httpClient,
        IMemoryCache memoryCache,
        ILogger<DMarketPricingService> logger,
        IGameCatalog gameCatalog,
        IOptions<PricingOptions> options,
        IAppLogService appLogService,
        IPriceDiagnosticLogService priceDiagnosticLogService)
    {
        _httpClient = httpClient;
        _memoryCache = memoryCache;
        _logger = logger;
        _gameCatalog = gameCatalog;
        _options = options.Value;
        _appLogService = appLogService;
        _priceDiagnosticLogService = priceDiagnosticLogService;
    }

    public async Task<PriceSourceResult> ProbePriceAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default)
    {
        var normalizedName = MarketHashNameUtility.Normalize(marketHashName);
        var game = _gameCatalog.Get(gameType);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return Failure("MissingMarketHashName", normalizedName);
        }

        if (!game.SupportsDMarketPricing || string.IsNullOrWhiteSpace(game.DMarketGameId))
        {
            return Failure($"DMarket pricing is not configured for {game.DisplayName}.", normalizedName);
        }

        var cacheKey = $"dmarket-price::{game.Key}::{normalizedName}";
        var cooldownKey = $"dmarket-price-cooldown::{game.Key}";
        if (_memoryCache.TryGetValue<PriceSourceResult>(cacheKey, out var cachedResult) && cachedResult is not null)
        {
            cachedResult.IsCached = true;
            return cachedResult;
        }

        if (_memoryCache.TryGetValue<DateTimeOffset>(cooldownKey, out var cooldownUntil) &&
            cooldownUntil > DateTimeOffset.UtcNow)
        {
            var cooledDown = Failure($"DMarket cooldown is active until {cooldownUntil.UtcDateTime:O}.", normalizedName);
            cooledDown.Status = "RateLimited";
            Cache(cacheKey, cooledDown);
            return cooledDown;
        }

        const string endpoint = "/marketplace-api/v1/aggregated-prices";
        var request = new DMarketAggregatedPricesRequest
        {
            Limit = "1",
            Filter = new DMarketAggregatedPricesFilter
            {
                Game = game.DMarketGameId,
                Titles = [normalizedName]
            }
        };

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("DMarket price lookup started for {GameType} / {MarketHashName}.", gameType, normalizedName);
        await LogVerboseAppAsync("Info", $"Start. Endpoint={endpoint}; GameType={(int)gameType}; DMarketGameId={game.DMarketGameId}; MarketHashName={normalizedName}", nameof(DMarketPricingService), cancellationToken: cancellationToken);
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(endpoint, request, SerializerOptions, cancellationToken);
            stopwatch.Stop();
            await LogVerboseAppAsync("Info", $"End. Endpoint={endpoint}; Http={(int)response.StatusCode}; ElapsedMs={stopwatch.ElapsedMilliseconds}; MarketHashName={normalizedName}", nameof(DMarketPricingService), cancellationToken: cancellationToken);
            if (response.StatusCode == (HttpStatusCode)429)
            {
                _memoryCache.Set(cooldownKey, DateTimeOffset.UtcNow.Add(RateLimitCooldown), RateLimitCooldown);
                var rateLimited = Failure("DMarket is rate limiting requests.", normalizedName);
                rateLimited.Status = "RateLimited";
                await LogDMarketProblemAsync(
                    "SourceRateLimited",
                    endpoint,
                    game,
                    normalizedName,
                    rateLimited.FailureReason,
                    httpStatusCode: (int)response.StatusCode,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: new { requestedTitle = normalizedName, gameId = game.DMarketGameId },
                    cancellationToken: CancellationToken.None);
                Cache(cacheKey, rateLimited);
                return rateLimited;
            }

            if (!response.IsSuccessStatusCode)
            {
                var failed = Failure($"DMarket returned HTTP {(int)response.StatusCode}.", normalizedName);
                failed.ProvenanceJson = JsonSerializer.Serialize(new
                {
                    endpoint,
                    httpStatus = (int)response.StatusCode,
                    gameId = game.DMarketGameId,
                    requestedTitle = normalizedName
                });
                await LogDMarketProblemAsync(
                    "ExternalApiError",
                    endpoint,
                    game,
                    normalizedName,
                    failed.FailureReason,
                    httpStatusCode: (int)response.StatusCode,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: new { requestedTitle = normalizedName, gameId = game.DMarketGameId },
                    cancellationToken: CancellationToken.None);
                Cache(cacheKey, failed);
                return failed;
            }

            var rawContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<DMarketAggregatedPricesResponse>(rawContent, SerializerOptions);
            var item = payload?.AggregatedPrices.FirstOrDefault(entry =>
                string.Equals(MarketHashNameUtility.Normalize(entry.Title), normalizedName, StringComparison.Ordinal));
            if (item is null)
            {
                var failed = Failure("DMarket did not return this title.", normalizedName);
                var returnedTitles = payload?.AggregatedPrices
                    .Select(entry => MarketHashNameUtility.Normalize(entry.Title))
                    .Where(title => !string.IsNullOrWhiteSpace(title))
                    .Take(10)
                    .ToList() ?? [];
                failed.ProvenanceJson = JsonSerializer.Serialize(new
                {
                    endpoint,
                    httpStatus = (int)response.StatusCode,
                    gameId = game.DMarketGameId,
                    requestedTitle = normalizedName,
                    responseCount = payload?.AggregatedPrices.Count ?? 0,
                    returnedTitles
                });
                await LogDMarketProblemAsync(
                    "SourceNotFound",
                    endpoint,
                    game,
                    normalizedName,
                    failed.FailureReason,
                    httpStatusCode: (int)response.StatusCode,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: new
                    {
                        requestedTitle = normalizedName,
                        gameId = game.DMarketGameId,
                        returnedCount = payload?.AggregatedPrices.Count ?? 0,
                        returnedTitles
                    },
                    cancellationToken: cancellationToken);
                await LogVerboseAppAsync(
                    "Info",
                    $"DMarket title not found. Requested={normalizedName}; GameId={game.DMarketGameId}; Http={(int)response.StatusCode}; ResponseCount={payload?.AggregatedPrices.Count ?? 0}; Titles={string.Join(" | ", returnedTitles)}; Reason={failed.FailureReason}",
                    nameof(DMarketPricingService),
                    cancellationToken: cancellationToken);
                Cache(cacheKey, failed);
                return failed;
            }

            var bidPrice = ParseMoney(item.OrderBestPrice);
            var observedAtUtc = DateTime.UtcNow;
            var offerCount = TryParseInt(item.OfferCount);
            var orderCount = TryParseInt(item.OrderCount);

            var offerPriceUsd = TryGetOfferBestPriceUsd(item);
            if (offerPriceUsd is > 0)
            {
                var priceUsd = offerPriceUsd.Value;
                var result = new PriceSourceResult
                {
                    Success = true,
                    Price = priceUsd,
                    Currency = "USD",
                    Source = PriceSourceNames.DMarket,
                    PriceType = PriceTypeNames.LowestListing,
                    Status = "Live",
                    LastUpdatedUtc = observedAtUtc,
                    ObservedAtUtc = observedAtUtc,
                    ExpiresAtUtc = observedAtUtc.AddMinutes(Math.Max(1, _options.DMarketLiveCacheMinutes)),
                    TtlSeconds = Math.Max(1, _options.DMarketLiveCacheMinutes) * 60,
                    OriginalPrice = priceUsd,
                    OriginalCurrency = "USD",
                    FxRate = 1m,
                    Quantity = offerCount,
                    BestAskUsd = priceUsd,
                    BestBidUsd = bidPrice?.Currency == "USD" ? Math.Round(bidPrice.Value.Amount, 2, MidpointRounding.AwayFromZero) : null,
                    ConfidenceScore = offerCount is > 0 ? 0.90m : 0.84m,
                    ResolvedMarketHashName = normalizedName,
                    ProvenanceJson = JsonSerializer.Serialize(new
                    {
                        endpoint,
                        usedField = "offerBestPrice",
                        offerCount,
                        orderCount,
                        orderBestPrice = item.OrderBestPrice,
                        suggestedPrice = item.SuggestedPrice,
                        recommendedPrice = item.RecommendedPrice
                    }),
                    RawPayloadHash = Hash(rawContent)
                };
                Cache(cacheKey, result);
                return result;
            }

            var suggestedPriceUsd = TryGetSuggestedPriceUsd(item);
            if (suggestedPriceUsd is > 0)
            {
                await LogDMarketProblemAsync(
                    "SourceRejected",
                    endpoint,
                    game,
                    normalizedName,
                    "DMarket suggestedPrice/recommendedPrice is only an estimate, not a live current price.",
                    httpStatusCode: (int)response.StatusCode,
                    priceType: PriceTypeNames.Suggested,
                    priceUsd: suggestedPriceUsd,
                    originalCurrency: ParseMoney(item.SuggestedPrice ?? item.RecommendedPrice)?.Currency,
                    confidenceScore: 0.44m,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: new { requestedTitle = normalizedName, returnedTitle = item.Title, offerBestPrice = item.OfferBestPrice, suggestedPrice = item.SuggestedPrice, recommendedPrice = item.RecommendedPrice },
                    cancellationToken: cancellationToken);
                var priceUsd = suggestedPriceUsd.Value;
                var result = new PriceSourceResult
                {
                    Success = true,
                    Price = priceUsd,
                    Currency = "USD",
                    Source = PriceSourceNames.DMarket,
                    PriceType = PriceTypeNames.Suggested,
                    Status = "Estimated",
                    IsEstimated = true,
                    LastUpdatedUtc = observedAtUtc,
                    ObservedAtUtc = observedAtUtc,
                    ExpiresAtUtc = observedAtUtc.AddMinutes(Math.Max(1, _options.DMarketSalesHistoryCacheMinutes)),
                    TtlSeconds = Math.Max(1, _options.DMarketSalesHistoryCacheMinutes) * 60,
                    OriginalPrice = priceUsd,
                    OriginalCurrency = "USD",
                    FxRate = 1m,
                    Quantity = offerCount,
                    BestBidUsd = bidPrice?.Currency == "USD" ? Math.Round(bidPrice.Value.Amount, 2, MidpointRounding.AwayFromZero) : null,
                    ConfidenceScore = 0.44m,
                    ResolvedMarketHashName = normalizedName,
                    ProvenanceJson = JsonSerializer.Serialize(new
                    {
                        endpoint,
                        usedField = item.SuggestedPrice is not null ? "suggestedPrice" : "recommendedPrice",
                        offerCount,
                        orderCount
                    }),
                    RawPayloadHash = Hash(rawContent)
                };
                Cache(cacheKey, result);
                return result;
            }

            var unavailable = Failure("DMarket did not return a USD offerBestPrice or safe estimate.", normalizedName);
            await LogDMarketProblemAsync(
                "SourceReturnedNoUsablePrice",
                endpoint,
                game,
                normalizedName,
                unavailable.FailureReason,
                httpStatusCode: (int)response.StatusCode,
                durationMs: stopwatch.ElapsedMilliseconds,
                details: new { requestedTitle = normalizedName, returnedTitle = item.Title, offerBestPrice = item.OfferBestPrice, suggestedPrice = item.SuggestedPrice, recommendedPrice = item.RecommendedPrice },
                cancellationToken: cancellationToken);
            Cache(cacheKey, unavailable);
            return unavailable;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            var failed = Failure($"DMarket request timed out after {stopwatch.ElapsedMilliseconds}ms.", normalizedName);
            await LogDMarketProblemAsync(
                "SourceTimeout",
                endpoint,
                game,
                normalizedName,
                failed.FailureReason,
                durationMs: stopwatch.ElapsedMilliseconds,
                details: new { exceptionType = exception.GetType().Name, requestedTitle = normalizedName, gameId = game.DMarketGameId },
                cancellationToken: CancellationToken.None);
            await LogVerboseAppAsync("Warning", $"Timeout. Endpoint={endpoint}; MarketHashName={normalizedName}; ElapsedMs={stopwatch.ElapsedMilliseconds}; ExceptionType={exception.GetType().Name}; Reason={failed.FailureReason}", nameof(DMarketPricingService), exception, CancellationToken.None);
            Cache(cacheKey, failed);
            return failed;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            stopwatch.Stop();
            var failed = Failure(exception.Message, normalizedName);
            await LogDMarketProblemAsync(
                exception is JsonException ? "ParseFailed" : "SourceFailed",
                endpoint,
                game,
                normalizedName,
                failed.FailureReason,
                durationMs: stopwatch.ElapsedMilliseconds,
                details: new { exceptionType = exception.GetType().Name, requestedTitle = normalizedName, gameId = game.DMarketGameId },
                cancellationToken: CancellationToken.None);
            await LogVerboseAppAsync("Error", $"Fail. Endpoint={endpoint}; MarketHashName={normalizedName}; ExceptionType={exception.GetType().Name}; Reason={failed.FailureReason}", nameof(DMarketPricingService), exception, CancellationToken.None);
            Cache(cacheKey, failed);
            return failed;
        }
    }

    private void Cache(string cacheKey, PriceSourceResult result)
    {
        var minutes = result.Success
            ? result.PriceType == PriceTypeNames.Suggested ? _options.DMarketSalesHistoryCacheMinutes : _options.DMarketLiveCacheMinutes
            : _options.NegativeCacheMinutes;
        _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Max(1, minutes)));
    }

    private static PriceSourceResult Failure(string failureReason, string? marketHashName)
    {
        return new PriceSourceResult
        {
            Source = PriceSourceNames.DMarket,
            Status = "Unavailable",
            PriceType = PriceTypeNames.Unavailable,
            FailureReason = failureReason,
            Currency = "USD",
            LastUpdatedUtc = DateTime.UtcNow,
            ResolvedMarketHashName = marketHashName
        };
    }

    internal static (decimal Amount, string Currency)? ParseMoney(DMarketMoneyDto? money)
    {
        if (money is null ||
            string.IsNullOrWhiteSpace(money.Amount) ||
            string.IsNullOrWhiteSpace(money.Currency))
        {
            return null;
        }

        return decimal.TryParse(money.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? (NormalizeDMarketAmount(money.Amount, amount), money.Currency)
            : null;
    }

    private static decimal NormalizeDMarketAmount(string rawAmount, decimal parsedAmount)
    {
        return rawAmount.Contains('.', StringComparison.Ordinal) || rawAmount.Contains(',', StringComparison.Ordinal)
            ? parsedAmount
            : parsedAmount / 100m;
    }

    internal static decimal? TryGetOfferBestPriceUsd(DMarketAggregatedPriceDto item)
    {
        var offerPrice = ParseMoney(item.OfferBestPrice);
        return offerPrice is { Amount: > 0 } &&
               string.Equals(offerPrice.Value.Currency, "USD", StringComparison.OrdinalIgnoreCase)
            ? Math.Round(offerPrice.Value.Amount, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    internal static decimal? TryGetSuggestedPriceUsd(DMarketAggregatedPriceDto item)
    {
        var suggestedPrice = ParseMoney(item.SuggestedPrice) ?? ParseMoney(item.RecommendedPrice);
        return suggestedPrice is { Amount: > 0 } &&
               string.Equals(suggestedPrice.Value.Currency, "USD", StringComparison.OrdinalIgnoreCase)
            ? Math.Round(suggestedPrice.Value.Amount, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    private static int? TryParseInt(string? rawValue)
    {
        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private Task LogDMarketProblemAsync(
        string eventType,
        string endpoint,
        GameDefinition game,
        string? marketHashName,
        string? failureReason,
        int? httpStatusCode = null,
        string? priceType = null,
        decimal? priceUsd = null,
        string? originalCurrency = null,
        decimal? confidenceScore = null,
        long? durationMs = null,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        return _priceDiagnosticLogService.LogProblemAsync(
            eventType,
            PriceSourceNames.DMarket,
            failureReason ?? "DMarket price problem.",
            game.Type,
            game.SteamAppId,
            marketHashName,
            endpoint: endpoint,
            httpStatusCode: httpStatusCode,
            priceType: priceType,
            priceUsd: priceUsd,
            originalCurrency: originalCurrency,
            confidenceScore: confidenceScore,
            durationMs: durationMs,
            detailsJson: details is null ? null : JsonSerializer.Serialize(details),
            cancellationToken: cancellationToken);
    }

    private Task LogVerboseAppAsync(
        string level,
        string message,
        string? source = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        return _options.EnableVerbosePriceDiagnostics
            ? _appLogService.WriteAsync(level, message, source, exception, cancellationToken)
            : Task.CompletedTask;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
