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
    private static readonly TimeSpan AwaitingUserActionWarningThreshold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AwaitingBuyerActionWarningThreshold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AwaitingBotConfirmationWarningThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AcceptedPendingReceiptWarningThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StaleWarningRepeatInterval = TimeSpan.FromHours(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SteamTradeSyncService> _logger;
    private readonly Dictionary<Guid, DateTime> _intakeWarningLogTimes = new();
    private readonly Dictionary<Guid, DateTime> _deliveryWarningLogTimes = new();

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
        var inventoryRefreshService = scope.ServiceProvider.GetRequiredService<ISteamInventoryRefreshService>();
        var gameCatalog = scope.ServiceProvider.GetRequiredService<IGameCatalog>();

        var operations = await dbContext.TradeOperations
            .Where(operation => operation.TradeOfferId != null &&
                                (operation.Status == "BotPending" ||
                                 operation.Status == "AwaitingBotConfirmation" ||
                                 operation.Status == "TradeCreated" ||
                                 operation.Status == "AwaitingUserAction" ||
                                 operation.Status == "TradeAcceptedPendingReceipt" ||
                                 operation.Status == "InEscrow"))
            .ToListAsync(cancellationToken);

        var deliveryItems = await dbContext.MarketPurchaseRecords
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
        var sellerRefreshRequests = new List<PendingInventoryRefreshRequest>();
        var buyerRefreshRequests = new List<(Guid BuyerAppUserId, GameType GameType, string Reason)>();
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

            var previousStatus = operation.Status;
            if (ApplyTradeOperationStatus(operation, status, transitionLogs))
            {
                stateChanged = true;
                if (!string.Equals(previousStatus, "ReceivedByBot", StringComparison.Ordinal) &&
                    string.Equals(operation.Status, "ReceivedByBot", StringComparison.Ordinal))
                {
                    sellerRefreshRequests.Add(new PendingInventoryRefreshRequest(
                        operation.SteamId,
                        ResolveGameType(gameCatalog, operation.AppId, operation.ContextId),
                        SteamInventoryRefreshReasons.TradeAccepted));
                }
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

            var previousDeliveryStatus = item.DeliveryStatus;
            if (ApplyDeliveryStatus(item, status, transitionLogs))
            {
                stateChanged = true;
                if (!string.Equals(previousDeliveryStatus, "Delivered", StringComparison.Ordinal) &&
                    string.Equals(item.DeliveryStatus, "Delivered", StringComparison.Ordinal) &&
                    item.BuyerAppUserId is Guid buyerAppUserId)
                {
                    buyerRefreshRequests.Add((
                        buyerAppUserId,
                        ResolveGameType(gameCatalog, item.AppId, item.ContextId),
                        SteamInventoryRefreshReasons.ItemDelivered));
                }
            }
        }

        AppendStaleWaitingWarnings(operations, deliveryItems, transitionLogs);

        if (stateChanged)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var entry in transitionLogs)
        {
            await appLogService.WriteAsync(entry.Level, entry.Message, entry.Source, cancellationToken: cancellationToken);
        }

        foreach (var request in sellerRefreshRequests)
        {
            if (string.IsNullOrWhiteSpace(request.SteamId))
            {
                continue;
            }

            await TryEnqueueInventoryRefreshAsync(
                inventoryRefreshService,
                appLogService,
                request.SteamId,
                request.GameType,
                request.Reason,
                cancellationToken,
                "intake trade sync");
        }

        if (buyerRefreshRequests.Count > 0)
        {
            var buyerIds = buyerRefreshRequests
                .Select(item => item.BuyerAppUserId)
                .Distinct()
                .ToList();
            var buyersById = await dbContext.AppUsers
                .AsNoTracking()
                .Where(user => buyerIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.SteamId, cancellationToken);

            foreach (var request in buyerRefreshRequests)
            {
                if (!buyersById.TryGetValue(request.BuyerAppUserId, out var buyerSteamId) ||
                    string.IsNullOrWhiteSpace(buyerSteamId))
                {
                    continue;
                }

                await TryEnqueueInventoryRefreshAsync(
                    inventoryRefreshService,
                    appLogService,
                    buyerSteamId,
                    request.GameType,
                    request.Reason,
                    cancellationToken,
                    "delivery trade sync");
            }
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

    public static bool ApplyTradeOperationStatus(
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
                operation.ErrorMessage = status.Message ?? "Trade offer is waiting for bot mobile confirmation.";
                break;
            case "Active":
                operation.Status = "AwaitingUserAction";
                operation.ErrorMessage = status.Message ?? "Trade offer is active and awaiting seller acceptance.";
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

        var changed = previousStatus != operation.Status || previousMessage != operation.ErrorMessage;
        if (changed)
        {
            operation.UpdatedAtUtc = DateTime.UtcNow;
            logs.Add((
                operation.Status == "Failed" ? "Warning" : "Info",
                $"Intake trade state changed. TradeOperationId={operation.Id}; OfferId={operation.TradeOfferId ?? "<null>"}; PreviousStatus={previousStatus}; NewStatus={operation.Status}; SteamState={status.State}; Message={operation.ErrorMessage ?? "<null>"}",
                nameof(SteamTradeSyncService)));
        }

        return changed;
    }

    public static bool ApplyDeliveryStatus(
        MarketPurchaseRecord item,
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
                item.DeliveryErrorMessage = status.Message ?? "Trade offer is waiting for bot mobile confirmation.";
                break;
            case "Active":
                item.DeliveryStatus = "AwaitingBuyerAction";
                item.DeliveryErrorMessage = status.Message ?? "Trade offer is active and awaiting buyer acceptance.";
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

        var changed = previousStatus != item.DeliveryStatus || previousMessage != item.DeliveryErrorMessage;
        if (changed)
        {
            item.UpdatedAtUtc = DateTime.UtcNow;
            logs.Add((
                item.DeliveryStatus == "DeliveryFailed" ? "Warning" : "Info",
                $"Delivery trade state changed. MarketPurchaseId={item.Id}; OfferId={item.DeliveryTradeOfferId ?? "<null>"}; PreviousStatus={previousStatus ?? "<null>"}; NewStatus={item.DeliveryStatus ?? "<null>"}; SteamState={status.State}; Message={item.DeliveryErrorMessage ?? "<null>"}",
                nameof(SteamTradeSyncService)));
        }

        return changed;
    }

    private void AppendStaleWaitingWarnings(
        IReadOnlyCollection<TradeOperation> operations,
        IReadOnlyCollection<MarketPurchaseRecord> deliveryItems,
        ICollection<(string Level, string Message, string Source)> logs)
    {
        var nowUtc = DateTime.UtcNow;
        AppendStaleIntakeWarnings(operations, logs, nowUtc);
        AppendStaleDeliveryWarnings(deliveryItems, logs, nowUtc);
    }

    private void AppendStaleIntakeWarnings(
        IEnumerable<TradeOperation> operations,
        ICollection<(string Level, string Message, string Source)> logs,
        DateTime nowUtc)
    {
        var activeWarnings = new HashSet<Guid>();
        foreach (var operation in operations)
        {
            if (!TryBuildStaleIntakeWarning(operation, nowUtc, out var message))
            {
                continue;
            }

            activeWarnings.Add(operation.Id);
            if (!ShouldLogStaleWarning(_intakeWarningLogTimes, operation.Id, nowUtc))
            {
                continue;
            }

            logs.Add(("Warning", message, nameof(SteamTradeSyncService)));
        }

        PruneInactiveWarnings(_intakeWarningLogTimes, activeWarnings);
    }

    private void AppendStaleDeliveryWarnings(
        IEnumerable<MarketPurchaseRecord> deliveryItems,
        ICollection<(string Level, string Message, string Source)> logs,
        DateTime nowUtc)
    {
        var activeWarnings = new HashSet<Guid>();
        foreach (var item in deliveryItems)
        {
            if (!TryBuildStaleDeliveryWarning(item, nowUtc, out var message))
            {
                continue;
            }

            activeWarnings.Add(item.Id);
            if (!ShouldLogStaleWarning(_deliveryWarningLogTimes, item.Id, nowUtc))
            {
                continue;
            }

            logs.Add(("Warning", message, nameof(SteamTradeSyncService)));
        }

        PruneInactiveWarnings(_deliveryWarningLogTimes, activeWarnings);
    }

    private static bool TryBuildStaleIntakeWarning(TradeOperation operation, DateTime nowUtc, out string message)
    {
        message = string.Empty;
        if (!TryDescribeIntakeWaitingStatus(operation.Status, out var threshold, out var waitingFor))
        {
            return false;
        }

        var age = nowUtc - operation.CreatedAtUtc;
        if (age < threshold)
        {
            return false;
        }

        message =
            $"Intake trade is waiting too long. TradeOperationId={operation.Id}; OfferId={operation.TradeOfferId ?? "<null>"}; Status={operation.Status}; WaitingFor={waitingFor}; AgeMinutes={(int)age.TotalMinutes}; CreatedAtUtc={operation.CreatedAtUtc:O}; Message={operation.ErrorMessage ?? "<null>"}";
        return true;
    }

    private static bool TryBuildStaleDeliveryWarning(MarketPurchaseRecord item, DateTime nowUtc, out string message)
    {
        message = string.Empty;
        if (!TryDescribeDeliveryWaitingStatus(item.DeliveryStatus, out var threshold, out var waitingFor))
        {
            return false;
        }

        var startedAtUtc = item.PurchasedAtUtc ?? item.CreatedAtUtc;
        var age = nowUtc - startedAtUtc;
        if (age < threshold)
        {
            return false;
        }

        message =
            $"Delivery trade is waiting too long. MarketPurchaseId={item.Id}; OfferId={item.DeliveryTradeOfferId ?? "<null>"}; Status={item.DeliveryStatus ?? "<null>"}; WaitingFor={waitingFor}; AgeMinutes={(int)age.TotalMinutes}; StartedAtUtc={startedAtUtc:O}; Message={item.DeliveryErrorMessage ?? "<null>"}";
        return true;
    }

    private static bool TryDescribeIntakeWaitingStatus(string? status, out TimeSpan threshold, out string waitingFor)
    {
        switch (status)
        {
            case "AwaitingBotConfirmation":
                threshold = AwaitingBotConfirmationWarningThreshold;
                waitingFor = "bot mobile confirmation";
                return true;
            case "TradeCreated":
            case "AwaitingUserAction":
                threshold = AwaitingUserActionWarningThreshold;
                waitingFor = "seller acceptance";
                return true;
            case "TradeAcceptedPendingReceipt":
                threshold = AcceptedPendingReceiptWarningThreshold;
                waitingFor = "Steam exchange details";
                return true;
            default:
                threshold = TimeSpan.Zero;
                waitingFor = string.Empty;
                return false;
        }
    }

    private static bool TryDescribeDeliveryWaitingStatus(string? status, out TimeSpan threshold, out string waitingFor)
    {
        switch (status)
        {
            case "AwaitingBotConfirmation":
                threshold = AwaitingBotConfirmationWarningThreshold;
                waitingFor = "bot mobile confirmation";
                return true;
            case "DeliveryTradeCreated":
            case "AwaitingBuyerAction":
                threshold = AwaitingBuyerActionWarningThreshold;
                waitingFor = "buyer acceptance";
                return true;
            default:
                threshold = TimeSpan.Zero;
                waitingFor = string.Empty;
                return false;
        }
    }

    private static bool ShouldLogStaleWarning(IDictionary<Guid, DateTime> cache, Guid entityId, DateTime nowUtc)
    {
        if (cache.TryGetValue(entityId, out var previousLoggedAtUtc) &&
            nowUtc - previousLoggedAtUtc < StaleWarningRepeatInterval)
        {
            return false;
        }

        cache[entityId] = nowUtc;
        return true;
    }

    private static void PruneInactiveWarnings(IDictionary<Guid, DateTime> cache, ISet<Guid> activeWarnings)
    {
        var staleKeys = cache.Keys
            .Where(key => !activeWarnings.Contains(key))
            .ToList();

        foreach (var staleKey in staleKeys)
        {
            cache.Remove(staleKey);
        }
    }

    private static GameType ResolveGameType(IGameCatalog gameCatalog, int appId, string contextId)
    {
        return gameCatalog.SupportedGames
            .FirstOrDefault(game => game.SteamAppId == appId &&
                                    string.Equals(game.SteamContextId.ToString(), contextId, StringComparison.Ordinal))
            ?.Type ?? gameCatalog.DefaultGameType;
    }

    private static async Task TryEnqueueInventoryRefreshAsync(
        ISteamInventoryRefreshService inventoryRefreshService,
        IAppLogService appLogService,
        string steamId,
        GameType gameType,
        string reason,
        CancellationToken cancellationToken,
        string source)
    {
        try
        {
            await inventoryRefreshService.EnqueueRefreshAsync(
                steamId,
                gameType,
                SteamInventoryRefreshPriority.High,
                cancellationToken,
                forceFreshness: true,
                reason: reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await appLogService.WriteAsync(
                "Warning",
                $"Inventory refresh enqueue failed after {source}. SteamId={steamId}; GameType={(int)gameType}; Reason={reason}; Message={exception.Message}",
                nameof(SteamTradeSyncService),
                exception,
                CancellationToken.None);
        }
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

    private readonly record struct PendingInventoryRefreshRequest(
        string SteamId,
        GameType GameType,
        string Reason);
}
