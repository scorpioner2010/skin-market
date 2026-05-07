using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MarketPricingService : IMarketPricingService
{
    private readonly IItemPriceResolver _itemPriceResolver;
    private readonly IGameCatalog _gameCatalog;
    private readonly ILogger<MarketPricingService> _logger;

    public MarketPricingService(
        IItemPriceResolver itemPriceResolver,
        IGameCatalog gameCatalog,
        ILogger<MarketPricingService> logger)
    {
        _itemPriceResolver = itemPriceResolver;
        _gameCatalog = gameCatalog;
        _logger = logger;
    }

    public decimal? CalculatePrice(SteamInventoryItemDto item, ItemPriceResolutionResult? resolvedPrice = null)
    {
        if (resolvedPrice?.HasPrice == true && resolvedPrice.Price.HasValue)
        {
            return Math.Round(resolvedPrice.Price.Value, 2, MidpointRounding.AwayFromZero);
        }

        _logger.LogDebug(
            "Blocked fallback market pricing for inventory item {ItemName}. Failure: {FailureReason}",
            item.Name,
            resolvedPrice?.FailureReason);
        return null;
    }

    public async Task<decimal?> CalculatePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        var marketHashName = MarketHashNameUtility.ResolvePrimary(item);
        if (string.IsNullOrWhiteSpace(marketHashName))
        {
            return null;
        }

        var resolvedPrice = await _itemPriceResolver.GetCachedAsync(marketHashName, item.GameType, cancellationToken);
        return CalculatePrice(item, resolvedPrice);
    }

    public async Task<decimal?> CalculatePriceAsync(TradeOperation operation, CancellationToken cancellationToken = default)
    {
        var marketHashName = MarketHashNameUtility.ResolvePrimary(operation);
        if (string.IsNullOrWhiteSpace(marketHashName))
        {
            return null;
        }

        var gameType = _gameCatalog.SupportedGames
            .FirstOrDefault(game =>
                game.SteamAppId == operation.AppId &&
                game.SteamContextId.ToString() == operation.ContextId)
            ?.Type ?? _gameCatalog.DefaultGameType;
        var resolvedPrice = await _itemPriceResolver.GetCachedAsync(marketHashName, gameType, cancellationToken);
        if (resolvedPrice.HasPrice && resolvedPrice.Price.HasValue)
        {
            return Math.Round(resolvedPrice.Price.Value, 2, MidpointRounding.AwayFromZero);
        }

        _logger.LogDebug(
            "Blocked fallback market pricing for trade operation {TradeOperationId}. Failure: {FailureReason}",
            operation.Id,
            resolvedPrice.FailureReason);
        return null;
    }
}
