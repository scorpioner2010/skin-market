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
}
