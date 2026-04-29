using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamInventoryService : ISteamInventoryService
{
    private const string IconBaseUrl = "https://community.akamai.steamstatic.com/economy/image/";
    private static readonly TimeSpan FreshCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StaleCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BotRateLimitCooldown = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RequestLocks = new(StringComparer.Ordinal);
    private const int BodySnippetLength = 280;
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SteamInventoryService> _logger;
    private readonly IGameCatalog _gameCatalog;
    private readonly IAppLogService _appLogService;
    private readonly ISteamBotInventoryClient _botInventoryClient;

    public SteamInventoryService(
        HttpClient httpClient,
        IMemoryCache memoryCache,
        ILogger<SteamInventoryService> logger,
        IGameCatalog gameCatalog,
        IAppLogService appLogService,
        ISteamBotInventoryClient botInventoryClient)
    {
        _httpClient = httpClient;
        _memoryCache = memoryCache;
        _logger = logger;
        _gameCatalog = gameCatalog;
        _appLogService = appLogService;
        _botInventoryClient = botInventoryClient;
    }

    public async Task<SteamInventoryResultDto> GetInventoryAsync(string steamId, GameType gameType, CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(gameType);
        var freshCacheKey = $"steam-inventory::{game.Key}::{steamId}";
        var staleCacheKey = $"steam-inventory-stale::{game.Key}::{steamId}";
        var cooldownKey = $"steam-inventory-cooldown::{game.Key}::{steamId}";
        var botCooldownKey = $"steam-inventory-bot-cooldown::{game.Key}::{steamId}";
        var requestId = Guid.NewGuid().ToString("N")[..8];
        if (_memoryCache.TryGetValue<SteamInventoryResultDto>(freshCacheKey, out var cachedInventory) && cachedInventory is not null)
        {
            _logger.LogInformation("Steam inventory cache hit for SteamId {SteamId} and game {GameKey}.", steamId, game.Key);
            await WriteInventoryLogAsync(
                "Info",
                $"Cache hit. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ItemCount={cachedInventory.Items.Count}",
                cancellationToken: cancellationToken);
            return cachedInventory;
        }

        var requestLock = RequestLocks.GetOrAdd(freshCacheKey, _ => new SemaphoreSlim(1, 1));
        await requestLock.WaitAsync(cancellationToken);
        try
        {
            if (_memoryCache.TryGetValue<SteamInventoryResultDto>(freshCacheKey, out cachedInventory) && cachedInventory is not null)
            {
                _logger.LogInformation("Steam inventory cache hit for SteamId {SteamId} and game {GameKey}.", steamId, game.Key);
                await WriteInventoryLogAsync(
                    "Info",
                    $"Cache hit after lock. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ItemCount={cachedInventory.Items.Count}",
                    cancellationToken: cancellationToken);
                return cachedInventory;
            }

            if (_memoryCache.TryGetValue<DateTimeOffset>(cooldownKey, out var cooldownUntil) &&
                cooldownUntil > DateTimeOffset.UtcNow)
            {
                if (_memoryCache.TryGetValue<SteamInventoryResultDto>(staleCacheKey, out var staleDuringCooldown) &&
                    staleDuringCooldown is not null)
                {
                    await WriteInventoryLogAsync(
                        "Warning",
                        $"Cooldown active. Returning stale inventory. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; CooldownUntil={cooldownUntil.UtcDateTime:O}; ItemCount={staleDuringCooldown.Items.Count}",
                        cancellationToken: cancellationToken);
                    return staleDuringCooldown;
                }

                await WriteInventoryLogAsync(
                    "Warning",
                    $"Cooldown active without stale cache. Steam request and bot fallback skipped to avoid extending Steam rate limits. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; CooldownUntil={cooldownUntil.UtcDateTime:O}",
                    cancellationToken: cancellationToken);
                return BuildRateLimitedResult();
            }

            _logger.LogInformation("Steam inventory cache miss for SteamId {SteamId} and game {GameKey}.", steamId, game.Key);
            var requestUrl = $"https://steamcommunity.com/inventory/{steamId}/{game.SteamAppId}/{game.SteamContextId}?l=english&count=2000";
            var stopwatch = Stopwatch.StartNew();
            string? rawContent = null;
            var hostingContext = BuildHostingContext();

            await WriteInventoryLogAsync(
                "Info",
                $"Request started. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; AppId={game.SteamAppId}; ContextId={game.SteamContextId}; Url={requestUrl}; HostContext={hostingContext}",
                cancellationToken: cancellationToken);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{steamId}/inventory");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await WriteInventoryLogAsync(
                "Info",
                $"Headers received. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Http={(int)response.StatusCode}; ElapsedMs={stopwatch.ElapsedMilliseconds}; ContentLength={response.Content.Headers.ContentLength?.ToString() ?? "<null>"}; ContentType={response.Content.Headers.ContentType?.ToString() ?? "<null>"}; RetryAfter={GetRetryAfterValue(response)}; HostContext={hostingContext}",
                cancellationToken: cancellationToken);

            if ((int)response.StatusCode == 429)
            {
                _logger.LogWarning("Steam inventory request returned 429 for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
                var retryAfter = GetRetryAfterValue(response);
                var bodySnippet = await ReadBodySnippetAsync(response, cancellationToken);
                await WriteInventoryLogAsync(
                    "Warning",
                    $"Rate limited. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Http=429; RetryAfter={retryAfter}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}; HostContext={hostingContext}; Body={bodySnippet}",
                    cancellationToken: cancellationToken);

                _memoryCache.Set(cooldownKey, DateTimeOffset.UtcNow.Add(RateLimitCooldown), RateLimitCooldown);

                if (_memoryCache.TryGetValue<SteamInventoryResultDto>(staleCacheKey, out var staleInventory) && staleInventory is not null)
                {
                    _logger.LogWarning("Falling back to cached Steam inventory after 429 for SteamId {SteamId}.", steamId);
                    await WriteInventoryLogAsync(
                        "Warning",
                        $"Stale cache returned after 429. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ItemCount={staleInventory.Items.Count}; CooldownMinutes={(int)RateLimitCooldown.TotalMinutes}",
                        cancellationToken: cancellationToken);
                    return staleInventory;
                }

                var botResult = await TryGetBotFallbackAsync(
                    steamId,
                    gameType,
                    requestId,
                    game.Key,
                    "Steam returned HTTP 429",
                    botCooldownKey,
                    cancellationToken);
                if (botResult is { IsSuccess: true })
                {
                    CacheInventory(freshCacheKey, staleCacheKey, botResult);
                    return botResult;
                }

                return BuildRateLimitedResult();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Steam inventory request returned 403 for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
                await WriteInventoryLogAsync(
                    "Warning",
                    $"Forbidden or private inventory. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Http=403; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}; Body={await ReadBodySnippetAsync(response, cancellationToken)}",
                    cancellationToken: cancellationToken);
                return new SteamInventoryResultDto
                {
                    ErrorMessage = "Steam inventory is private or unavailable. Status code: 403."
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Steam inventory request failed for SteamId {SteamId}. Status code: {StatusCode}. Endpoint: {RequestUrl}",
                    steamId,
                    (int)response.StatusCode,
                    requestUrl);
                await WriteInventoryLogAsync(
                    "Warning",
                    $"HTTP failure. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Http={(int)response.StatusCode}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}; Body={await ReadBodySnippetAsync(response, cancellationToken)}",
                    cancellationToken: cancellationToken);
                return new SteamInventoryResultDto
                {
                    ErrorMessage = $"Steam inventory request failed. Status code: {(int)response.StatusCode}."
                };
            }

            rawContent = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(rawContent);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("Steam inventory response root is not an object for SteamId {SteamId}.", steamId);
                await WriteInventoryLogAsync(
                    "Warning",
                    $"Invalid payload root. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}; Body={BuildBodySnippet(rawContent)}",
                    cancellationToken: cancellationToken);
                return new SteamInventoryResultDto
                {
                    ErrorMessage = "Steam inventory response was empty or invalid."
                };
            }

            if (root.TryGetProperty("success", out var successElement) &&
                successElement.ValueKind == JsonValueKind.Number &&
                successElement.GetInt32() != 1)
            {
                _logger.LogWarning("Steam inventory response reported success != 1 for SteamId {SteamId}.", steamId);
                await WriteInventoryLogAsync(
                    "Warning",
                    $"Payload returned success != 1. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; SuccessValue={successElement.GetInt32()}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}; Body={BuildBodySnippet(rawContent)}",
                    cancellationToken: cancellationToken);
                return new SteamInventoryResultDto
                {
                    ErrorMessage = "Steam inventory is unavailable or private."
                };
            }

            var totalInventoryCount = GetInt32(root, "total_inventory_count");
            if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Steam inventory response does not contain a valid assets array for SteamId {SteamId}.", steamId);
                await WriteInventoryLogAsync(
                    "Warning",
                    $"Assets array missing. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; TotalInventoryCount={FormatInt(totalInventoryCount)}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}; HostContext={hostingContext}; Body={BuildBodySnippet(rawContent)}",
                    cancellationToken: cancellationToken);

                if (totalInventoryCount == 0)
                {
                    var emptyResult = new SteamInventoryResultDto
                    {
                        IsSuccess = true
                    };
                    CacheInventory(freshCacheKey, staleCacheKey, emptyResult);
                    await WriteInventoryLogAsync(
                        "Info",
                        $"Assets array omitted for empty Steam inventory. Returning empty result. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; FreshCacheSeconds={(int)FreshCacheDuration.TotalSeconds}; StaleCacheHours={(int)StaleCacheDuration.TotalHours}",
                        cancellationToken: cancellationToken);
                    return emptyResult;
                }

                var botResult = await TryGetBotFallbackAsync(
                    steamId,
                    gameType,
                    requestId,
                    game.Key,
                    $"Steam response omitted assets. TotalInventoryCount={FormatInt(totalInventoryCount)}",
                    botCooldownKey,
                    cancellationToken);
                if (botResult is { IsSuccess: true })
                {
                    CacheInventory(freshCacheKey, staleCacheKey, botResult);
                    return botResult;
                }

                if (_memoryCache.TryGetValue<SteamInventoryResultDto>(staleCacheKey, out var staleInventory) &&
                    staleInventory is not null)
                {
                    await WriteInventoryLogAsync(
                        "Warning",
                        $"Stale cache returned after incomplete Steam payload. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ItemCount={staleInventory.Items.Count}",
                        cancellationToken: cancellationToken);
                    return staleInventory;
                }

                return new SteamInventoryResultDto
                {
                    ErrorMessage = "Steam inventory response is incomplete: assets were not returned."
                };
            }

            var rawAssetCount = assetsElement.GetArrayLength();
            var descriptions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var rawDescriptionCount = 0;
            if (root.TryGetProperty("descriptions", out var descriptionsElement) &&
                descriptionsElement.ValueKind == JsonValueKind.Array)
            {
                rawDescriptionCount = descriptionsElement.GetArrayLength();
                foreach (var description in descriptionsElement.EnumerateArray())
                {
                    var classId = GetString(description, "classid");
                    var instanceId = GetString(description, "instanceid");
                    if (string.IsNullOrWhiteSpace(classId) || string.IsNullOrWhiteSpace(instanceId))
                    {
                        continue;
                    }

                    descriptions[$"{classId}_{instanceId}"] = description;
                }
            }
            else
            {
                _logger.LogInformation("Steam inventory response did not include descriptions for SteamId {SteamId}. Items will use fallback values.", steamId);
                await WriteInventoryLogAsync(
                    "Info",
                    $"Descriptions are missing. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}",
                    cancellationToken: cancellationToken);
            }

            await WriteInventoryLogAsync(
                "Info",
                $"Payload parsed. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Success={FormatInt(GetInt32(root, "success"))}; TotalInventoryCount={FormatInt(GetInt32(root, "total_inventory_count"))}; AssetCount={rawAssetCount}; DescriptionArrayCount={rawDescriptionCount}; LastAssetId={GetString(root, "last_assetid") ?? "<null>"}; MoreItems={FormatNullableBool(GetBooleanFlag(root, "more_items"))}",
                cancellationToken: cancellationToken);

            var items = new List<SteamInventoryItemDto>();
            var missingDescriptionCount = 0;
            var missingMarketNameCount = 0;
            var skippedAssetsWithoutAssetId = 0;
            var sampleItems = new List<string>();
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var assetId = GetString(asset, "assetid");
                var classId = GetString(asset, "classid") ?? string.Empty;
                var instanceId = GetString(asset, "instanceid") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(assetId))
                {
                    skippedAssetsWithoutAssetId++;
                    continue;
                }

                descriptions.TryGetValue($"{classId}_{instanceId}", out var description);
                var hasDescription = description.ValueKind != JsonValueKind.Undefined;
                if (!hasDescription)
                {
                    missingDescriptionCount++;
                }

                var marketHashName = MarketHashNameUtility.Normalize(GetString(description, "market_hash_name"));
                var marketName = MarketHashNameUtility.Normalize(GetString(description, "market_name"));
                if (string.IsNullOrWhiteSpace(marketHashName) && string.IsNullOrWhiteSpace(marketName))
                {
                    missingMarketNameCount++;
                }

                var item = new SteamInventoryItemDto
                {
                    GameType = gameType,
                    AssetId = assetId,
                    ClassId = classId,
                    InstanceId = instanceId,
                    Name = GetString(description, "name") ?? "Unknown Item",
                    MarketHashName = marketHashName,
                    MarketName = marketName,
                    IconUrl = BuildIconUrl(GetString(description, "icon_url")),
                    Tradable = GetBooleanFlag(description, "tradable"),
                    Marketable = GetBooleanFlag(description, "marketable")
                };
                if (sampleItems.Count < 5)
                {
                    sampleItems.Add(
                        $"Asset={item.AssetId}; Name={Truncate(item.Name, 60)}; Hash={item.MarketHashName ?? item.MarketName ?? "<null>"}; Tradable={FormatNullableBool(item.Tradable)}; Marketable={FormatNullableBool(item.Marketable)}; HasDescription={hasDescription}");
                }

                items.Add(item);
            }

            var result = new SteamInventoryResultDto
            {
                IsSuccess = true,
                Items = items
            };

            CacheInventory(freshCacheKey, staleCacheKey, result);
            await WriteInventoryLogAsync(
                items.Count == 0 ? "Warning" : "Info",
                $"Request finished. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ItemCount={items.Count}; TotalInventoryCount={FormatInt(GetInt32(root, "total_inventory_count"))}; AssetCount={rawAssetCount}; DescriptionCount={descriptions.Count}; MissingDescriptionCount={missingDescriptionCount}; MissingMarketNameCount={missingMarketNameCount}; SkippedAssetsWithoutAssetId={skippedAssetsWithoutAssetId}; TradableCount={items.Count(item => item.Tradable == true)}; MarketableCount={items.Count(item => item.Marketable == true)}; Sample={FormatSample(sampleItems)}; FreshCacheSeconds={(int)FreshCacheDuration.TotalSeconds}; StaleCacheHours={(int)StaleCacheDuration.TotalHours}; ElapsedMs={stopwatch.ElapsedMilliseconds}{(items.Count == 0 ? $"; Body={BuildBodySnippet(rawContent)}" : string.Empty)}",
                cancellationToken: cancellationToken);
            return result;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Steam inventory request was canceled by the caller for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
            await WriteInventoryLogAsync(
                "Warning",
                $"Request canceled by caller. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}",
                exception,
                CancellationToken.None);
            return new SteamInventoryResultDto
            {
                ErrorMessage = "Steam inventory request was canceled before Steam responded."
            };
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(exception, "Steam inventory request timed out or was canceled before completion for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
            await WriteInventoryLogAsync(
                "Warning",
                $"Request timed out or was canceled before completion. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; TimeoutSeconds={(int)_httpClient.Timeout.TotalSeconds}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}",
                exception,
                CancellationToken.None);
            return new SteamInventoryResultDto
            {
                ErrorMessage = "Steam inventory request timed out before Steam responded."
            };
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "Steam inventory HTTP request failed for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
            await WriteInventoryLogAsync(
                "Error",
                $"HTTP request exception. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}; ExceptionType={exception.GetType().Name}; Message={exception.Message}",
                exception,
                cancellationToken);
            return new SteamInventoryResultDto
            {
                ErrorMessage = $"Failed to reach Steam inventory endpoint: {exception.Message}"
            };
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Steam inventory JSON parsing failed for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
            await WriteInventoryLogAsync(
                "Error",
                $"JSON parsing failed. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}; Body={BuildBodySnippet(rawContent)}",
                exception,
                cancellationToken);
            return new SteamInventoryResultDto
            {
                ErrorMessage = "Steam inventory returned invalid JSON."
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected Steam inventory error for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
            await WriteInventoryLogAsync(
                "Error",
                $"Unexpected error. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={requestUrl}; ExceptionType={exception.GetType().Name}; Message={exception.Message}",
                exception,
                cancellationToken);
            return new SteamInventoryResultDto
            {
                ErrorMessage = $"Steam inventory failed unexpectedly: {exception.GetType().Name}."
            };
        }
        }
        finally
        {
            requestLock.Release();
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
    }

    private static bool? GetBooleanFlag(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetInt32() == 1,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? BuildIconUrl(string? iconPath)
    {
        return string.IsNullOrWhiteSpace(iconPath) ? null : $"{IconBaseUrl}{iconPath}";
    }

    private void CacheInventory(string freshCacheKey, string staleCacheKey, SteamInventoryResultDto result)
    {
        _memoryCache.Set(freshCacheKey, result, FreshCacheDuration);
        _memoryCache.Set(staleCacheKey, result, StaleCacheDuration);
    }

    private static SteamInventoryResultDto BuildRateLimitedResult()
    {
        return new SteamInventoryResultDto
        {
            ErrorMessage = "Steam is temporarily rate-limiting inventory requests. Please try again in a few minutes."
        };
    }

    private async Task<SteamInventoryResultDto?> TryGetBotFallbackAsync(
        string steamId,
        GameType gameType,
        string requestId,
        string gameKey,
        string reason,
        string botCooldownKey,
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue<DateTimeOffset>(botCooldownKey, out var botCooldownUntil) &&
            botCooldownUntil > DateTimeOffset.UtcNow)
        {
            await WriteInventoryLogAsync(
                "Warning",
                $"Bot inventory fallback skipped because bot cooldown is active. RequestId={requestId}; SteamId={steamId}; Game={gameKey}; CooldownUntil={botCooldownUntil.UtcDateTime:O}; Reason={reason}",
                cancellationToken: cancellationToken);
            return null;
        }

        await WriteInventoryLogAsync(
            "Info",
            $"Trying bot inventory fallback. RequestId={requestId}; SteamId={steamId}; Game={gameKey}; Reason={reason}",
            cancellationToken: cancellationToken);

        var result = await _botInventoryClient.GetInventoryAsync(steamId, gameType, cancellationToken);
        if (result.IsSuccess)
        {
            await WriteInventoryLogAsync(
                "Info",
                $"Bot inventory fallback succeeded. RequestId={requestId}; SteamId={steamId}; Game={gameKey}; ItemCount={result.Items.Count}",
                cancellationToken: cancellationToken);
            return result;
        }

        await WriteInventoryLogAsync(
            "Warning",
            $"Bot inventory fallback failed. RequestId={requestId}; SteamId={steamId}; Game={gameKey}; Error={result.ErrorMessage ?? "<null>"}",
            cancellationToken: cancellationToken);
        if (IsRateLimitMessage(result.ErrorMessage))
        {
            var cooldownUntil = DateTimeOffset.UtcNow.Add(BotRateLimitCooldown);
            _memoryCache.Set(botCooldownKey, cooldownUntil, BotRateLimitCooldown);
            await WriteInventoryLogAsync(
                "Warning",
                $"Bot inventory fallback rate limited. Bot cooldown activated. RequestId={requestId}; SteamId={steamId}; Game={gameKey}; CooldownUntil={cooldownUntil.UtcDateTime:O}; CooldownMinutes={(int)BotRateLimitCooldown.TotalMinutes}",
                cancellationToken: cancellationToken);
        }

        return null;
    }

    private static bool IsRateLimitMessage(string? message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
               (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("rate limited", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("429", StringComparison.OrdinalIgnoreCase));
    }

    private Task WriteInventoryLogAsync(
        string level,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        return _appLogService.WriteAsync(level, Truncate(message, 3900), nameof(SteamInventoryService), exception, CancellationToken.None);
    }

    private static async Task<string> ReadBodySnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return BuildBodySnippet(body);
    }

    private static string GetRetryAfterValue(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return $"{Math.Max(0, (int)delta.TotalSeconds)}s";
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            return date.UtcDateTime.ToString("O");
        }

        return "<none>";
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            return null;
        }

        return value;
    }

    private static string BuildHostingContext()
    {
        var renderService = FirstConfiguredEnvironmentValue("RENDER_SERVICE_NAME", "RENDER_SERVICE_ID");
        var renderRegion = FirstConfiguredEnvironmentValue("RENDER_REGION");
        var renderCommit = FirstConfiguredEnvironmentValue("RENDER_GIT_COMMIT");
        var isRender = HasEnvironmentValue("RENDER") ||
                       !string.IsNullOrWhiteSpace(renderService) ||
                       !string.IsNullOrWhiteSpace(renderRegion);

        return string.Join("; ", new[]
        {
            $"Render={(isRender ? "Yes" : "No")}",
            $"Service={renderService ?? "<null>"}",
            $"Region={renderRegion ?? "<null>"}",
            $"Commit={Truncate(renderCommit ?? "<null>", 12)}",
            $"Environment={FirstConfiguredEnvironmentValue("ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT") ?? "<null>"}",
            $"Machine={Environment.MachineName}"
        });
    }

    private static bool HasEnvironmentValue(string key)
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key));
    }

    private static string? FirstConfiguredEnvironmentValue(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string FormatInt(int? value)
    {
        return value?.ToString() ?? "<null>";
    }

    private static string FormatNullableBool(bool? value)
    {
        return value.HasValue ? value.Value.ToString() : "<null>";
    }

    private static string FormatSample(IReadOnlyCollection<string> items)
    {
        return items.Count == 0 ? "<none>" : string.Join(" | ", items);
    }

    private static string BuildBodySnippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        var compact = body
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);

        while (compact.Contains("  ", StringComparison.Ordinal))
        {
            compact = compact.Replace("  ", " ", StringComparison.Ordinal);
        }

        return Truncate(compact.Trim(), BodySnippetLength);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }
}
