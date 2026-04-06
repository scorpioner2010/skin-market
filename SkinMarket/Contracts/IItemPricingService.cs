using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IItemPricingService
{
    Task<decimal> CalculatePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default);
    Task<decimal> CalculatePriceAsync(TradeOperation operation, CancellationToken cancellationToken = default);
}
