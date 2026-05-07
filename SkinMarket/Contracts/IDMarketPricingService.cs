using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IDMarketPricingService
{
    Task<PriceSourceResult> ProbePriceAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default);
}
