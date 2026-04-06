using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ISteamTradeClient
{
    Task<BotIntakeResult> CreateIntakeTradeAsync(TradeOperation operation, CancellationToken cancellationToken = default);
    Task<MarketDeliveryResult> CreateDeliveryTradeAsync(MarketItem marketItem, AppUser buyer, CancellationToken cancellationToken = default);
}
