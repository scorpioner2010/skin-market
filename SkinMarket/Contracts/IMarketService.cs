using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IMarketService
{
    Task<List<MarketListingItem>> GetAvailableItemsAsync(CancellationToken cancellationToken = default);
}
