using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class ItemPricingService : IItemPricingService
{
    private readonly IItemPriceResolver _itemPriceResolver;
    private readonly ILogger<ItemPricingService> _logger;

    public ItemPricingService(IItemPriceResolver itemPriceResolver, ILogger<ItemPricingService> logger)
    {
        _itemPriceResolver = itemPriceResolver;
        _logger = logger;
    }

    public async Task<decimal> CalculatePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        var resolvedPrice = await _itemPriceResolver.ResolveAsync(item, cancellationToken);
        if (resolvedPrice.HasPrice && resolvedPrice.Price.HasValue)
        {
            return Math.Round(resolvedPrice.Price.Value * 0.80m, 2, MidpointRounding.AwayFromZero);
        }

        _logger.LogInformation("Using fallback credit pricing for inventory item {ItemName}. Failure: {FailureReason}", item.Name, resolvedPrice.FailureReason);
        return CalculateFallbackInventoryPrice(item);
    }

    public async Task<decimal> CalculatePriceAsync(TradeOperation operation, CancellationToken cancellationToken = default)
    {
        var resolvedPrice = await _itemPriceResolver.ResolveAsync(operation, cancellationToken);
        if (resolvedPrice.HasPrice && resolvedPrice.Price.HasValue)
        {
            return Math.Round(resolvedPrice.Price.Value * 0.80m, 2, MidpointRounding.AwayFromZero);
        }

        _logger.LogInformation("Using fallback credit pricing for trade operation {TradeOperationId}. Failure: {FailureReason}", operation.Id, resolvedPrice.FailureReason);
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
