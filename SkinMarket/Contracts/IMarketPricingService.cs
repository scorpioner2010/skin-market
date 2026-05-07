using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IMarketPricingService
{
    decimal? CalculatePrice(SteamInventoryItemDto item, ItemPriceResolutionResult? resolvedPrice = null);
    Task<decimal?> CalculatePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default);
    Task<decimal?> CalculatePriceAsync(TradeOperation operation, CancellationToken cancellationToken = default);
}
