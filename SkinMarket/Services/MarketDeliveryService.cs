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
    private readonly IAppLogService _appLogService;

    public MarketDeliveryService(
        AppDbContext dbContext,
        ISteamTradeClient steamTradeClient,
        IOptions<SteamBotOptions> options,
        IAppLogService appLogService)
    {
        _dbContext = dbContext;
        _steamTradeClient = steamTradeClient;
        _options = options.Value;
        _appLogService = appLogService;
    }

    public async Task<MarketDeliveryResult> CreateDeliveryTradeAsync(Guid marketItemId, Guid buyerAppUserId, CancellationToken cancellationToken = default)
    {
        async Task<MarketDeliveryResult> FailAsync(string status, string message)
        {
            await _appLogService.WriteAsync(
                "Warning",
                $"Delivery creation failed. MarketPurchaseId={marketItemId}; BuyerAppUserId={buyerAppUserId}; Status={status}; Message={message}",
                nameof(MarketDeliveryService),
                cancellationToken: cancellationToken);
            return new MarketDeliveryResult { NewStatus = status, Message = message };
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Delivery creation started. MarketPurchaseId={marketItemId}; BuyerAppUserId={buyerAppUserId}",
            nameof(MarketDeliveryService),
            cancellationToken: cancellationToken);

        var marketItem = await _dbContext.MarketPurchaseRecords
            .SingleOrDefaultAsync(item => item.Id == marketItemId && item.BuyerAppUserId == buyerAppUserId, cancellationToken);

        if (marketItem is null)
        {
            return await FailAsync("DeliveryFailed", "Purchased item was not found.");
        }

        if (marketItem.Status != "Sold" || marketItem.BuyerAppUserId is null)
        {
            return await FailAsync(marketItem.DeliveryStatus ?? "DeliveryFailed", "Only sold items can enter delivery flow.");
        }

        if (marketItem.DeliveryStatus == "Delivered")
        {
            await _appLogService.WriteAsync(
                "Info",
                $"Delivery creation skipped because item is already delivered. MarketPurchaseId={marketItem.Id}; OfferId={marketItem.DeliveryTradeOfferId ?? "<null>"}",
                nameof(MarketDeliveryService),
                cancellationToken: cancellationToken);
            return new MarketDeliveryResult
            {
                Success = true,
                NewStatus = marketItem.DeliveryStatus,
                DeliveryTradeOfferId = marketItem.DeliveryTradeOfferId,
                Message = "Item was already delivered."
            };
        }

        if (marketItem.DeliveryStatus == "DeliveryTradeCreated" ||
            marketItem.DeliveryStatus == "AwaitingBuyerAction" ||
            marketItem.DeliveryStatus == "AwaitingBotConfirmation")
        {
            await _appLogService.WriteAsync(
                "Info",
                $"Delivery creation skipped because trade already exists. MarketPurchaseId={marketItem.Id}; DeliveryStatus={marketItem.DeliveryStatus}; OfferId={marketItem.DeliveryTradeOfferId ?? "<null>"}",
                nameof(MarketDeliveryService),
                cancellationToken: cancellationToken);
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
            return await FailAsync("DeliveryFailed", "Buyer profile was not found.");
        }

        if (string.IsNullOrWhiteSpace(buyer.TradeUrl))
        {
            marketItem.UpdatedAtUtc = DateTime.UtcNow;
            marketItem.DeliveryErrorMessage = "Buyer Trade URL is required before delivery can start.";
            await _dbContext.SaveChangesAsync(cancellationToken);

            return await FailAsync("DeliveryFailed", marketItem.DeliveryErrorMessage);
        }

        if (string.IsNullOrWhiteSpace(marketItem.AssetId) ||
            string.IsNullOrWhiteSpace(marketItem.ClassId) ||
            string.IsNullOrWhiteSpace(marketItem.InstanceId))
        {
            marketItem.UpdatedAtUtc = DateTime.UtcNow;
            marketItem.DeliveryErrorMessage = "Bot inventory asset mapping is missing for this market item.";
            await _dbContext.SaveChangesAsync(cancellationToken);

            return await FailAsync("DeliveryFailed", marketItem.DeliveryErrorMessage);
        }

        if (!_options.Enabled || !HasConfiguredCredentials())
        {
            marketItem.DeliveryStatus = "DeliveryFailed";
            marketItem.UpdatedAtUtc = DateTime.UtcNow;
            marketItem.DeliveryErrorMessage = "Delivery bot integration is not fully configured yet. Complete bot settings to enable real delivery trade creation.";
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new MarketDeliveryResult
            {
                Success = false,
                NewStatus = marketItem.DeliveryStatus,
                Message = marketItem.DeliveryErrorMessage
            };
        }

        marketItem.DeliveryStatus = "DeliveryBotPending";
        marketItem.UpdatedAtUtc = DateTime.UtcNow;
        marketItem.DeliveryErrorMessage = null;
        marketItem.DeliveryTradeOfferId = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var deliveryResult = await _steamTradeClient.CreateDeliveryTradeAsync(marketItem, buyer, cancellationToken);
        marketItem.DeliveryStatus = deliveryResult.Success ? deliveryResult.NewStatus : "DeliveryFailed";
        marketItem.DeliveryTradeOfferId = deliveryResult.DeliveryTradeOfferId;
        marketItem.UpdatedAtUtc = DateTime.UtcNow;
        marketItem.DeliveryErrorMessage = deliveryResult.Success ? null : deliveryResult.Message;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _appLogService.WriteAsync(
            deliveryResult.Success ? "Info" : "Warning",
            $"Delivery creation finished. MarketPurchaseId={marketItem.Id}; BuyerAppUserId={buyerAppUserId}; Success={deliveryResult.Success}; Status={marketItem.DeliveryStatus ?? "<null>"}; OfferId={marketItem.DeliveryTradeOfferId ?? "<null>"}; Message={deliveryResult.Message}",
            nameof(MarketDeliveryService),
            cancellationToken: cancellationToken);
        return deliveryResult;
    }

    public async Task<MarketDeliveryResult> ConfirmDeliveredAsync(Guid marketItemId, Guid buyerAppUserId, CancellationToken cancellationToken = default)
    {
        var marketItem = await _dbContext.MarketPurchaseRecords
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

        return new MarketDeliveryResult
        {
            Success = false,
            NewStatus = marketItem.DeliveryStatus ?? "DeliveryFailed",
            DeliveryTradeOfferId = marketItem.DeliveryTradeOfferId,
            Message = "Delivery completion is synchronized from Steam automatically."
        };
    }

    private bool HasConfiguredCredentials()
    {
        return !string.IsNullOrWhiteSpace(_options.Username) &&
               !string.IsNullOrWhiteSpace(_options.Password) &&
               !string.IsNullOrWhiteSpace(_options.BotSteamId);
    }
}
