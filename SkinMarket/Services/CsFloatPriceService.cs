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
    private readonly IPriceDiagnosticLogService _priceDiagnosticLogService;

    public CsFloatPriceService(
        HttpClient httpClient,
        IMemoryCache memoryCache,
        ILogger<CsFloatPriceService> logger,
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

        if (game.SteamAppId != 730)
        {
            var failed = Failure("CSFloat pricing is supported only for CS2.", normalizedName);
            await LogCsFloatProblemAsync("SourceRejected", "csfloat.com/api/v1/listings", game, normalizedName, failed.FailureReason, details: new { appId = game.SteamAppId, game = game.DisplayName }, cancellationToken: cancellationToken);
            return failed;
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
        var apiKey = ResolveApiKey();
        _logger.LogInformation("CSFloat price lookup started for {GameType} / {MarketHashName}.", gameType, normalizedName);
        await LogVerboseAppAsync("Info", $"Start. Url={requestUri}; GameType={(int)gameType}; MarketHashName={normalizedName}", nameof(CsFloatPriceService), cancellationToken: cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", apiKey);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "CSFloat price lookup finished for {GameType} / {MarketHashName} with HTTP {StatusCode} in {ElapsedMs}ms.",
                gameType,
                normalizedName,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);
            await LogVerboseAppAsync("Info", $"End. Url={requestUri}; Http={(int)response.StatusCode}; ElapsedMs={stopwatch.ElapsedMilliseconds}; MarketHashName={normalizedName}", nameof(CsFloatPriceService), cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(CancellationToken.None);
                var remoteMessage = TryGetRemoteErrorMessage(errorBody);
                var failed = Failure(BuildHttpFailureReason(response.StatusCode, apiKey, remoteMessage), normalizedName);
                await LogCsFloatProblemAsync(
                    response.StatusCode == HttpStatusCode.TooManyRequests ? "SourceRateLimited" : "ExternalApiError",
                    "csfloat.com/api/v1/listings",
                    game,
                    normalizedName,
                    failed.FailureReason,
                    httpStatusCode: (int)response.StatusCode,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: new
                    {
                        apiKeyConfigured = !string.IsNullOrWhiteSpace(apiKey),
                        remoteMessage,
                        responseBody = TrimForLog(errorBody)
                    },
                    cancellationToken: CancellationToken.None);
                await LogVerboseAppAsync("Warning", $"Fail. Url={requestUri}; Http={(int)response.StatusCode}; MarketHashName={normalizedName}; Reason={failed.FailureReason}", nameof(CsFloatPriceService), cancellationToken: CancellationToken.None);
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
                var failed = Failure(listing is null ? "CSFloat listing was not found." : "CSFloat top-level listing price is missing or non-positive.", normalizedName);
                await LogCsFloatProblemAsync(
                    listing is null ? "SourceNotFound" : "SourceReturnedNoUsablePrice",
                    "csfloat.com/api/v1/listings",
                    game,
                    normalizedName,
                    failed.FailureReason,
                    httpStatusCode: (int)response.StatusCode,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: new
                    {
                        returnedCount = payload?.Count ?? 0,
                        returnedNames = payload?.Select(item => item.Item?.MarketHashName).Where(name => !string.IsNullOrWhiteSpace(name)).Take(10).ToList(),
                        listingPriceCents = listing?.Price,
                        scmPriceCents = listing?.Item?.Scm?.Price,
                        scmVolume = listing?.Item?.Scm?.Volume,
                        ignoredSteamReferenceField = listing?.Item?.Scm?.Price is > 0 ? "item.scm.price" : null
                    },
                    cancellationToken: CancellationToken.None);
                await LogVerboseAppAsync("Info", $"No price. Url={requestUri}; MarketHashName={normalizedName}; Reason={failed.FailureReason}", nameof(CsFloatPriceService), cancellationToken: CancellationToken.None);
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

            await LogVerboseAppAsync("Info", $"Success. Url={requestUri}; MarketHashName={normalizedName}; Price={result.Price}; Currency={result.Currency}", nameof(CsFloatPriceService), cancellationToken: CancellationToken.None);
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
            await LogCsFloatProblemAsync("SourceTimeout", "csfloat.com/api/v1/listings", game, normalizedName, failed.FailureReason, durationMs: stopwatch.ElapsedMilliseconds, details: new { exceptionType = exception.GetType().Name }, cancellationToken: CancellationToken.None);
            await LogVerboseAppAsync("Warning", $"Timeout. Url={requestUri}; MarketHashName={normalizedName}; ElapsedMs={stopwatch.ElapsedMilliseconds}; ExceptionType={exception.GetType().Name}; Reason={failed.FailureReason}", nameof(CsFloatPriceService), exception, CancellationToken.None);
            Cache(cacheKey, failed);
            return failed;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            stopwatch.Stop();
            _logger.LogWarning(exception, "CSFloat price lookup failed for {MarketHashName}.", normalizedName);
            var failed = Failure(exception.Message, normalizedName);
            await LogCsFloatProblemAsync(exception is JsonException ? "ParseFailed" : "SourceFailed", "csfloat.com/api/v1/listings", game, normalizedName, failed.FailureReason, durationMs: stopwatch.ElapsedMilliseconds, details: new { exceptionType = exception.GetType().Name }, cancellationToken: CancellationToken.None);
            await LogVerboseAppAsync("Error", $"Fail. Url={requestUri}; MarketHashName={normalizedName}; ExceptionType={exception.GetType().Name}; Reason={failed.FailureReason}", nameof(CsFloatPriceService), exception, CancellationToken.None);
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

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.CsFloatApiKey))
        {
            return _options.CsFloatApiKey.Trim();
        }

        return Environment.GetEnvironmentVariable("CSFLOAT_API_KEY")?.Trim()
               ?? Environment.GetEnvironmentVariable("CS_FLOAT_API_KEY")?.Trim()
               ?? string.Empty;
    }

    private static string BuildHttpFailureReason(HttpStatusCode statusCode, string? apiKey, string? remoteMessage)
    {
        var baseReason = string.IsNullOrWhiteSpace(remoteMessage)
            ? $"CSFloat returned HTTP {(int)statusCode}."
            : $"CSFloat returned HTTP {(int)statusCode}: {remoteMessage.Trim()}.";

        if (statusCode == HttpStatusCode.Forbidden && string.IsNullOrWhiteSpace(apiKey))
        {
            return $"{baseReason} Configure Pricing:CsFloatApiKey or CSFLOAT_API_KEY.";
        }

        return baseReason;
    }

    private static string? TryGetRemoteErrorMessage(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody))
        {
            return null;
        }

        try
        {
            var error = JsonSerializer.Deserialize<CsFloatErrorResponse>(
                errorBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return string.IsNullOrWhiteSpace(error?.Message) ? null : error.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TrimForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const int maxLength = 500;
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    internal static decimal? TryGetTopLevelListingPriceUsd(CsFloatListingDto? listing)
    {
        return listing?.Price is > 0
            ? Math.Round(listing.Price.Value / 100m, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    private Task LogCsFloatProblemAsync(
        string eventType,
        string endpoint,
        GameDefinition game,
        string? marketHashName,
        string? failureReason,
        int? httpStatusCode = null,
        string? priceType = null,
        decimal? priceUsd = null,
        decimal? confidenceScore = null,
        long? durationMs = null,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        return _priceDiagnosticLogService.LogProblemAsync(
            eventType,
            PriceSourceNames.CSFloat,
            failureReason ?? "CSFloat price problem.",
            game.Type,
            game.SteamAppId,
            marketHashName,
            httpStatusCode: httpStatusCode,
            endpoint: endpoint,
            priceType: priceType,
            priceUsd: priceUsd,
            originalCurrency: "USD",
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

    private sealed class CsFloatErrorResponse
    {
        public int? Code { get; set; }
        public string? Message { get; set; }
    }
}
