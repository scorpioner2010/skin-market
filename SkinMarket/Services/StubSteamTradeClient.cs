using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class StubSteamTradeClient : ISteamTradeClient
{
    public Task<BotIntakeResult> CreateIntakeTradeAsync(TradeOperation operation, CancellationToken cancellationToken = default)
    {
        var result = new BotIntakeResult
        {
            Success = true,
            NewStatus = "TradeCreated",
            TradeOfferId = $"stub-{operation.Id:N}",
            Message = "Stub bot trade created. Real Steam bot integration is not connected yet."
        };

        return Task.FromResult(result);
    }

    public Task<MarketDeliveryResult> CreateDeliveryTradeAsync(MarketItem marketItem, AppUser buyer, CancellationToken cancellationToken = default)
    {
        var result = new MarketDeliveryResult
        {
            Success = true,
            NewStatus = "DeliveryTradeCreated",
            DeliveryTradeOfferId = $"delivery-{marketItem.Id:N}",
            Message = "Stub delivery trade created. Real Steam delivery integration is not connected yet."
        };

        return Task.FromResult(result);
    }
}
