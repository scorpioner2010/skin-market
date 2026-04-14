using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MarketService : IMarketService
{
    private readonly AppDbContext _dbContext;
    private readonly ISteamInventoryService _steamInventoryService;
    private readonly IMarketPricingService _marketPricingService;
    private readonly IInventoryPriceRefreshService _inventoryPriceRefreshService;
    private readonly IGameCatalog _gameCatalog;
    private readonly SteamBotOptions _steamBotOptions;
    private readonly ILogger<MarketService> _logger;

    public MarketService(
        AppDbContext dbContext,
        ISteamInventoryService steamInventoryService,
        IMarketPricingService marketPricingService,
        IInventoryPriceRefreshService inventoryPriceRefreshService,
        IGameCatalog gameCatalog,
        IOptions<SteamBotOptions> steamBotOptions,
        ILogger<MarketService> logger)
    {
        _dbContext = dbContext;
        _steamInventoryService = steamInventoryService;
        _marketPricingService = marketPricingService;
        _inventoryPriceRefreshService = inventoryPriceRefreshService;
        _gameCatalog = gameCatalog;
        _steamBotOptions = steamBotOptions.Value;
        _logger = logger;
    }

    public async Task<List<MarketListingItem>> GetAvailableItemsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_steamBotOptions.BotSteamId))
        {
            _logger.LogWarning("Market listings were skipped because SteamBot:BotSteamId is not configured.");
            return [];
        }

        var game = _gameCatalog.Get(_gameCatalog.DefaultGameType);
        var inventory = await _steamInventoryService.GetInventoryAsync(_steamBotOptions.BotSteamId, game.Type, cancellationToken);
        if (!inventory.IsSuccess)
        {
            _logger.LogWarning(
                "Live market inventory loading failed for bot SteamId {SteamId}. Error: {Error}",
                _steamBotOptions.BotSteamId,
                inventory.ErrorMessage);
            return [];
        }

        var reservedAssetKeys = await _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .Where(item => item.AppId == game.SteamAppId && item.ContextId == game.SteamContextId.ToString())
            .Select(item => item.AssetId)
            .ToListAsync(cancellationToken);
        var reservedAssetSet = new HashSet<string>(
            reservedAssetKeys.Select(assetId => BuildAssetKey(game.SteamAppId, game.SteamContextId.ToString(), assetId)),
            StringComparer.Ordinal);

        var liveItems = inventory.Items
            .Where(item => !reservedAssetSet.Contains(BuildAssetKey(game.SteamAppId, game.SteamContextId.ToString(), item.AssetId)))
            .ToList();

        var sourceOperationsByAssetId = await LoadSourceOperationsByAssetIdAsync(game, liveItems, cancellationToken);
        var marketHashNames = liveItems
            .Select(MarketHashNameUtility.ResolvePrimary)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var cachedPrices = marketHashNames.Count == 0
            ? new Dictionary<string, ItemPriceResolutionResult>(StringComparer.Ordinal)
            : await _inventoryPriceRefreshService.GetCurrentPricesAsync(marketHashNames, game.Type, cancellationToken);

        var refreshTargets = new HashSet<string>(StringComparer.Ordinal);
        var listings = new List<MarketListingItem>(liveItems.Count);
        foreach (var item in liveItems)
        {
            var marketHashName = MarketHashNameUtility.ResolvePrimary(item);
            cachedPrices.TryGetValue(marketHashName ?? string.Empty, out var resolvedPrice);

            if (!string.IsNullOrWhiteSpace(marketHashName) &&
                resolvedPrice is not null &&
                (resolvedPrice.NeedsRefresh || string.Equals(resolvedPrice.Status, "Refreshing", StringComparison.Ordinal)))
            {
                refreshTargets.Add(marketHashName);
            }

            sourceOperationsByAssetId.TryGetValue(item.AssetId, out var sourceOperation);
            listings.Add(new MarketListingItem
            {
                GameType = game.Type,
                SourceTradeOperationId = sourceOperation?.Id,
                SellerAppUserId = sourceOperation?.AppUserId,
                AppId = game.SteamAppId,
                ContextId = game.SteamContextId.ToString(),
                AssetId = item.AssetId,
                ClassId = item.ClassId,
                InstanceId = item.InstanceId,
                ItemName = string.IsNullOrWhiteSpace(item.Name) ? "Unknown Item" : item.Name,
                MarketHashName = marketHashName,
                IconUrl = item.IconUrl,
                Price = _marketPricingService.CalculatePrice(item, resolvedPrice),
                Tradable = item.Tradable,
                Marketable = item.Marketable
            });
        }

        if (refreshTargets.Count > 0)
        {
            await _inventoryPriceRefreshService.QueueRefreshAsync(refreshTargets.ToList(), game.Type, cancellationToken);
        }

        return listings
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.AssetId, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<Dictionary<string, TradeOperation>> LoadSourceOperationsByAssetIdAsync(
        GameDefinition game,
        IReadOnlyCollection<SteamInventoryItemDto> items,
        CancellationToken cancellationToken)
    {
        var assetIds = items
            .Select(item => item.AssetId)
            .Where(assetId => !string.IsNullOrWhiteSpace(assetId))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (assetIds.Count == 0)
        {
            return new Dictionary<string, TradeOperation>(StringComparer.Ordinal);
        }

        var operations = await _dbContext.TradeOperations
            .AsNoTracking()
            .Where(operation =>
                operation.AppId == game.SteamAppId &&
                operation.ContextId == game.SteamContextId.ToString() &&
                operation.BotAssetId != null &&
                assetIds.Contains(operation.BotAssetId))
            .OrderByDescending(operation => operation.CreditedAtUtc ?? operation.ReceivedByBotAtUtc ?? operation.UpdatedAtUtc)
            .ToListAsync(cancellationToken);

        return operations
            .GroupBy(operation => operation.BotAssetId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static string BuildAssetKey(int appId, string contextId, string assetId)
    {
        return $"{appId}:{contextId}:{assetId}";
    }
}
