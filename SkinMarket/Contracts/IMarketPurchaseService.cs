using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IMarketPurchaseService
{
    Task<MarketPurchaseResult> PurchaseAsync(MarketPurchaseRequest request, Guid buyerAppUserId, CancellationToken cancellationToken = default);
    Task<List<MarketPurchaseRecord>> GetRecentPurchasesAsync(Guid buyerAppUserId, GameType gameType, int count, CancellationToken cancellationToken = default);
}
