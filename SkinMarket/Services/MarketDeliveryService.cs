using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MarketDeliveryService : IMarketDeliveryService
{
    private readonly AppDbContext _dbContext;
    private readonly ISteamTradeClient _steamTradeClient;
    private readonly SteamBotOptions _options;

    public MarketDeliveryService(
        AppDbContext dbContext,
        ISteamTradeClient steamTradeClient,
        IOptions<SteamBotOptions> options)
    {
        _dbContext = dbContext;
        _steamTradeClient = steamTradeClient;
        _options = options.Value;
    }

    public async Task<MarketDeliveryResult> CreateDeliveryTradeAsync(Guid marketItemId, Guid buyerAppUserId, CancellationToken cancellationToken = default)
    {
        var marketItem = await _dbContext.MarketItems
            .SingleOrDefaultAsync(item => item.Id == marketItemId && item.BuyerAppUserId == buyerAppUserId, cancellationToken);

        if (marketItem is null)
        {
            return new MarketDeliveryResult { NewStatus = "DeliveryFailed", Message = "Purchased item was not found." };
        }

        if (marketItem.Status != "Sold" || marketItem.BuyerAppUserId is null)
        {
            return new MarketDeliveryResult { NewStatus = marketItem.DeliveryStatus ?? "DeliveryFailed", Message = "Only sold items can enter delivery flow." };
        }

        if (marketItem.DeliveryStatus == "Delivered")
        {
            return new MarketDeliveryResult
            {
                Success = true,
                NewStatus = marketItem.DeliveryStatus,
                DeliveryTradeOfferId = marketItem.DeliveryTradeOfferId,
                Message = "Item was already delivered."
            };
        }

        if (marketItem.DeliveryStatus == "DeliveryTradeCreated" || marketItem.DeliveryStatus == "AwaitingBuyerAction")
        {
            return new MarketDeliveryResult
            {
                Success = true,
                NewStatus = marketItem.DeliveryStatus,
                DeliveryTradeOfferId = marketItem.DeliveryTradeOfferId,
                Message = "Delivery trade already exists for this item."
            };
        }

        var buyer = await _dbContext.AppUsers
            .SingleOrDefaultAsync(user => user.Id == buyerAppUserId, cancellationToken);

        if (buyer is null)
        {
            return new MarketDeliveryResult { NewStatus = "DeliveryFailed", Message = "Buyer profile was not found." };
        }

        if (string.IsNullOrWhiteSpace(buyer.TradeUrl))
        {
            marketItem.UpdatedAtUtc = DateTime.UtcNow;
            marketItem.DeliveryErrorMessage = "Buyer Trade URL is required before delivery can start.";
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new MarketDeliveryResult { NewStatus = "DeliveryFailed", Message = marketItem.DeliveryErrorMessage };
        }

        marketItem.DeliveryStatus = "DeliveryBotPending";
        marketItem.UpdatedAtUtc = DateTime.UtcNow;
        marketItem.DeliveryErrorMessage = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (!_options.Enabled || !HasConfiguredCredentials())
        {
            marketItem.DeliveryStatus = "AwaitingBuyerAction";
            marketItem.UpdatedAtUtc = DateTime.UtcNow;
            marketItem.DeliveryErrorMessage = "Delivery bot integration is not fully configured yet. Complete bot settings to enable real delivery trade creation.";
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new MarketDeliveryResult
            {
                Success = true,
                NewStatus = marketItem.DeliveryStatus,
                Message = marketItem.DeliveryErrorMessage
            };
        }

        var deliveryResult = await _steamTradeClient.CreateDeliveryTradeAsync(marketItem, buyer, cancellationToken);
        marketItem.DeliveryStatus = deliveryResult.NewStatus;
        marketItem.DeliveryTradeOfferId = deliveryResult.DeliveryTradeOfferId;
        marketItem.UpdatedAtUtc = DateTime.UtcNow;
        marketItem.DeliveryErrorMessage = deliveryResult.Success ? null : deliveryResult.Message;

        if (!deliveryResult.Success && string.IsNullOrWhiteSpace(marketItem.DeliveryStatus))
        {
            marketItem.DeliveryStatus = "DeliveryFailed";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return deliveryResult;
    }

    public async Task<MarketDeliveryResult> ConfirmDeliveredAsync(Guid marketItemId, Guid buyerAppUserId, CancellationToken cancellationToken = default)
    {
        var marketItem = await _dbContext.MarketItems
            .SingleOrDefaultAsync(item => item.Id == marketItemId && item.BuyerAppUserId == buyerAppUserId, cancellationToken);

        if (marketItem is null)
        {
            return new MarketDeliveryResult { NewStatus = "DeliveryFailed", Message = "Purchased item was not found." };
        }

        if (marketItem.DeliveryStatus == "Delivered" || marketItem.DeliveredAtUtc.HasValue)
        {
            return new MarketDeliveryResult
            {
                Success = true,
                NewStatus = "Delivered",
                DeliveryTradeOfferId = marketItem.DeliveryTradeOfferId,
                Message = "Item was already marked as delivered."
            };
        }

        if (marketItem.DeliveryStatus != "DeliveryTradeCreated" && marketItem.DeliveryStatus != "AwaitingBuyerAction")
        {
            return new MarketDeliveryResult
            {
                NewStatus = marketItem.DeliveryStatus ?? "DeliveryFailed",
                DeliveryTradeOfferId = marketItem.DeliveryTradeOfferId,
                Message = "Only delivery-created items can be confirmed as delivered."
            };
        }

        marketItem.DeliveryStatus = "Delivered";
        marketItem.DeliveredAtUtc = DateTime.UtcNow;
        marketItem.UpdatedAtUtc = DateTime.UtcNow;
        marketItem.DeliveryErrorMessage = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new MarketDeliveryResult
        {
            Success = true,
            NewStatus = marketItem.DeliveryStatus,
            DeliveryTradeOfferId = marketItem.DeliveryTradeOfferId,
            Message = "Item marked as delivered."
        };
    }

    private bool HasConfiguredCredentials()
    {
        return !string.IsNullOrWhiteSpace(_options.BotSteamId) &&
               !string.IsNullOrWhiteSpace(_options.BotTradeUrl) &&
               !string.IsNullOrWhiteSpace(_options.ApiKey);
    }
}
