using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IMarketService
{
    Task<List<MarketListingItem>> GetAvailableItemsAsync(GameType gameType, CancellationToken cancellationToken = default);
}
