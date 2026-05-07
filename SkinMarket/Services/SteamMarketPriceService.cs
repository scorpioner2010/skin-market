using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamMarketPriceService : ISteamMarketPriceService
{
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(15);
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SteamMarketPriceService> _logger;
    private readonly IGameCatalog _gameCatalog;
    private readonly PricingOptions _options;
    private readonly SteamMarketPriceOptions _steamMarketPriceOptions;
    private readonly IAppLogService _appLogService;

    public SteamMarketPriceService(
        HttpClient httpClient,
        IMemoryCache memoryCache,
        ILogger<SteamMarketPriceService> logger,
        IGameCatalog gameCatalog,
        IOptions<PricingOptions> options,
        IOptions<SteamMarketPriceOptions> steamMarketPriceOptions,
        IAppLogService appLogService)
    {
        _httpClient = httpClient;
        _memoryCache = memoryCache;
        _logger = logger;
        _gameCatalog = gameCatalog;
        _options = options.Value;
        _steamMarketPriceOptions = steamMarketPriceOptions.Value;
        _appLogService = appLogService;
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
        var cooldownKey = $"steam-market-price-cooldown::{game.Key}";
        if (_memoryCache.TryGetValue<PriceSourceResult>(cacheKey, out var cachedResult) && cachedResult is not null)
        {
            cachedResult.IsCached = true;
            return cachedResult;
        }

        if (!_steamMarketPriceOptions.Enabled)
        {
            var disabled = Failure("Steam", "Disabled", "Steam market priceoverview is disabled by configuration.", normalizedName);
            await _appLogService.WriteAsync(
                "Info",
                $"Disabled. Url=skipped; GameType={(int)gameType}; MarketHashName={normalizedName}; Reason={disabled.FailureReason}",
                nameof(SteamMarketPriceService),
                cancellationToken: CancellationToken.None);
            return disabled;
        }

        if (_memoryCache.TryGetValue<DateTimeOffset>(cooldownKey, out var cooldownUntil) &&
            cooldownUntil > DateTimeOffset.UtcNow)
        {
            var cooledDown = Failure("Steam", "RateLimited", $"Steam market cooldown is active until {cooldownUntil.UtcDateTime:O}.", normalizedName);
            await _appLogService.WriteAsync("Warning", $"Cooldown skip. GameType={(int)gameType}; MarketHashName={normalizedName}; CooldownUntil={cooldownUntil.UtcDateTime:O}", nameof(SteamMarketPriceService), cancellationToken: CancellationToken.None);
            Cache(cacheKey, cooledDown);
            return cooledDown;
        }

        var requestUri =
            $"https://steamcommunity.com/market/priceoverview/?country=US&currency=1&appid={game.SteamAppId}&market_hash_name={Uri.EscapeDataString(normalizedName)}";

        _logger.LogInformation("Steam price lookup started for {GameType} / {MarketHashName}.", gameType, normalizedName);
        await _appLogService.WriteAsync("Info", $"Start. Url={requestUri}; GameType={(int)gameType}; MarketHashName={normalizedName}", nameof(SteamMarketPriceService), cancellationToken: cancellationToken);
        for (var attempt = 0; attempt <= Math.Max(0, _options.SteamRetryCount); attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
                stopwatch.Stop();
                _logger.LogInformation(
                    "Steam price lookup finished for {GameType} / {MarketHashName} with HTTP {StatusCode} in {ElapsedMs}ms (attempt {Attempt}).",
                    gameType,
                    normalizedName,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    attempt + 1);
                await _appLogService.WriteAsync("Info", $"End. Url={requestUri}; Http={(int)response.StatusCode}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Attempt={attempt + 1}; MarketHashName={normalizedName}", nameof(SteamMarketPriceService), cancellationToken: cancellationToken);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var forbidden = Failure("Steam", "Forbidden", "Steam market denied the request.", normalizedName);
                    await _appLogService.WriteAsync("Warning", $"Fail. Status=Forbidden; Url={requestUri}; MarketHashName={normalizedName}; Reason={forbidden.FailureReason}", nameof(SteamMarketPriceService), cancellationToken: CancellationToken.None);
                    Cache(cacheKey, forbidden);
                    return forbidden;
                }

                if (response.StatusCode == (HttpStatusCode)429)
                {
                    _memoryCache.Set(cooldownKey, DateTimeOffset.UtcNow.Add(RateLimitCooldown), RateLimitCooldown);
                    var rateLimited = Failure("Steam", "RateLimited", "Steam market is rate limiting requests.", normalizedName);
                    await _appLogService.WriteAsync("Warning", $"Fail. Status=RateLimited; Url={requestUri}; MarketHashName={normalizedName}; CooldownMinutes={(int)RateLimitCooldown.TotalMinutes}; Reason={rateLimited.FailureReason}", nameof(SteamMarketPriceService), cancellationToken: CancellationToken.None);
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
                    await _appLogService.WriteAsync("Warning", $"Fail. Status=HttpError; Http={(int)response.StatusCode}; Url={requestUri}; MarketHashName={normalizedName}; Reason={failed.FailureReason}", nameof(SteamMarketPriceService), cancellationToken: CancellationToken.None);
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
                    await _appLogService.WriteAsync("Info", $"No price. Url={requestUri}; MarketHashName={normalizedName}; Reason={unavailable.FailureReason}", nameof(SteamMarketPriceService), cancellationToken: CancellationToken.None);
                    Cache(cacheKey, unavailable);
                    return unavailable;
                }

                var lowestPrice = ParsePrice(payload.LowestPrice);
                var medianPrice = ParsePrice(payload.MedianPrice);
                var usingLowest = lowestPrice.HasValue && lowestPrice > 0;
                var parsedPrice = usingLowest ? lowestPrice : medianPrice;
                if (!parsedPrice.HasValue || parsedPrice <= 0)
                {
                    var malformed = Failure("Steam", "MalformedResponse", "Steam market response did not contain a usable price.", normalizedName);
                    await _appLogService.WriteAsync("Warning", $"Parse fail. Url={requestUri}; MarketHashName={normalizedName}; Reason={malformed.FailureReason}", nameof(SteamMarketPriceService), cancellationToken: CancellationToken.None);
                    Cache(cacheKey, malformed);
                    return malformed;
                }

                var observedAtUtc = DateTime.UtcNow;
                var result = new PriceSourceResult
                {
                    Success = true,
                    Price = Math.Round(parsedPrice.Value, 2, MidpointRounding.AwayFromZero),
                    Currency = _options.PreferredCurrency,
                    Source = PriceSourceNames.Steam,
                    PriceType = usingLowest ? PriceTypeNames.LowestListing : PriceTypeNames.MedianSale,
                    Status = usingLowest ? "Live" : "Estimated",
                    IsEstimated = !usingLowest,
                    LastUpdatedUtc = observedAtUtc,
                    ObservedAtUtc = observedAtUtc,
                    ExpiresAtUtc = observedAtUtc.AddMinutes(Math.Max(1, _options.SteamCacheMinutes)),
                    TtlSeconds = Math.Max(1, _options.SteamCacheMinutes) * 60,
                    OriginalPrice = Math.Round(parsedPrice.Value, 2, MidpointRounding.AwayFromZero),
                    OriginalCurrency = "USD",
                    FxRate = 1m,
                    BestAskUsd = usingLowest ? Math.Round(parsedPrice.Value, 2, MidpointRounding.AwayFromZero) : null,
                    Volume = TryParseInt(payload.Volume),
                    ConfidenceScore = usingLowest ? 0.80m : 0.66m,
                    ResolvedMarketHashName = normalizedName,
                    ProvenanceJson = JsonSerializer.Serialize(new
                    {
                        endpoint = "steamcommunity.com/market/priceoverview",
                        lowest_price = payload.LowestPrice,
                        median_price = payload.MedianPrice,
                        volume = payload.Volume,
                        usedField = usingLowest ? "lowest_price" : "median_price"
                    }),
                    RawPayloadHash = Hash(rawContent)
                };

                await _appLogService.WriteAsync("Info", $"Success. Url={requestUri}; MarketHashName={normalizedName}; Price={result.Price}; Currency={result.Currency}", nameof(SteamMarketPriceService), cancellationToken: CancellationToken.None);
                Cache(cacheKey, result);
                return result;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                var failed = Failure("Steam", "Timeout", $"Steam market request timed out after {stopwatch.ElapsedMilliseconds}ms.", normalizedName);
                _logger.LogWarning(
                    exception,
                    "Steam price lookup timed out for {GameType} / {MarketHashName} after {ElapsedMs}ms.",
                    gameType,
                    normalizedName,
                    stopwatch.ElapsedMilliseconds);
                await _appLogService.WriteAsync("Warning", $"Timeout. Url={requestUri}; MarketHashName={normalizedName}; ElapsedMs={stopwatch.ElapsedMilliseconds}; ExceptionType={exception.GetType().Name}; Reason={failed.FailureReason}", nameof(SteamMarketPriceService), exception, CancellationToken.None);
                Cache(cacheKey, failed);
                return failed;
            }
            catch (HttpRequestException exception) when (attempt < _options.SteamRetryCount)
            {
                stopwatch.Stop();
                _logger.LogWarning(exception, "Transient Steam price error for {MarketHashName}. Retrying.", normalizedName);
                await DelayForRetryAsync(cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                stopwatch.Stop();
                var failed = Failure("Steam", "NetworkError", exception.Message, normalizedName);
                await _appLogService.WriteAsync("Error", $"Fail. Url={requestUri}; MarketHashName={normalizedName}; ExceptionType={exception.GetType().Name}; Reason={failed.FailureReason}", nameof(SteamMarketPriceService), exception, CancellationToken.None);
                Cache(cacheKey, failed);
                return failed;
            }
            catch (JsonException exception)
            {
                stopwatch.Stop();
                var failed = Failure("Steam", "MalformedResponse", exception.Message, normalizedName);
                await _appLogService.WriteAsync("Error", $"Parse fail. Url={requestUri}; MarketHashName={normalizedName}; ExceptionType={exception.GetType().Name}; Reason={failed.FailureReason}", nameof(SteamMarketPriceService), exception, CancellationToken.None);
                Cache(cacheKey, failed);
                return failed;
            }
        }

        var exhausted = Failure("Steam", "TransientFailure", "Steam market retry budget exhausted.", normalizedName);
        await _appLogService.WriteAsync("Warning", $"Fail. Url={requestUri}; MarketHashName={normalizedName}; Reason={exhausted.FailureReason}", nameof(SteamMarketPriceService), cancellationToken: CancellationToken.None);
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
            PriceType = PriceTypeNames.Unavailable,
            FailureReason = failureReason,
            Currency = "USD",
            ResolvedMarketHashName = marketHashName,
            LastUpdatedUtc = DateTime.UtcNow
        };
    }

    internal static decimal? ParsePrice(string? rawValue)
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

        var lastDot = cleaned.LastIndexOf('.');
        var lastComma = cleaned.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            var decimalSeparatorIndex = Math.Max(lastDot, lastComma);
            cleaned = NormalizePriceWithDecimalSeparator(cleaned, decimalSeparatorIndex);
        }
        else if (lastComma >= 0)
        {
            cleaned = NormalizeSingleSeparatorPrice(cleaned, ',');
        }
        else if (lastDot >= 0)
        {
            cleaned = NormalizeSingleSeparatorPrice(cleaned, '.');
        }

        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string NormalizePriceWithDecimalSeparator(string value, int decimalSeparatorIndex)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsDigit(character))
            {
                builder.Append(character);
            }
            else if (index == decimalSeparatorIndex)
            {
                builder.Append('.');
            }
        }

        return builder.ToString();
    }

    private static string NormalizeSingleSeparatorPrice(string value, char separator)
    {
        var separatorCount = value.Count(character => character == separator);
        var separatorIndex = value.LastIndexOf(separator);
        var digitsAfter = value.Length - separatorIndex - 1;
        var treatAsThousands = digitsAfter == 3 && separatorCount >= 1;
        if (treatAsThousands)
        {
            return value.Replace(separator.ToString(), string.Empty);
        }

        if (separator == ',')
        {
            return value.Replace(',', '.');
        }

        return separatorCount == 1
            ? value
            : NormalizePriceWithDecimalSeparator(value, separatorIndex);
    }

    private static int? TryParseInt(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var cleaned = new string(rawValue.Where(char.IsDigit).ToArray());
        return int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class SteamMarketPriceResponse
    {
        public bool Success { get; set; }
        public string? LowestPrice { get; set; }
        public string? MedianPrice { get; set; }
        public string? Volume { get; set; }
    }
}
