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

public class SkinportPricingService : ISkinportPricingService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SkinportPricingService> _logger;
    private readonly IGameCatalog _gameCatalog;
    private readonly PricingOptions _options;
    private readonly IAppLogService _appLogService;
    private readonly IPriceDiagnosticLogService _priceDiagnosticLogService;

    public SkinportPricingService(
        HttpClient httpClient,
        IMemoryCache memoryCache,
        ILogger<SkinportPricingService> logger,
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

        if (!game.SupportsSkinportPricing)
        {
            return Failure($"Skinport pricing is not configured for {game.DisplayName}.", normalizedName);
        }

        var endpointDiagnostics = new List<SkinportEndpointDiagnostic>();
        var liveMap = await GetItemsPriceMapAsync(gameType, cancellationToken);
        if (liveMap.TryGetValue(normalizedName, out var liveItem))
        {
            var liveResult = ResolveFromLiveItem(liveItem, normalizedName);
            if (liveResult.Success)
            {
                if (!string.Equals(liveResult.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                {
                    await LogSkinportProblemAsync(
                        "CurrencyMismatch",
                        "skinport/v1/items live",
                        game,
                        normalizedName,
                        $"Skinport live item currency is {liveResult.Currency}, expected USD.",
                        priceType: liveResult.PriceType,
                        priceUsd: liveResult.Price,
                        originalCurrency: liveResult.Currency,
                        confidenceScore: liveResult.ConfidenceScore,
                        details: new { liveItem.Currency, liveItem.MinPrice, liveItem.Quantity },
                        cancellationToken: cancellationToken);
                }

                return liveResult;
            }

            await LogSkinportProblemAsync(
                "SourceReturnedNoUsablePrice",
                "skinport/v1/items live",
                game,
                normalizedName,
                liveResult.FailureReason ?? "Skinport live item returned no usable price.",
                details: new { liveItem.Currency, liveItem.MinPrice, liveItem.Quantity },
                cancellationToken: cancellationToken);
            endpointDiagnostics.Add(BuildEndpointDiagnostic(
                "skinport/v1/items live",
                normalizedName,
                game,
                liveMap.Keys,
                foundRequestedItem: true,
                liveResult.FailureReason ?? "Skinport live item returned no usable price."));
        }
        else
        {
            endpointDiagnostics.Add(BuildEndpointDiagnostic("skinport/v1/items live", normalizedName, game, liveMap.Keys));
        }

        var historyMap = await GetSalesHistoryAsync([normalizedName], gameType, cancellationToken);
        if (historyMap.TryGetValue(normalizedName, out var historyItem))
        {
            var historyResult = ResolveFromHistory(historyItem, normalizedName);
            if (historyResult.Success)
            {
                if (!string.Equals(historyResult.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                {
                    await LogSkinportProblemAsync(
                        "CurrencyMismatch",
                        "skinport/v1/sales/history",
                        game,
                        normalizedName,
                        $"Skinport history currency is {historyResult.Currency}, expected USD.",
                        priceType: historyResult.PriceType,
                        priceUsd: historyResult.Price,
                        originalCurrency: historyResult.Currency,
                        confidenceScore: historyResult.ConfidenceScore,
                        details: new { historyItem.Currency },
                        cancellationToken: cancellationToken);
                }

                return historyResult;
            }

            await LogSkinportProblemAsync(
                "SourceReturnedNoUsablePrice",
                "skinport/v1/sales/history",
                game,
                normalizedName,
                historyResult.FailureReason ?? "Skinport history returned no usable median.",
                details: BuildHistoryDetails(historyItem),
                cancellationToken: cancellationToken);
            endpointDiagnostics.Add(BuildEndpointDiagnostic(
                "skinport/v1/sales/history",
                normalizedName,
                game,
                historyMap.Keys,
                foundRequestedItem: true,
                historyResult.FailureReason ?? "Skinport history returned no usable median."));
        }
        else
        {
            endpointDiagnostics.Add(BuildEndpointDiagnostic("skinport/v1/sales/history", normalizedName, game, historyMap.Keys));
        }

        var outOfStockMap = await GetOutOfStockPriceMapAsync(gameType, cancellationToken);
        if (outOfStockMap.TryGetValue(normalizedName, out var outOfStockItem))
        {
            var outOfStockResult = ResolveFromOutOfStock(outOfStockItem, normalizedName);
            if (outOfStockResult.Success)
            {
                if (!string.Equals(outOfStockResult.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                {
                    await LogSkinportProblemAsync(
                        "CurrencyMismatch",
                        "skinport/v1/items out-of-stock",
                        game,
                        normalizedName,
                        $"Skinport out-of-stock currency is {outOfStockResult.Currency}, expected USD.",
                        priceType: outOfStockResult.PriceType,
                        priceUsd: outOfStockResult.Price,
                        originalCurrency: outOfStockResult.Currency,
                        confidenceScore: outOfStockResult.ConfidenceScore,
                        details: new { outOfStockItem.Currency, outOfStockItem.AvgSalePrice, outOfStockItem.SuggestedPrice, outOfStockItem.SalesLast90Days },
                        cancellationToken: cancellationToken);
                }

                return outOfStockResult;
            }

            await LogSkinportProblemAsync(
                "SourceReturnedNoUsablePrice",
                "skinport/v1/items out-of-stock",
                game,
                normalizedName,
                outOfStockResult.FailureReason ?? "Skinport out-of-stock returned no usable avg_sale_price or suggested_price.",
                details: new { outOfStockItem.Currency, outOfStockItem.AvgSalePrice, outOfStockItem.SuggestedPrice, outOfStockItem.SalesLast90Days },
                cancellationToken: cancellationToken);
            endpointDiagnostics.Add(BuildEndpointDiagnostic(
                "skinport/v1/items out-of-stock",
                normalizedName,
                game,
                outOfStockMap.Keys,
                foundRequestedItem: true,
                outOfStockResult.FailureReason ?? "Skinport out-of-stock returned no usable avg_sale_price or suggested_price."));
        }
        else
        {
            endpointDiagnostics.Add(BuildEndpointDiagnostic("skinport/v1/items out-of-stock", normalizedName, game, outOfStockMap.Keys));
        }

        var allMapsEmpty = liveMap.Count == 0 && historyMap.Count == 0 && outOfStockMap.Count == 0;
        var anyFoundWithoutUsablePrice = endpointDiagnostics.Any(item => item.FoundRequestedItem);
        var failureReason = allMapsEmpty
            ? "Skinport returned empty live/history/out-of-stock responses for this lookup."
            : anyFoundWithoutUsablePrice
                ? "Skinport returned matching item data but no usable live/history/out-of-stock price."
                : "Skinport did not find the requested item in live/history/out-of-stock responses.";
        var details = new
        {
            requestedNormalizedName = normalizedName,
            gameType = (int)game.Type,
            appId = game.SteamAppId,
            endpoints = endpointDiagnostics
        };
        await LogSkinportProblemAsync(
            allMapsEmpty ? "SourceFailed" : anyFoundWithoutUsablePrice ? "SourceReturnedNoUsablePrice" : "SourceNotFound",
            "skinport aggregate",
            game,
            normalizedName,
            failureReason,
            details: details,
            cancellationToken: cancellationToken);

        var failure = Failure(failureReason, normalizedName);
        failure.ProvenanceJson = JsonSerializer.Serialize(details);
        return failure;
    }

    public async Task<IReadOnlyDictionary<string, SkinportItemDto>> GetItemsPriceMapAsync(GameType gameType, CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(gameType);
        var cacheKey = $"skinport-items::{game.Key}::{_options.PreferredCurrency}";
        if (_memoryCache.TryGetValue<IReadOnlyDictionary<string, SkinportItemDto>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var endpoint =
            $"https://api.skinport.com/v1/items?app_id={game.SteamAppId}&currency={Uri.EscapeDataString(_options.PreferredCurrency)}&tradable=1";

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Skinport live items lookup started for {GameType}.", gameType);
        await LogVerboseAppAsync("Info", $"Start live items. Url={endpoint}; GameType={(int)gameType}", nameof(SkinportPricingService), cancellationToken: cancellationToken);
        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            stopwatch.Stop();
            await LogVerboseAppAsync("Info", $"End live items. Http={(int)response.StatusCode}; ElapsedMs={stopwatch.ElapsedMilliseconds}; GameType={(int)gameType}", nameof(SkinportPricingService), cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                await LogSkinportProblemAsync(
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "SourceRateLimited" : "ExternalApiError",
                    "skinport/v1/items live",
                    game,
                    null,
                    $"Skinport live items returned HTTP {(int)response.StatusCode}.",
                    httpStatusCode: (int)response.StatusCode,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: new { gameType = (int)game.Type, appId = game.SteamAppId },
                    cancellationToken: CancellationToken.None);
                await LogVerboseAppAsync("Warning", $"Fail live items. Url={endpoint}; Http={(int)response.StatusCode}; Reason=Skinport items returned HTTP {(int)response.StatusCode}.", nameof(SkinportPricingService), cancellationToken: CancellationToken.None);
                return EmptyItemsMap();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<SkinportItemDto>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            var result = BuildItemsPriceMap(payload ?? []);

            _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Max(1, _options.SkinportItemsCacheMinutes)));
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            await LogSkinportProblemAsync(
                "SourceTimeout",
                "skinport/v1/items live",
                game,
                null,
                $"Skinport live items timed out after {stopwatch.ElapsedMilliseconds}ms.",
                durationMs: stopwatch.ElapsedMilliseconds,
                details: new { exceptionType = exception.GetType().Name },
                cancellationToken: CancellationToken.None);
            await LogVerboseAppAsync("Warning", $"Timeout live items. Url={endpoint}; ElapsedMs={stopwatch.ElapsedMilliseconds}; ExceptionType={exception.GetType().Name}", nameof(SkinportPricingService), exception, CancellationToken.None);
            return EmptyItemsMap();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            stopwatch.Stop();
            await LogSkinportProblemAsync(
                exception is JsonException ? "ParseFailed" : "SourceFailed",
                "skinport/v1/items live",
                game,
                null,
                exception.Message,
                durationMs: stopwatch.ElapsedMilliseconds,
                details: new { exceptionType = exception.GetType().Name },
                cancellationToken: CancellationToken.None);
            await LogVerboseAppAsync("Error", $"Fail live items. Url={endpoint}; ExceptionType={exception.GetType().Name}; Reason={exception.Message}", nameof(SkinportPricingService), exception, CancellationToken.None);
            return EmptyItemsMap();
        }
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

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Skinport history lookup started for {GameType} / {Count} items.", gameType, normalizedNames.Count);
        await LogVerboseAppAsync("Info", $"Start history. Url={endpoint}; GameType={(int)gameType}; Count={normalizedNames.Count}", nameof(SkinportPricingService), cancellationToken: cancellationToken);
        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "Skinport history lookup finished for {GameType} / {Count} items with HTTP {StatusCode} in {ElapsedMs}ms.",
                gameType,
                normalizedNames.Count,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);
            await LogVerboseAppAsync("Info", $"End history. Http={(int)response.StatusCode}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Count={normalizedNames.Count}", nameof(SkinportPricingService), cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Skinport history request failed with status code {StatusCode}.", (int)response.StatusCode);
                await LogSkinportProblemAsync(
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "SourceRateLimited" : "ExternalApiError",
                    "skinport/v1/sales/history",
                    game,
                    string.Join(',', normalizedNames),
                    $"Skinport history returned HTTP {(int)response.StatusCode}.",
                    httpStatusCode: (int)response.StatusCode,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: new { requested = normalizedNames },
                    cancellationToken: CancellationToken.None);
                await LogVerboseAppAsync("Warning", $"Fail history. Url={endpoint}; Http={(int)response.StatusCode}; Reason=Skinport history returned HTTP {(int)response.StatusCode}.", nameof(SkinportPricingService), cancellationToken: CancellationToken.None);
                return EmptyHistoryMap();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<SkinportSalesHistoryDto>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            var result = BuildSalesHistoryMap(payload ?? []);

            _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Max(1, _options.SkinportHistoryCacheMinutes)));
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                exception,
                "Skinport history lookup timed out for {GameType} / {Count} items after {ElapsedMs}ms.",
                gameType,
                normalizedNames.Count,
                stopwatch.ElapsedMilliseconds);
            await LogVerboseAppAsync("Warning", $"Timeout history. Url={endpoint}; ElapsedMs={stopwatch.ElapsedMilliseconds}; ExceptionType={exception.GetType().Name}", nameof(SkinportPricingService), exception, CancellationToken.None);
            await LogSkinportProblemAsync(
                "SourceTimeout",
                "skinport/v1/sales/history",
                game,
                string.Join(',', normalizedNames),
                $"Skinport history timed out after {stopwatch.ElapsedMilliseconds}ms.",
                durationMs: stopwatch.ElapsedMilliseconds,
                details: new { requested = normalizedNames, exceptionType = exception.GetType().Name },
                cancellationToken: CancellationToken.None);
            return EmptyHistoryMap();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            stopwatch.Stop();
            _logger.LogWarning(exception, "Skinport history lookup failed.");
            await LogSkinportProblemAsync(
                exception is JsonException ? "ParseFailed" : "SourceFailed",
                "skinport/v1/sales/history",
                game,
                string.Join(',', normalizedNames),
                exception.Message,
                durationMs: stopwatch.ElapsedMilliseconds,
                details: new { requested = normalizedNames, exceptionType = exception.GetType().Name },
                cancellationToken: CancellationToken.None);
            await LogVerboseAppAsync("Error", $"Fail history. Url={endpoint}; ExceptionType={exception.GetType().Name}; Reason={exception.Message}", nameof(SkinportPricingService), exception, CancellationToken.None);
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

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Skinport out-of-stock lookup started for {GameType}.", gameType);
        await LogVerboseAppAsync("Info", $"Start items. Url={endpoint}; GameType={(int)gameType}", nameof(SkinportPricingService), cancellationToken: cancellationToken);
        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "Skinport out-of-stock lookup finished for {GameType} with HTTP {StatusCode} in {ElapsedMs}ms.",
                gameType,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);
            await LogVerboseAppAsync("Info", $"End items. Http={(int)response.StatusCode}; ElapsedMs={stopwatch.ElapsedMilliseconds}; GameType={(int)gameType}", nameof(SkinportPricingService), cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Skinport items request failed with status code {StatusCode}.", (int)response.StatusCode);
                await LogSkinportProblemAsync(
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "SourceRateLimited" : "ExternalApiError",
                    "skinport/v1/items out-of-stock",
                    game,
                    null,
                    $"Skinport out-of-stock items returned HTTP {(int)response.StatusCode}.",
                    httpStatusCode: (int)response.StatusCode,
                    durationMs: stopwatch.ElapsedMilliseconds,
                    details: new { gameType = (int)game.Type, appId = game.SteamAppId },
                    cancellationToken: CancellationToken.None);
                await LogVerboseAppAsync("Warning", $"Fail items. Url={endpoint}; Http={(int)response.StatusCode}; Reason=Skinport items returned HTTP {(int)response.StatusCode}.", nameof(SkinportPricingService), cancellationToken: CancellationToken.None);
                return EmptyOutOfStockMap();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<SkinportOutOfStockItemDto>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            var result = BuildOutOfStockPriceMap(payload ?? []);

            _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Max(1, _options.SkinportOutOfStockCacheMinutes)));
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                exception,
                "Skinport out-of-stock lookup timed out for {GameType} after {ElapsedMs}ms.",
                gameType,
                stopwatch.ElapsedMilliseconds);
            await LogVerboseAppAsync("Warning", $"Timeout items. Url={endpoint}; ElapsedMs={stopwatch.ElapsedMilliseconds}; ExceptionType={exception.GetType().Name}", nameof(SkinportPricingService), exception, CancellationToken.None);
            await LogSkinportProblemAsync(
                "SourceTimeout",
                "skinport/v1/items out-of-stock",
                game,
                null,
                $"Skinport out-of-stock items timed out after {stopwatch.ElapsedMilliseconds}ms.",
                durationMs: stopwatch.ElapsedMilliseconds,
                details: new { exceptionType = exception.GetType().Name },
                cancellationToken: CancellationToken.None);
            return EmptyOutOfStockMap();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            stopwatch.Stop();
            _logger.LogWarning(exception, "Skinport out-of-stock lookup failed.");
            await LogSkinportProblemAsync(
                exception is JsonException ? "ParseFailed" : "SourceFailed",
                "skinport/v1/items out-of-stock",
                game,
                null,
                exception.Message,
                durationMs: stopwatch.ElapsedMilliseconds,
                details: new { exceptionType = exception.GetType().Name },
                cancellationToken: CancellationToken.None);
            await LogVerboseAppAsync("Error", $"Fail items. Url={endpoint}; ExceptionType={exception.GetType().Name}; Reason={exception.Message}", nameof(SkinportPricingService), exception, CancellationToken.None);
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
                    PriceType = PriceTypeNames.MedianSale,
                    Status = "Estimated",
                    IsEstimated = true,
                    LastUpdatedUtc = DateTime.UtcNow,
                    ObservedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.SkinportHistoryCacheMinutes)),
                    TtlSeconds = Math.Max(1, _options.SkinportHistoryCacheMinutes) * 60,
                    OriginalPrice = Math.Round(median, 2, MidpointRounding.AwayFromZero),
                    OriginalCurrency = item.Currency,
                    FxRate = string.Equals(item.Currency, "USD", StringComparison.OrdinalIgnoreCase) ? 1m : null,
                    Volume = candidate.Window.Volume,
                    SalesCount = candidate.Window.Volume,
                    ConfidenceScore = 0.70m,
                    ResolvedMarketHashName = marketHashName,
                    ProvenanceJson = JsonSerializer.Serialize(new
                    {
                        endpoint = "skinport/v1/sales/history",
                        window = candidate.Status,
                        usedField = "median",
                        currency = item.Currency
                    }),
                    RawPayloadHash = Hash(JsonSerializer.Serialize(item))
                };
            }
        }

        return Failure("Skinport history did not contain a usable median.", marketHashName);
    }

    private PriceSourceResult ResolveFromLiveItem(SkinportItemDto item, string marketHashName)
    {
        var livePrice = TryGetLiveMinPriceUsd(item);
        if (!livePrice.HasValue)
        {
            return Failure("Skinport live items did not contain min_price with quantity > 0.", marketHashName);
        }

        var observedAtUtc = item.UpdatedAt.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(item.UpdatedAt.Value).UtcDateTime
            : DateTime.UtcNow;
        var priceUsd = livePrice.Value;
        return new PriceSourceResult
        {
            Success = true,
            Price = priceUsd,
            Currency = item.Currency,
            Source = PriceSourceNames.Skinport,
            PriceType = PriceTypeNames.LowestListing,
            Status = "Live",
            LastUpdatedUtc = DateTime.UtcNow,
            ObservedAtUtc = observedAtUtc,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.SkinportItemsCacheMinutes)),
            TtlSeconds = Math.Max(1, _options.SkinportItemsCacheMinutes) * 60,
            OriginalPrice = priceUsd,
            OriginalCurrency = item.Currency,
            FxRate = string.Equals(item.Currency, "USD", StringComparison.OrdinalIgnoreCase) ? 1m : null,
            Quantity = item.Quantity,
            BestAskUsd = priceUsd,
            ConfidenceScore = 0.94m,
            ResolvedMarketHashName = marketHashName,
            ProvenanceJson = JsonSerializer.Serialize(new
            {
                endpoint = "skinport/v1/items",
                usedField = "min_price",
                quantity = item.Quantity,
                median_price = item.MedianPrice,
                mean_price = item.MeanPrice,
                suggested_price = item.SuggestedPrice
            }),
            RawPayloadHash = Hash(JsonSerializer.Serialize(item))
        };
    }

    private PriceSourceResult ResolveFromOutOfStock(SkinportOutOfStockItemDto item, string marketHashName)
    {
        var price = TryGetOutOfStockEstimateUsd(item);
        if (!price.HasValue || price <= 0)
        {
            return Failure("Skinport out-of-stock data did not contain a usable price.", marketHashName);
        }

        var isSuggested = !item.AvgSalePrice.HasValue && item.SuggestedPrice.HasValue;
        var observedAtUtc = DateTime.UtcNow;
        return new PriceSourceResult
        {
            Success = true,
            Price = Math.Round(price.Value, 2, MidpointRounding.AwayFromZero),
            Currency = item.Currency,
            Source = PriceSourceNames.Skinport,
            PriceType = isSuggested ? PriceTypeNames.Suggested : PriceTypeNames.AvgSale,
            Status = "Estimated",
            IsEstimated = true,
            LastUpdatedUtc = observedAtUtc,
            ObservedAtUtc = observedAtUtc,
            ExpiresAtUtc = observedAtUtc.AddMinutes(Math.Max(1, _options.SkinportOutOfStockCacheMinutes)),
            TtlSeconds = Math.Max(1, _options.SkinportOutOfStockCacheMinutes) * 60,
            OriginalPrice = Math.Round(price.Value, 2, MidpointRounding.AwayFromZero),
            OriginalCurrency = item.Currency,
            FxRate = string.Equals(item.Currency, "USD", StringComparison.OrdinalIgnoreCase) ? 1m : null,
            SalesCount = item.SalesLast90Days,
            ConfidenceScore = isSuggested ? 0.42m : 0.60m,
            ResolvedMarketHashName = marketHashName,
            ProvenanceJson = JsonSerializer.Serialize(new
            {
                endpoint = "skinport/v1/items",
                mode = "out_of_stock",
                usedField = isSuggested ? "suggested_price" : "avg_sale_price",
                avg_sale_price = item.AvgSalePrice,
                suggested_price = item.SuggestedPrice,
                sales_last_90d = item.SalesLast90Days
            }),
            RawPayloadHash = Hash(JsonSerializer.Serialize(item))
        };
    }

    private static PriceSourceResult Failure(string failureReason, string? marketHashName)
    {
        return new PriceSourceResult
        {
            Source = PriceSourceNames.Skinport,
            Status = "Unavailable",
            PriceType = PriceTypeNames.Unavailable,
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

    private static IReadOnlyDictionary<string, SkinportItemDto> EmptyItemsMap()
    {
        return new Dictionary<string, SkinportItemDto>(StringComparer.Ordinal);
    }

    internal static decimal? TryGetLiveMinPriceUsd(SkinportItemDto item)
    {
        return item.Quantity is > 0 && item.MinPrice is > 0
            ? Math.Round(item.MinPrice.Value, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    internal static decimal? TryGetOutOfStockEstimateUsd(SkinportOutOfStockItemDto item)
    {
        var price = item.AvgSalePrice ?? item.SuggestedPrice;
        return price is > 0
            ? Math.Round(price.Value, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    internal static IReadOnlyDictionary<string, SkinportItemDto> BuildItemsPriceMap(IEnumerable<SkinportItemDto> items)
    {
        return items
            .Select(item => new { Key = MarketHashNameUtility.Normalize(item.MarketHashName), Item = item })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => item.Item)
                    .OrderByDescending(item => item.Quantity is > 0)
                    .ThenBy(item => item.Quantity is > 0 && item.MinPrice is > 0 ? item.MinPrice.Value : decimal.MaxValue)
                    .First(),
                StringComparer.Ordinal);
    }

    internal static IReadOnlyDictionary<string, SkinportSalesHistoryDto> BuildSalesHistoryMap(IEnumerable<SkinportSalesHistoryDto> items)
    {
        return items
            .Select(item => new { Key = MarketHashNameUtility.Normalize(item.MarketHashName), Item = item })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => item.Item)
                    .OrderBy(item => GetHistoryRank(item))
                    .First(),
                StringComparer.Ordinal);
    }

    internal static IReadOnlyDictionary<string, SkinportOutOfStockItemDto> BuildOutOfStockPriceMap(IEnumerable<SkinportOutOfStockItemDto> items)
    {
        return items
            .Select(item => new { Key = MarketHashNameUtility.Normalize(item.MarketHashName), Item = item })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => item.Item)
                    .OrderByDescending(item => item.AvgSalePrice is > 0)
                    .ThenByDescending(item => item.SuggestedPrice is > 0)
                    .First(),
                StringComparer.Ordinal);
    }

    private static SkinportEndpointDiagnostic BuildEndpointDiagnostic(
        string endpointName,
        string normalizedName,
        GameDefinition game,
        IEnumerable<string> returnedKeys,
        bool foundRequestedItem = false,
        string? reason = null)
    {
        var keyList = returnedKeys.ToList();
        return new SkinportEndpointDiagnostic
        {
            Endpoint = endpointName,
            Requested = normalizedName,
            GameType = (int)game.Type,
            AppId = game.SteamAppId,
            FoundRequestedItem = foundRequestedItem,
            ReturnedCount = keyList.Count,
            MapEmpty = keyList.Count == 0,
            SampleKeys = foundRequestedItem || keyList.Count == 0 ? [] : keyList.Take(10).ToList(),
            Reason = reason ?? (foundRequestedItem ? null : $"Requested item was not found in {endpointName}.")
        };
    }

    private static int GetHistoryRank(SkinportSalesHistoryDto item)
    {
        if (HasUsableHistory(item.Last7Days))
        {
            return 0;
        }

        if (HasUsableHistory(item.Last30Days))
        {
            return 1;
        }

        return HasUsableHistory(item.Last90Days) ? 2 : 3;
    }

    private static bool HasUsableHistory(SkinportSalesWindowDto? window)
    {
        return window?.Median is > 0 && window.Volume is > 0;
    }

    private Task LogSkinportProblemAsync(
        string eventType,
        string endpoint,
        GameDefinition game,
        string? marketHashName,
        string failureReason,
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
            PriceSourceNames.Skinport,
            failureReason,
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

    private static object BuildHistoryDetails(SkinportSalesHistoryDto item)
    {
        return new
        {
            item.Currency,
            last7Days = new { item.Last7Days?.Median, item.Last7Days?.Volume },
            last30Days = new { item.Last30Days?.Median, item.Last30Days?.Volume },
            last90Days = new { item.Last90Days?.Median, item.Last90Days?.Volume }
        };
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class SkinportEndpointDiagnostic
    {
        public string Endpoint { get; init; } = string.Empty;
        public string Requested { get; init; } = string.Empty;
        public int GameType { get; init; }
        public int AppId { get; init; }
        public bool FoundRequestedItem { get; init; }
        public int ReturnedCount { get; init; }
        public bool MapEmpty { get; init; }
        public IReadOnlyList<string> SampleKeys { get; init; } = [];
        public string? Reason { get; init; }
    }
}
