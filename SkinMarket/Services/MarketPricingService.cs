using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MarketPricingService : IMarketPricingService
{
    private readonly IItemPriceResolver _itemPriceResolver;
    private readonly ILogger<MarketPricingService> _logger;

    public MarketPricingService(IItemPriceResolver itemPriceResolver, ILogger<MarketPricingService> logger)
    {
        _itemPriceResolver = itemPriceResolver;
        _logger = logger;
    }

    public decimal CalculatePrice(SteamInventoryItemDto item, ItemPriceResolutionResult? resolvedPrice = null)
    {
        if (resolvedPrice?.HasPrice == true && resolvedPrice.Price.HasValue)
        {
            return Math.Round(resolvedPrice.Price.Value * 0.92m, 2, MidpointRounding.AwayFromZero);
        }

        _logger.LogInformation(
            "Using fallback market pricing for inventory item {ItemName}. Failure: {FailureReason}",
            item.Name,
            resolvedPrice?.FailureReason);
        return CalculateFallbackMarketPrice(item);
    }

    public async Task<decimal> CalculatePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        var resolvedPrice = await _itemPriceResolver.ResolveAsync(item, cancellationToken);
        return CalculatePrice(item, resolvedPrice);
    }

    public async Task<decimal> CalculatePriceAsync(TradeOperation operation, CancellationToken cancellationToken = default)
    {
        var resolvedPrice = await _itemPriceResolver.ResolveAsync(operation, cancellationToken);
        if (resolvedPrice.HasPrice && resolvedPrice.Price.HasValue)
        {
            return Math.Round(resolvedPrice.Price.Value * 0.92m, 2, MidpointRounding.AwayFromZero);
        }

        _logger.LogInformation("Using fallback market pricing for trade operation {TradeOperationId}. Failure: {FailureReason}", operation.Id, resolvedPrice.FailureReason);
        var basePrice = operation.CreditAmount > 0 ? operation.CreditAmount : 10m;
        return Math.Round(basePrice * 1.15m, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal CalculateFallbackMarketPrice(SteamInventoryItemDto item)
    {
        var basePrice = 10m;

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            basePrice += 2m;
        }

        if (item.Marketable == true)
        {
            basePrice += 5m;
        }

        if (item.Tradable == true)
        {
            basePrice += 3m;
        }

        return Math.Round(basePrice * 1.15m, 2, MidpointRounding.AwayFromZero);
    }
}
