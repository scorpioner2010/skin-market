using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamInventoryService : ISteamInventoryService
{
    private const string IconBaseUrl = "https://community.akamai.steamstatic.com/economy/image/";
    private static readonly TimeSpan FreshCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StaleCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan PersistentStaleCacheDuration = TimeSpan.FromDays(3);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BotRateLimitCooldown = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RequestLocks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const int BodySnippetLength = 280;
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SteamInventoryService> _logger;
    private readonly IGameCatalog _gameCatalog;
    private readonly IAppLogService _appLogService;
    private readonly ISteamBotInventoryClient _botInventoryClient;
    private readonly IServiceScopeFactory _scopeFactory;

    public SteamInventoryService(
        HttpClient httpClient,
        IMemoryCache memoryCache,
        ILogger<SteamInventoryService> logger,
        IGameCatalog gameCatalog,
        IAppLogService appLogService,
        ISteamBotInventoryClient botInventoryClient,
        IServiceScopeFactory scopeFactory)
    {
        _httpClient = httpClient;
        _memoryCache = memoryCache;
        _logger = logger;
        _gameCatalog = gameCatalog;
        _appLogService = appLogService;
        _botInventoryClient = botInventoryClient;
        _scopeFactory = scopeFactory;
    }

    public async Task<SteamInventoryResultDto> GetInventoryAsync(string steamId, GameType gameType, CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(gameType);
        var freshCacheKey = $"steam-inventory::{game.Key}::{steamId}";
        var staleCacheKey = $"steam-inventory-stale::{game.Key}::{steamId}";
        var cooldownKey = $"steam-inventory-cooldown::{game.Key}::{steamId}";
        var globalCooldownKey = "steam-inventory-cooldown::global";
        var botCooldownKey = $"steam-inventory-bot-cooldown::{game.Key}::{steamId}";
        var botGlobalCooldownKey = "steam-inventory-bot-cooldown::global";
        var requestId = Guid.NewGuid().ToString("N")[..8];
        await WriteInventoryLogAsync(
            "Info",
            $"Inventory load decision started. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; AppId={game.SteamAppId}; ContextId={game.SteamContextId}; FreshCacheSeconds={(int)FreshCacheDuration.TotalSeconds}; StaleCacheHours={(int)StaleCacheDuration.TotalHours}; PersistentCacheHours={(int)PersistentStaleCacheDuration.TotalHours}; RateLimitCooldownMinutes={(int)RateLimitCooldown.TotalMinutes}; BotCooldownMinutes={(int)BotRateLimitCooldown.TotalMinutes}; HostContext={BuildHostingContext()}",
            cancellationToken: cancellationToken);

        if (_memoryCache.TryGetValue<SteamInventoryResultDto>(freshCacheKey, out var cachedInventory) && cachedInventory is not null)
        {
            _logger.LogInformation("Steam inventory cache hit for SteamId {SteamId} and game {GameKey}.", steamId, game.Key);
            await WriteInventoryLogAsync(
                "Info",
                $"Cache hit. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ItemCount={cachedInventory.Items.Count}",
                cancellationToken: cancellationToken);
            return cachedInventory;
        }

        await WriteInventoryLogAsync(
            "Info",
            $"Fresh memory cache miss before lock. RequestId={requestId}; SteamId={steamId}; Game={game.Key}",
            cancellationToken: cancellationToken);

        var requestLock = RequestLocks.GetOrAdd(freshCacheKey, _ => new SemaphoreSlim(1, 1));
        var lockStopwatch = Stopwatch.StartNew();
        await requestLock.WaitAsync(cancellationToken);
        await WriteInventoryLogAsync(
            "Info",
            $"Inventory request lock acquired. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; WaitMs={lockStopwatch.ElapsedMilliseconds}",
            cancellationToken: cancellationToken);
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

            await LogMemoryCacheDiagnosticsAsync(
                freshCacheKey,
                staleCacheKey,
                cooldownKey,
                globalCooldownKey,
                botCooldownKey,
                botGlobalCooldownKey,
                requestId,
                steamId,
                game.Key,
                cancellationToken);

            await LogPersistentCacheSnapshotAsync(
                steamId,
                game,
                requestId,
                "After lock and before live Steam request.",
                cancellationToken);

            var cooldownUntil = GetActiveCooldownUntil(cooldownKey, globalCooldownKey);
            if (cooldownUntil is not null)
            {
                if (await TryReturnMemoryStaleInventoryAsync(
                        staleCacheKey,
                        requestId,
                        steamId,
                        game.Key,
                        $"Cooldown active. CooldownUntil={cooldownUntil.Value.UtcDateTime:O}",
                        cancellationToken) is { } memoryStaleDuringCooldown)
                {
                    return memoryStaleDuringCooldown;
                }

                if (await TryLoadPersistentStaleInventoryAsync(
                        steamId,
                        gameType,
                        game,
                        staleCacheKey,
                        requestId,
                        $"Cooldown active. CooldownUntil={cooldownUntil.Value.UtcDateTime:O}",
                        cancellationToken) is { } persistentStaleDuringCooldown)
                {
                    return persistentStaleDuringCooldown;
                }

                await WriteInventoryLogAsync(
                    "Warning",
                    $"Cooldown active without stale cache. Steam request and bot fallback skipped to avoid extending Steam rate limits. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; CooldownUntil={cooldownUntil.Value.UtcDateTime:O}",
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

                    ActivateCooldown(cooldownKey, globalCooldownKey, RateLimitCooldown);

                    if (await TryReturnMemoryStaleInventoryAsync(
                            staleCacheKey,
                            requestId,
                            steamId,
                            game.Key,
                            $"Steam returned HTTP 429. CooldownMinutes={(int)RateLimitCooldown.TotalMinutes}",
                            cancellationToken) is { } staleInventory)
                    {
                        return staleInventory;
                    }

                    if (await TryLoadPersistentStaleInventoryAsync(
                            steamId,
                            gameType,
                            game,
                            staleCacheKey,
                            requestId,
                            $"Steam returned HTTP 429. CooldownMinutes={(int)RateLimitCooldown.TotalMinutes}",
                            cancellationToken) is { } persistentStaleInventory)
                    {
                        return persistentStaleInventory;
                    }

                    var botResult = await TryGetBotFallbackAsync(
                        steamId,
                        gameType,
                        requestId,
                        game.Key,
                        "Steam returned HTTP 429",
                        botCooldownKey,
                        botGlobalCooldownKey,
                        cancellationToken);
                    if (botResult is { IsSuccess: true })
                    {
                        CacheInventory(freshCacheKey, staleCacheKey, botResult);
                        await PersistInventoryCacheAsync(steamId, gameType, game, botResult, requestId, cancellationToken);
                        return botResult;
                    }

                    await WriteInventoryLogAsync(
                        "Warning",
                        $"No inventory fallback available after 429. Returning rate-limited result. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; MemoryStale=Miss; PersistentStale=Miss; BotFallback=FailedOrSkipped",
                        cancellationToken: cancellationToken);
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
                    if (await TryLoadPersistentStaleInventoryAsync(
                            steamId,
                            gameType,
                            game,
                            staleCacheKey,
                            requestId,
                            $"Steam returned HTTP {(int)response.StatusCode}.",
                            cancellationToken) is { } persistentStaleInventory)
                    {
                        return persistentStaleInventory;
                    }

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
                    if (await TryLoadPersistentStaleInventoryAsync(
                            steamId,
                            gameType,
                            game,
                            staleCacheKey,
                            requestId,
                            "Steam inventory payload root was invalid.",
                            cancellationToken) is { } persistentStaleInventory)
                    {
                        return persistentStaleInventory;
                    }

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
                        await PersistInventoryCacheAsync(steamId, gameType, game, emptyResult, requestId, cancellationToken);
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
                        botGlobalCooldownKey,
                        cancellationToken);
                    if (botResult is { IsSuccess: true })
                    {
                        CacheInventory(freshCacheKey, staleCacheKey, botResult);
                        await PersistInventoryCacheAsync(steamId, gameType, game, botResult, requestId, cancellationToken);
                        return botResult;
                    }

                    if (await TryReturnMemoryStaleInventoryAsync(
                            staleCacheKey,
                            requestId,
                            steamId,
                            game.Key,
                            "Steam response omitted assets.",
                            cancellationToken) is { } staleInventory)
                    {
                        return staleInventory;
                    }

                    if (await TryLoadPersistentStaleInventoryAsync(
                            steamId,
                            gameType,
                            game,
                            staleCacheKey,
                            requestId,
                            "Steam response omitted assets.",
                            cancellationToken) is { } persistentStaleInventory)
                    {
                        return persistentStaleInventory;
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
                await PersistInventoryCacheAsync(steamId, gameType, game, result, requestId, cancellationToken);
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
                if (await TryLoadPersistentStaleInventoryAsync(
                        steamId,
                        gameType,
                        game,
                        staleCacheKey,
                        requestId,
                        "Steam inventory request timed out.",
                        CancellationToken.None) is { } persistentStaleInventory)
                {
                    return persistentStaleInventory;
                }

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
                if (await TryLoadPersistentStaleInventoryAsync(
                        steamId,
                        gameType,
                        game,
                        staleCacheKey,
                        requestId,
                        "Steam inventory HTTP request failed.",
                        CancellationToken.None) is { } persistentStaleInventory)
                {
                    return persistentStaleInventory;
                }

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
                if (await TryLoadPersistentStaleInventoryAsync(
                        steamId,
                        gameType,
                        game,
                        staleCacheKey,
                        requestId,
                        "Steam inventory JSON parsing failed.",
                        CancellationToken.None) is { } persistentStaleInventory)
                {
                    return persistentStaleInventory;
                }

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
                if (await TryLoadPersistentStaleInventoryAsync(
                        steamId,
                        gameType,
                        game,
                        staleCacheKey,
                        requestId,
                        "Steam inventory failed unexpectedly.",
                        CancellationToken.None) is { } persistentStaleInventory)
                {
                    return persistentStaleInventory;
                }

                return new SteamInventoryResultDto
                {
                    ErrorMessage = $"Steam inventory failed unexpectedly: {exception.GetType().Name}."
                };
            }
        }
        finally
        {
            requestLock.Release();
            await WriteInventoryLogAsync(
                "Info",
                $"Inventory request lock released. RequestId={requestId}; SteamId={steamId}; Game={game.Key}",
                cancellationToken: CancellationToken.None);
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

    private async Task LogMemoryCacheDiagnosticsAsync(
        string freshCacheKey,
        string staleCacheKey,
        string cooldownKey,
        string globalCooldownKey,
        string botCooldownKey,
        string botGlobalCooldownKey,
        string requestId,
        string steamId,
        string gameKey,
        CancellationToken cancellationToken)
    {
        var freshHit = _memoryCache.TryGetValue<SteamInventoryResultDto>(freshCacheKey, out var freshInventory) &&
                       freshInventory is not null;
        var staleHit = _memoryCache.TryGetValue<SteamInventoryResultDto>(staleCacheKey, out var staleInventory) &&
                       staleInventory is not null;
        var scopedCooldown = GetCooldownUntil(cooldownKey);
        var globalCooldown = GetCooldownUntil(globalCooldownKey);
        var botScopedCooldown = GetCooldownUntil(botCooldownKey);
        var botGlobalCooldown = GetCooldownUntil(botGlobalCooldownKey);

        await WriteInventoryLogAsync(
            "Info",
            $"Memory cache diagnostics. RequestId={requestId}; SteamId={steamId}; Game={gameKey}; FreshHit={freshHit}; FreshItems={FormatItemCount(freshInventory)}; FreshCachedAt={FormatCachedAt(freshInventory)}; StaleHit={staleHit}; StaleItems={FormatItemCount(staleInventory)}; StaleCachedAt={FormatCachedAt(staleInventory)}; SteamCooldownScoped={FormatCooldown(scopedCooldown)}; SteamCooldownGlobal={FormatCooldown(globalCooldown)}; BotCooldownScoped={FormatCooldown(botScopedCooldown)}; BotCooldownGlobal={FormatCooldown(botGlobalCooldown)}",
            cancellationToken: cancellationToken);
    }

    private async Task LogPersistentCacheSnapshotAsync(
        string steamId,
        GameDefinition game,
        string requestId,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var contextId = game.SteamContextId.ToString();
            var now = DateTime.UtcNow;
            var entry = await dbContext.SteamInventoryCacheEntries
                .AsNoTracking()
                .Where(item => item.SteamId == steamId &&
                               item.AppId == game.SteamAppId &&
                               item.ContextId == contextId)
                .OrderByDescending(item => item.FetchedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (entry is null)
            {
                await WriteInventoryLogAsync(
                    "Info",
                    $"Persistent cache snapshot. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Exists=False; Reason={reason}",
                    cancellationToken: cancellationToken);
                return;
            }

            await WriteInventoryLogAsync(
                entry.ExpiresAtUtc > now ? "Info" : "Warning",
                $"Persistent cache snapshot. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Exists=True; Usable={entry.ExpiresAtUtc > now}; ItemCount={entry.ItemCount}; JsonLength={entry.ItemsJson.Length}; FetchedAt={entry.FetchedAtUtc:O}; ExpiresAt={entry.ExpiresAtUtc:O}; AgeMinutes={(int)(now - entry.FetchedAtUtc).TotalMinutes}; ExpiresInMinutes={(int)(entry.ExpiresAtUtc - now).TotalMinutes}; Reason={reason}",
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            await WriteInventoryLogAsync(
                "Error",
                $"Persistent cache snapshot failed. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Reason={reason}; ExceptionType={exception.GetType().Name}; Message={exception.Message}",
                exception,
                CancellationToken.None);
        }
    }

    private void CacheInventory(string freshCacheKey, string staleCacheKey, SteamInventoryResultDto result)
    {
        result.IsStale = false;
        result.CachedAtUtc ??= DateTime.UtcNow;
        _memoryCache.Set(freshCacheKey, CloneInventoryResult(result, isStale: false), FreshCacheDuration);
        _memoryCache.Set(staleCacheKey, CloneInventoryResult(result, isStale: false), StaleCacheDuration);
    }

    private async Task<SteamInventoryResultDto?> TryReturnMemoryStaleInventoryAsync(
        string staleCacheKey,
        string requestId,
        string steamId,
        string gameKey,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!_memoryCache.TryGetValue<SteamInventoryResultDto>(staleCacheKey, out var staleInventory) ||
            staleInventory is null)
        {
            await WriteInventoryLogAsync(
                "Info",
                $"Stale memory cache miss. RequestId={requestId}; SteamId={steamId}; Game={gameKey}; Reason={reason}",
                cancellationToken: cancellationToken);
            return null;
        }

        var result = CloneInventoryResult(staleInventory, isStale: true);
        await WriteInventoryLogAsync(
            "Warning",
            $"Stale memory cache returned. RequestId={requestId}; SteamId={steamId}; Game={gameKey}; ItemCount={result.Items.Count}; CachedAt={result.CachedAtUtc?.ToString("O") ?? "<null>"}; Reason={reason}",
            cancellationToken: cancellationToken);
        return result;
    }

    private async Task<SteamInventoryResultDto?> TryLoadPersistentStaleInventoryAsync(
        string steamId,
        GameType gameType,
        GameDefinition game,
        string staleCacheKey,
        string requestId,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteInventoryLogAsync(
                "Info",
                $"Persistent stale lookup started. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Reason={reason}",
                cancellationToken: cancellationToken);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var entry = await dbContext.SteamInventoryCacheEntries
                .AsNoTracking()
                .Where(item => item.SteamId == steamId &&
                               item.AppId == game.SteamAppId &&
                               item.ContextId == game.SteamContextId.ToString())
                .OrderByDescending(item => item.FetchedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (entry is null)
            {
                await WriteInventoryLogAsync(
                    "Warning",
                    $"Persistent stale lookup miss. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Exists=False; Reason={reason}",
                    cancellationToken: cancellationToken);
                return null;
            }

            if (entry.ExpiresAtUtc <= now)
            {
                await WriteInventoryLogAsync(
                    "Warning",
                    $"Persistent stale lookup miss. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Exists=True; Usable=False; ItemCount={entry.ItemCount}; JsonLength={entry.ItemsJson.Length}; FetchedAt={entry.FetchedAtUtc:O}; ExpiresAt={entry.ExpiresAtUtc:O}; ExpiredMinutes={(int)(now - entry.ExpiresAtUtc).TotalMinutes}; Reason={reason}",
                    cancellationToken: cancellationToken);
                return null;
            }

            List<SteamInventoryItemDto> items;
            try
            {
                items = JsonSerializer.Deserialize<List<SteamInventoryItemDto>>(entry.ItemsJson, SerializerOptions) ?? new List<SteamInventoryItemDto>();
            }
            catch (JsonException exception)
            {
                await WriteInventoryLogAsync(
                    "Error",
                    $"Persistent stale lookup deserialize failed. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ItemCount={entry.ItemCount}; JsonLength={entry.ItemsJson.Length}; FetchedAt={entry.FetchedAtUtc:O}; ExpiresAt={entry.ExpiresAtUtc:O}; Message={exception.Message}; Reason={reason}",
                    exception,
                    CancellationToken.None);
                return null;
            }

            foreach (var item in items)
            {
                item.GameType = gameType;
            }

            var result = new SteamInventoryResultDto
            {
                IsSuccess = true,
                IsStale = true,
                CachedAtUtc = entry.FetchedAtUtc,
                Items = items
            };

            _memoryCache.Set(staleCacheKey, CloneInventoryResult(result, isStale: false), StaleCacheDuration);
            await WriteInventoryLogAsync(
                "Warning",
                $"Persistent stale inventory returned. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ItemCount={result.Items.Count}; CachedAt={entry.FetchedAtUtc:O}; ExpiresAt={entry.ExpiresAtUtc:O}; Reason={reason}",
                cancellationToken: cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            await WriteInventoryLogAsync(
                "Error",
                $"Persistent inventory cache read failed. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Reason={reason}; Message={exception.Message}",
                exception,
                CancellationToken.None);
            return null;
        }
    }

    private async Task PersistInventoryCacheAsync(
        string steamId,
        GameType gameType,
        GameDefinition game,
        SteamInventoryResultDto result,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess)
        {
            return;
        }

        try
        {
            await WriteInventoryLogAsync(
                "Info",
                $"Persistent inventory cache save started. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ItemCount={result.Items.Count}; IsStale={result.IsStale}; CachedAt={result.CachedAtUtc?.ToString("O") ?? "<null>"}",
                cancellationToken: cancellationToken);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var contextId = game.SteamContextId.ToString();
            var entry = await dbContext.SteamInventoryCacheEntries
                .SingleOrDefaultAsync(
                    item => item.SteamId == steamId &&
                            item.AppId == game.SteamAppId &&
                            item.ContextId == contextId,
                    cancellationToken);

            var saveMode = "Update";
            if (entry is null)
            {
                saveMode = "Create";
                entry = new SteamInventoryCacheEntry
                {
                    Id = Guid.NewGuid(),
                    SteamId = steamId,
                    AppId = game.SteamAppId,
                    ContextId = contextId
                };
                dbContext.SteamInventoryCacheEntries.Add(entry);
            }

            entry.GameType = gameType;
            entry.ItemsJson = JsonSerializer.Serialize(result.Items, SerializerOptions);
            entry.ItemCount = result.Items.Count;
            entry.FetchedAtUtc = now;
            entry.ExpiresAtUtc = now.Add(PersistentStaleCacheDuration);
            await dbContext.SaveChangesAsync(cancellationToken);

            await WriteInventoryLogAsync(
                "Info",
                $"Persistent inventory cache saved. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; Mode={saveMode}; ItemCount={result.Items.Count}; JsonLength={entry.ItemsJson.Length}; FetchedAt={entry.FetchedAtUtc:O}; ExpiresAt={entry.ExpiresAtUtc:O}",
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            await WriteInventoryLogAsync(
                "Error",
                $"Persistent inventory cache write failed. RequestId={requestId}; SteamId={steamId}; Game={game.Key}; ItemCount={result.Items.Count}; Message={exception.Message}",
                exception,
                CancellationToken.None);
        }
    }

    private DateTimeOffset? GetActiveCooldownUntil(params string[] keys)
    {
        DateTimeOffset? latest = null;
        var now = DateTimeOffset.UtcNow;
        foreach (var key in keys)
        {
            var cooldownUntil = GetCooldownUntil(key);
            if (cooldownUntil is null || cooldownUntil <= now)
            {
                continue;
            }

            if (latest is null || cooldownUntil.Value > latest.Value)
            {
                latest = cooldownUntil.Value;
            }
        }

        return latest;
    }

    private DateTimeOffset? GetCooldownUntil(string key)
    {
        return _memoryCache.TryGetValue<DateTimeOffset>(key, out var cooldownUntil)
            ? cooldownUntil
            : null;
    }

    private DateTimeOffset ActivateCooldown(string scopedCooldownKey, string globalCooldownKey, TimeSpan duration)
    {
        var cooldownUntil = DateTimeOffset.UtcNow.Add(duration);
        _memoryCache.Set(scopedCooldownKey, cooldownUntil, duration);
        _memoryCache.Set(globalCooldownKey, cooldownUntil, duration);
        return cooldownUntil;
    }

    private static SteamInventoryResultDto CloneInventoryResult(SteamInventoryResultDto source, bool isStale)
    {
        return new SteamInventoryResultDto
        {
            IsSuccess = source.IsSuccess,
            ErrorMessage = source.ErrorMessage,
            IsStale = isStale,
            CachedAtUtc = source.CachedAtUtc,
            Items = source.Items.Select(CloneInventoryItem).ToList()
        };
    }

    private static SteamInventoryItemDto CloneInventoryItem(SteamInventoryItemDto item)
    {
        return new SteamInventoryItemDto
        {
            GameType = item.GameType,
            AssetId = item.AssetId,
            ClassId = item.ClassId,
            InstanceId = item.InstanceId,
            Name = item.Name,
            MarketHashName = item.MarketHashName,
            MarketName = item.MarketName,
            IconUrl = item.IconUrl,
            Tradable = item.Tradable,
            Marketable = item.Marketable
        };
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
        string botGlobalCooldownKey,
        CancellationToken cancellationToken)
    {
        var botCooldownUntil = GetActiveCooldownUntil(botCooldownKey, botGlobalCooldownKey);
        if (botCooldownUntil is not null)
        {
            await WriteInventoryLogAsync(
                "Warning",
                $"Bot inventory fallback skipped because bot cooldown is active. RequestId={requestId}; SteamId={steamId}; Game={gameKey}; CooldownUntil={botCooldownUntil.Value.UtcDateTime:O}; Reason={reason}",
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
            var cooldownUntil = ActivateCooldown(botCooldownKey, botGlobalCooldownKey, BotRateLimitCooldown);
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

    private static string FormatItemCount(SteamInventoryResultDto? result)
    {
        return result?.Items.Count.ToString() ?? "<null>";
    }

    private static string FormatCachedAt(SteamInventoryResultDto? result)
    {
        return result?.CachedAtUtc?.ToString("O") ?? "<null>";
    }

    private static string FormatCooldown(DateTimeOffset? cooldownUntil)
    {
        if (cooldownUntil is null)
        {
            return "<none>";
        }

        var remainingSeconds = (int)Math.Ceiling((cooldownUntil.Value - DateTimeOffset.UtcNow).TotalSeconds);
        return remainingSeconds > 0
            ? $"{cooldownUntil.Value.UtcDateTime:O} ({remainingSeconds}s remaining)"
            : $"{cooldownUntil.Value.UtcDateTime:O} (expired)";
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
