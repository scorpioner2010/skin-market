using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IMarketPricingService
{
    Task<decimal> CalculatePriceAsync(TradeOperation operation, CancellationToken cancellationToken = default);
}
