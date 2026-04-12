using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamTradeSyncService : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(60);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SteamTradeSyncService> _logger;

    public SteamTradeSyncService(IServiceScopeFactory scopeFactory, ILogger<SteamTradeSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Steam trade sync worker started.");

        using var timer = new PeriodicTimer(SyncInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Steam trade sync iteration failed.");
                await PersistWorkerFailureAsync(exception, stoppingToken);
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Steam trade sync worker stopped.");
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SteamBotOptions>>().Value;
        if (!options.Enabled)
        {
            return;
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tradeClient = scope.ServiceProvider.GetRequiredService<ISteamTradeClient>();
        var appLogService = scope.ServiceProvider.GetRequiredService<IAppLogService>();
        var creditService = scope.ServiceProvider.GetRequiredService<ICreditService>();

        var operations = await dbContext.TradeOperations
            .Where(operation => operation.TradeOfferId != null &&
                                (operation.Status == "BotPending" ||
                                 operation.Status == "AwaitingBotConfirmation" ||
                                 operation.Status == "TradeCreated" ||
                                 operation.Status == "AwaitingUserAction" ||
                                 operation.Status == "TradeAcceptedPendingReceipt" ||
                                 operation.Status == "InEscrow"))
            .ToListAsync(cancellationToken);

        var deliveryItems = await dbContext.MarketItems
            .Where(item => item.DeliveryTradeOfferId != null &&
                           (item.DeliveryStatus == "DeliveryBotPending" ||
                            item.DeliveryStatus == "AwaitingBotConfirmation" ||
                            item.DeliveryStatus == "DeliveryTradeCreated" ||
                            item.DeliveryStatus == "AwaitingBuyerAction" ||
                            item.DeliveryStatus == "DeliveryInEscrow"))
            .ToListAsync(cancellationToken);

        var pendingCredits = await dbContext.TradeOperations
            .Where(operation => operation.Status == "ReceivedByBot" && !operation.CreditedAtUtc.HasValue)
            .Select(operation => new { operation.Id, operation.AppUserId })
            .ToListAsync(cancellationToken);

        if (operations.Count == 0 && deliveryItems.Count == 0 && pendingCredits.Count == 0)
        {
            return;
        }

        var requests = operations
            .Where(operation => !string.IsNullOrWhiteSpace(operation.TradeOfferId))
            .Select(operation => new SteamTradeOfferStatusRequest
            {
                OfferId = operation.TradeOfferId!,
                Flow = "intake"
            })
            .Concat(deliveryItems
                .Where(item => !string.IsNullOrWhiteSpace(item.DeliveryTradeOfferId))
                .Select(item => new SteamTradeOfferStatusRequest
                {
                    OfferId = item.DeliveryTradeOfferId!,
                    Flow = "delivery"
                }))
            .ToList();

        var statusResults = await tradeClient.GetOfferStatusesAsync(requests, cancellationToken);
        var statusMap = statusResults.ToDictionary(
            item => $"{item.Flow}:{item.OfferId}",
            item => item,
            StringComparer.Ordinal);

        var transitionLogs = new List<(string Level, string Message, string Source)>();
        var stateChanged = false;

        foreach (var operation in operations)
        {
            if (string.IsNullOrWhiteSpace(operation.TradeOfferId))
            {
                continue;
            }

            if (!statusMap.TryGetValue($"intake:{operation.TradeOfferId}", out var status))
            {
                continue;
            }

            if (ApplyTradeOperationStatus(operation, status, transitionLogs))
            {
                stateChanged = true;
            }
        }

        foreach (var item in deliveryItems)
        {
            if (string.IsNullOrWhiteSpace(item.DeliveryTradeOfferId))
            {
                continue;
            }

            if (!statusMap.TryGetValue($"delivery:{item.DeliveryTradeOfferId}", out var status))
            {
                continue;
            }

            if (ApplyDeliveryStatus(item, status, transitionLogs))
            {
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var entry in transitionLogs)
        {
            await appLogService.WriteAsync(entry.Level, entry.Message, entry.Source, cancellationToken: cancellationToken);
        }

        var creditTargets = pendingCredits
            .Concat(operations
                .Where(operation => operation.Status == "ReceivedByBot" && !operation.CreditedAtUtc.HasValue)
                .Select(operation => new { operation.Id, operation.AppUserId }))
            .DistinctBy(item => item.Id)
            .ToList();

        foreach (var target in creditTargets)
        {
            var result = await creditService.ConfirmReceivedAndCreditAsync(target.Id, target.AppUserId, cancellationToken);
            await appLogService.WriteAsync(
                result.Success ? "Info" : "Warning",
                $"Auto credit result. TradeOperationId={target.Id}; Success={result.Success}; Status={result.NewStatus}; OfferId={result.TradeOfferId ?? "<null>"}; Message={result.Message}",
                nameof(SteamTradeSyncService),
                cancellationToken: cancellationToken);
        }
    }

    private static bool ApplyTradeOperationStatus(
        TradeOperation operation,
        SteamTradeOfferStatusResult status,
        ICollection<(string Level, string Message, string Source)> logs)
    {
        var previousStatus = operation.Status;
        var previousMessage = operation.ErrorMessage;

        switch (status.State)
        {
            case "OfferNotFound":
                operation.Status = "Failed";
                operation.ErrorMessage = status.Message ?? "Steam could not find this intake trade offer.";
                break;
            case "CreatedNeedsConfirmation":
                operation.Status = "AwaitingBotConfirmation";
                operation.ErrorMessage = status.Message;
                break;
            case "Active":
                operation.Status = "TradeCreated";
                operation.ErrorMessage = null;
                break;
            case "AcceptedPendingReceipt":
                operation.Status = "TradeAcceptedPendingReceipt";
                operation.ErrorMessage = status.Message ?? "Trade offer was accepted and exchange details are still pending.";
                break;
            case "Accepted":
                operation.Status = "ReceivedByBot";
                operation.ErrorMessage = null;
                operation.ReceivedByBotAtUtc ??= DateTime.UtcNow;
                if (status.ReceivedItem is not null)
                {
                    operation.BotAssetId = status.ReceivedItem.AssetId;
                    operation.BotClassId = status.ReceivedItem.ClassId;
                    operation.BotInstanceId = status.ReceivedItem.InstanceId;
                    operation.AppId = status.ReceivedItem.AppId;
                    operation.ContextId = status.ReceivedItem.ContextId;
                }
                break;
            case "InEscrow":
                operation.Status = "InEscrow";
                operation.ErrorMessage = status.Message ?? "Trade offer is in escrow and cannot be credited yet.";
                break;
            case "Declined":
            case "Canceled":
            case "Expired":
            case "Invalid":
            case "InvalidItems":
            case "CanceledBySecondFactor":
            case "Countered":
            case "Failed":
                operation.Status = "Failed";
                operation.ErrorMessage = status.Message ?? $"Trade offer ended with state {status.State}.";
                break;
        }

        operation.UpdatedAtUtc = DateTime.UtcNow;
        var changed = previousStatus != operation.Status || previousMessage != operation.ErrorMessage;
        if (changed)
        {
            logs.Add((
                operation.Status == "Failed" ? "Warning" : "Info",
                $"Intake trade state changed. TradeOperationId={operation.Id}; OfferId={operation.TradeOfferId ?? "<null>"}; PreviousStatus={previousStatus}; NewStatus={operation.Status}; SteamState={status.State}; Message={operation.ErrorMessage ?? "<null>"}",
                nameof(SteamTradeSyncService)));
        }

        return changed;
    }

    private static bool ApplyDeliveryStatus(
        MarketItem item,
        SteamTradeOfferStatusResult status,
        ICollection<(string Level, string Message, string Source)> logs)
    {
        var previousStatus = item.DeliveryStatus;
        var previousMessage = item.DeliveryErrorMessage;

        switch (status.State)
        {
            case "OfferNotFound":
                item.DeliveryStatus = "DeliveryFailed";
                item.DeliveryErrorMessage = status.Message ?? "Steam could not find this delivery trade offer.";
                break;
            case "CreatedNeedsConfirmation":
                item.DeliveryStatus = "AwaitingBotConfirmation";
                item.DeliveryErrorMessage = status.Message;
                break;
            case "Active":
                item.DeliveryStatus = "DeliveryTradeCreated";
                item.DeliveryErrorMessage = null;
                break;
            case "Accepted":
                item.DeliveryStatus = "Delivered";
                item.DeliveredAtUtc ??= DateTime.UtcNow;
                item.DeliveryErrorMessage = null;
                break;
            case "InEscrow":
                item.DeliveryStatus = "DeliveryInEscrow";
                item.DeliveryErrorMessage = status.Message ?? "Delivery trade is in escrow and not completed yet.";
                break;
            case "Declined":
            case "Canceled":
            case "Expired":
            case "Invalid":
            case "InvalidItems":
            case "CanceledBySecondFactor":
            case "Countered":
            case "Failed":
                item.DeliveryStatus = "DeliveryFailed";
                item.DeliveryErrorMessage = status.Message ?? $"Delivery trade ended with state {status.State}.";
                break;
        }

        item.UpdatedAtUtc = DateTime.UtcNow;
        var changed = previousStatus != item.DeliveryStatus || previousMessage != item.DeliveryErrorMessage;
        if (changed)
        {
            logs.Add((
                item.DeliveryStatus == "DeliveryFailed" ? "Warning" : "Info",
                $"Delivery trade state changed. MarketItemId={item.Id}; OfferId={item.DeliveryTradeOfferId ?? "<null>"}; PreviousStatus={previousStatus ?? "<null>"}; NewStatus={item.DeliveryStatus ?? "<null>"}; SteamState={status.State}; Message={item.DeliveryErrorMessage ?? "<null>"}",
                nameof(SteamTradeSyncService)));
        }

        return changed;
    }

    private async Task PersistWorkerFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var appLogService = scope.ServiceProvider.GetRequiredService<IAppLogService>();
        await appLogService.WriteAsync(
            "Error",
            "Steam trade sync worker iteration failed.",
            nameof(SteamTradeSyncService),
            exception,
            cancellationToken);
    }
}
