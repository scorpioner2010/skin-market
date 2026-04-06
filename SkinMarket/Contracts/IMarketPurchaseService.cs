using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IMarketPurchaseService
{
    Task<MarketPurchaseResult> PurchaseAsync(Guid marketItemId, Guid buyerAppUserId, CancellationToken cancellationToken = default);
    Task<List<MarketItem>> GetRecentPurchasesAsync(Guid buyerAppUserId, int count, CancellationToken cancellationToken = default);
}
