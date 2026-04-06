using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IMarketService
{
    Task<MarketItem?> CreateFromTradeOperationAsync(Guid tradeOperationId, CancellationToken cancellationToken = default);
    Task<List<MarketItem>> GetAvailableItemsAsync(CancellationToken cancellationToken = default);
}
