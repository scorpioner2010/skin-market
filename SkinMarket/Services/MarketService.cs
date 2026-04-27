using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MarketService : IMarketService
{
    private static readonly TimeSpan ProtectedInventoryFallbackWindow = TimeSpan.FromDays(14);
    private readonly AppDbContext _dbContext;
    private readonly ISteamBotInventoryClient _steamBotInventoryClient;
    private readonly ISteamInventoryService _steamInventoryService;
    private readonly IMarketPricingService _marketPricingService;
    private readonly IInventoryPriceRefreshService _inventoryPriceRefreshService;
    private readonly IGameCatalog _gameCatalog;
    private readonly SteamBotOptions _steamBotOptions;
    private readonly ILogger<MarketService> _logger;

    public MarketService(
        AppDbContext dbContext,
        ISteamBotInventoryClient steamBotInventoryClient,
        ISteamInventoryService steamInventoryService,
        IMarketPricingService marketPricingService,
        IInventoryPriceRefreshService inventoryPriceRefreshService,
        IGameCatalog gameCatalog,
        IOptions<SteamBotOptions> steamBotOptions,
        ILogger<MarketService> logger)
    {
        _dbContext = dbContext;
        _steamBotInventoryClient = steamBotInventoryClient;
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
        var inventory = await LoadBotInventoryAsync(game, cancellationToken);
        if (!inventory.IsSuccess)
        {
            _logger.LogWarning(
                "Live market inventory loading failed for bot SteamId {SteamId}. Error: {Error}",
                _steamBotOptions.BotSteamId,
                inventory.ErrorMessage);
            return [];
        }

        var reservedAssets = await _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .Where(item => item.AppId == game.SteamAppId)
            .Select(item => new { item.AppId, item.ContextId, item.AssetId })
            .ToListAsync(cancellationToken);
        var reservedAssetSet = new HashSet<string>(
            reservedAssets.Select(item => BuildAssetKey(item.AppId, item.ContextId, item.AssetId)),
            StringComparer.Ordinal);

        var liveItems = inventory.Items
            .Where(item => !reservedAssetSet.Contains(BuildAssetKey(game.SteamAppId, game.SteamContextId.ToString(), item.AssetId)))
            .ToList();
        var liveAssetIds = new HashSet<string>(
            liveItems.Select(item => item.AssetId),
            StringComparer.Ordinal);
        var protectedFallbackOperations = await LoadProtectedFallbackOperationsAsync(
            game,
            liveAssetIds,
            reservedAssetSet,
            cancellationToken);

        var sourceOperationsByAssetId = await LoadSourceOperationsByAssetIdAsync(
            game,
            liveItems,
            protectedFallbackOperations,
            cancellationToken);
        var marketHashNames = liveItems
            .Select(MarketHashNameUtility.ResolvePrimary)
            .Concat(protectedFallbackOperations.Select(MarketHashNameUtility.ResolvePrimary))
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

        foreach (var operation in protectedFallbackOperations)
        {
            var marketHashName = MarketHashNameUtility.ResolvePrimary(operation);
            cachedPrices.TryGetValue(marketHashName ?? string.Empty, out var resolvedPrice);

            if (!string.IsNullOrWhiteSpace(marketHashName) &&
                resolvedPrice is not null &&
                (resolvedPrice.NeedsRefresh || string.Equals(resolvedPrice.Status, "Refreshing", StringComparison.Ordinal)))
            {
                refreshTargets.Add(marketHashName);
            }

            listings.Add(new MarketListingItem
            {
                GameType = game.Type,
                SourceTradeOperationId = operation.Id,
                SellerAppUserId = operation.AppUserId,
                AppId = operation.AppId,
                ContextId = operation.ContextId,
                AssetId = operation.BotAssetId!,
                ClassId = operation.BotClassId ?? operation.ClassId,
                InstanceId = operation.BotInstanceId ?? operation.InstanceId,
                ItemName = string.IsNullOrWhiteSpace(operation.ItemName) ? "Unknown Item" : operation.ItemName,
                MarketHashName = marketHashName,
                IconUrl = operation.IconUrl,
                Price = CalculateProtectedFallbackPrice(operation, resolvedPrice),
                Tradable = false,
                Marketable = null
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

    private async Task<SteamInventoryResultDto> LoadBotInventoryAsync(
        GameDefinition game,
        CancellationToken cancellationToken)
    {
        var liveInventory = await _steamBotInventoryClient.GetInventoryAsync(_steamBotOptions.BotSteamId, game.Type, cancellationToken);
        if (liveInventory.IsSuccess)
        {
            return liveInventory;
        }

        _logger.LogWarning(
            "Bot inventory service failed for market listings for SteamId {SteamId}. Falling back to Steam community inventory. Error: {Error}",
            _steamBotOptions.BotSteamId,
            liveInventory.ErrorMessage);

        return await _steamInventoryService.GetInventoryAsync(_steamBotOptions.BotSteamId, game.Type, cancellationToken);
    }

    private async Task<Dictionary<string, TradeOperation>> LoadSourceOperationsByAssetIdAsync(
        GameDefinition game,
        IReadOnlyCollection<SteamInventoryItemDto> items,
        IReadOnlyCollection<TradeOperation> protectedFallbackOperations,
        CancellationToken cancellationToken)
    {
        var assetIds = items
            .Select(item => item.AssetId)
            .Concat(protectedFallbackOperations
                .Select(operation => operation.BotAssetId)
                .Where(assetId => !string.IsNullOrWhiteSpace(assetId))
                .Cast<string>())
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

    private async Task<List<TradeOperation>> LoadProtectedFallbackOperationsAsync(
        GameDefinition game,
        ISet<string> liveAssetIds,
        ISet<string> reservedAssetSet,
        CancellationToken cancellationToken)
    {
        var thresholdUtc = DateTime.UtcNow - ProtectedInventoryFallbackWindow;
        var operations = await _dbContext.TradeOperations
            .AsNoTracking()
            .Where(operation =>
                operation.AppId == game.SteamAppId &&
                operation.BotAssetId != null &&
                (operation.Status == "ReceivedByBot" || operation.Status == "Credited") &&
                (operation.CreditedAtUtc ?? operation.ReceivedByBotAtUtc ?? operation.UpdatedAtUtc) >= thresholdUtc)
            .OrderByDescending(operation => operation.CreditedAtUtc ?? operation.ReceivedByBotAtUtc ?? operation.UpdatedAtUtc)
            .ToListAsync(cancellationToken);

        return operations
            .GroupBy(operation => operation.BotAssetId!, StringComparer.Ordinal)
            .Select(group => group.First())
            .Where(operation =>
                !liveAssetIds.Contains(operation.BotAssetId!) &&
                !reservedAssetSet.Contains(BuildAssetKey(operation.AppId, operation.ContextId, operation.BotAssetId!)))
            .ToList();
    }

    private static decimal CalculateProtectedFallbackPrice(
        TradeOperation operation,
        ItemPriceResolutionResult? resolvedPrice)
    {
        if (resolvedPrice?.HasPrice == true && resolvedPrice.Price.HasValue)
        {
            return Math.Round(resolvedPrice.Price.Value * 0.92m, 2, MidpointRounding.AwayFromZero);
        }

        var basePrice = operation.CreditAmount > 0 ? operation.CreditAmount : 10m;
        return Math.Round(basePrice * 1.15m, 2, MidpointRounding.AwayFromZero);
    }

    private static string BuildAssetKey(int appId, string contextId, string assetId)
    {
        return $"{appId}:{contextId}:{assetId}";
    }
}
