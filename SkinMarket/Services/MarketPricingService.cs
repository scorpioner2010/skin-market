using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MarketPricingService : IMarketPricingService
{
    private readonly ISkinportPricingService _skinportPricingService;
    private readonly ILogger<MarketPricingService> _logger;
    private readonly IGameCatalog _gameCatalog;

    public MarketPricingService(ISkinportPricingService skinportPricingService, ILogger<MarketPricingService> logger, IGameCatalog gameCatalog)
    {
        _skinportPricingService = skinportPricingService;
        _logger = logger;
        _gameCatalog = gameCatalog;
    }

    public async Task<decimal> CalculatePriceAsync(TradeOperation operation, CancellationToken cancellationToken = default)
    {
        var resolvedPrice = await _skinportPricingService.ResolvePriceAsync(operation.ItemName, _gameCatalog.DefaultGameType, cancellationToken);
        if (resolvedPrice.RealPriceUsd.HasValue)
        {
            return Math.Round(resolvedPrice.RealPriceUsd.Value * 0.92m, 2, MidpointRounding.AwayFromZero);
        }

        _logger.LogInformation("Using fallback market pricing for trade operation {TradeOperationId}. Source: {Source}", operation.Id, resolvedPrice.Source);
        var basePrice = operation.CreditAmount > 0 ? operation.CreditAmount : 10m;
        return Math.Round(basePrice * 1.15m, 2, MidpointRounding.AwayFromZero);
    }
}
