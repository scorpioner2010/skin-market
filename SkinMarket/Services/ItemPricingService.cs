using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class ItemPricingService : IItemPricingService
{
    private readonly ISkinportPricingService _skinportPricingService;
    private readonly ILogger<ItemPricingService> _logger;
    private readonly IGameCatalog _gameCatalog;

    public ItemPricingService(ISkinportPricingService skinportPricingService, ILogger<ItemPricingService> logger, IGameCatalog gameCatalog)
    {
        _skinportPricingService = skinportPricingService;
        _logger = logger;
        _gameCatalog = gameCatalog;
    }

    public async Task<decimal> CalculatePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        var resolvedPrice = await _skinportPricingService.ResolvePriceAsync(item.Name, item.GameType, cancellationToken);
        if (resolvedPrice.RealPriceUsd.HasValue)
        {
            return Math.Round(resolvedPrice.RealPriceUsd.Value * 0.80m, 2, MidpointRounding.AwayFromZero);
        }

        _logger.LogInformation("Using fallback credit pricing for inventory item {ItemName}. Source: {Source}", item.Name, resolvedPrice.Source);
        return CalculateFallbackInventoryPrice(item);
    }

    public async Task<decimal> CalculatePriceAsync(TradeOperation operation, CancellationToken cancellationToken = default)
    {
        var resolvedPrice = await _skinportPricingService.ResolvePriceAsync(operation.ItemName, _gameCatalog.DefaultGameType, cancellationToken);
        if (resolvedPrice.RealPriceUsd.HasValue)
        {
            return Math.Round(resolvedPrice.RealPriceUsd.Value * 0.80m, 2, MidpointRounding.AwayFromZero);
        }

        _logger.LogInformation("Using fallback credit pricing for trade operation {TradeOperationId}. Source: {Source}", operation.Id, resolvedPrice.Source);
        return CalculateFallbackTradeOperationPrice(operation);
    }

    private static decimal CalculateFallbackInventoryPrice(SteamInventoryItemDto item)
    {
        var price = 10m;

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            price += 2m;
        }

        if (item.Marketable == true)
        {
            price += 5m;
        }

        if (item.Tradable == true)
        {
            price += 3m;
        }

        return price;
    }

    private static decimal CalculateFallbackTradeOperationPrice(TradeOperation operation)
    {
        var price = 10m;

        if (!string.IsNullOrWhiteSpace(operation.ItemName))
        {
            price += 2m;
        }

        if (!string.IsNullOrWhiteSpace(operation.ClassId))
        {
            price += 3m;
        }

        return price;
    }
}
