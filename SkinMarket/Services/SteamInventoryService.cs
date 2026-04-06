using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamInventoryService : ISteamInventoryService
{
    private const string IconBaseUrl = "https://community.akamai.steamstatic.com/economy/image/";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SteamInventoryService> _logger;
    private readonly IGameCatalog _gameCatalog;

    public SteamInventoryService(HttpClient httpClient, IMemoryCache memoryCache, ILogger<SteamInventoryService> logger, IGameCatalog gameCatalog)
    {
        _httpClient = httpClient;
        _memoryCache = memoryCache;
        _logger = logger;
        _gameCatalog = gameCatalog;
    }

    public async Task<SteamInventoryResultDto> GetInventoryAsync(string steamId, GameType gameType, CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(gameType);
        var cacheKey = $"steam-inventory::{game.Key}::{steamId}";
        if (_memoryCache.TryGetValue<SteamInventoryResultDto>(cacheKey, out var cachedInventory) && cachedInventory is not null)
        {
            _logger.LogInformation("Steam inventory cache hit for SteamId {SteamId} and game {GameKey}.", steamId, game.Key);
            return cachedInventory;
        }

        _logger.LogInformation("Steam inventory cache miss for SteamId {SteamId} and game {GameKey}.", steamId, game.Key);
        var requestUrl = $"https://steamcommunity.com/inventory/{steamId}/{game.SteamAppId}/{game.SteamContextId}?l=english&count=2000";

        try
        {
            using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
            if ((int)response.StatusCode == 429)
            {
                _logger.LogWarning("Steam inventory request returned 429 for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
                if (_memoryCache.TryGetValue<SteamInventoryResultDto>(cacheKey, out var staleInventory) && staleInventory is not null)
                {
                    _logger.LogWarning("Falling back to cached Steam inventory after 429 for SteamId {SteamId}.", steamId);
                    return staleInventory;
                }

                return new SteamInventoryResultDto
                {
                    ErrorMessage = "Steam is temporarily rate-limiting inventory requests. Please try again in a few minutes."
                };
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Steam inventory request returned 403 for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
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
                return new SteamInventoryResultDto
                {
                    ErrorMessage = $"Steam inventory request failed. Status code: {(int)response.StatusCode}."
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("Steam inventory response root is not an object for SteamId {SteamId}.", steamId);
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
                return new SteamInventoryResultDto
                {
                    ErrorMessage = "Steam inventory is unavailable or private."
                };
            }

            if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Steam inventory response does not contain a valid assets array for SteamId {SteamId}.", steamId);
                return new SteamInventoryResultDto
                {
                    ErrorMessage = "Steam inventory response is incomplete: assets were not returned."
                };
            }

            var descriptions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("descriptions", out var descriptionsElement) &&
                descriptionsElement.ValueKind == JsonValueKind.Array)
            {
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
            }

            var items = new List<SteamInventoryItemDto>();
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var assetId = GetString(asset, "assetid");
                var classId = GetString(asset, "classid") ?? string.Empty;
                var instanceId = GetString(asset, "instanceid") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(assetId))
                {
                    continue;
                }

                descriptions.TryGetValue($"{classId}_{instanceId}", out var description);

                items.Add(new SteamInventoryItemDto
                {
                    GameType = gameType,
                    AssetId = assetId,
                    ClassId = classId,
                    InstanceId = instanceId,
                    Name = GetString(description, "name") ?? "Unknown Item",
                    MarketHashName = GetString(description, "market_hash_name"),
                    MarketName = GetString(description, "market_name"),
                    IconUrl = BuildIconUrl(GetString(description, "icon_url")),
                    Tradable = GetBooleanFlag(description, "tradable"),
                    Marketable = GetBooleanFlag(description, "marketable")
                });
            }

            var result = new SteamInventoryResultDto
            {
                IsSuccess = true,
                Items = items
            };

            _memoryCache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "Steam inventory HTTP request failed for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
            return new SteamInventoryResultDto
            {
                ErrorMessage = $"Failed to reach Steam inventory endpoint: {exception.Message}"
            };
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Steam inventory JSON parsing failed for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
            return new SteamInventoryResultDto
            {
                ErrorMessage = "Steam inventory returned invalid JSON."
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected Steam inventory error for SteamId {SteamId}. Endpoint: {RequestUrl}", steamId, requestUrl);
            return new SteamInventoryResultDto
            {
                ErrorMessage = $"Steam inventory failed unexpectedly: {exception.GetType().Name}."
            };
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
}
