using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ISteamTradeClient
{
    Task<BotIntakeResult> CreateIntakeTradeAsync(TradeOperation operation, AppUser seller, CancellationToken cancellationToken = default);
    Task<MarketDeliveryResult> CreateDeliveryTradeAsync(MarketItem marketItem, TradeOperation sourceOperation, AppUser buyer, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SteamTradeOfferStatusResult>> GetOfferStatusesAsync(
        IReadOnlyCollection<SteamTradeOfferStatusRequest> requests,
        CancellationToken cancellationToken = default);
}
