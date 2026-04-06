using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IMarketDeliveryService
{
    Task<MarketDeliveryResult> CreateDeliveryTradeAsync(Guid marketItemId, Guid buyerAppUserId, CancellationToken cancellationToken = default);
    Task<MarketDeliveryResult> ConfirmDeliveredAsync(Guid marketItemId, Guid buyerAppUserId, CancellationToken cancellationToken = default);
}
