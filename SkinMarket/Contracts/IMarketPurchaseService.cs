using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IMarketPurchaseService
{
    Task<MarketPurchaseResult> PurchaseAsync(MarketPurchaseRequest request, Guid buyerAppUserId, CancellationToken cancellationToken = default);
    Task<List<MarketPurchaseRecord>> GetRecentPurchasesAsync(Guid buyerAppUserId, int count, CancellationToken cancellationToken = default);
}
