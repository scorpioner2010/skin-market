using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MarketPurchaseService : IMarketPurchaseService
{
    private readonly AppDbContext _dbContext;
    private readonly ISteamBotInventoryClient _steamBotInventoryClient;
    private readonly IMarketPricingService _marketPricingService;
    private readonly IGameCatalog _gameCatalog;
    private readonly ISteamInventoryRefreshService _steamInventoryRefreshService;
    private readonly SteamBotOptions _steamBotOptions;
    private readonly IAppLogService _appLogService;

    public MarketPurchaseService(
        AppDbContext dbContext,
        ISteamBotInventoryClient steamBotInventoryClient,
        IMarketPricingService marketPricingService,
        IGameCatalog gameCatalog,
        ISteamInventoryRefreshService steamInventoryRefreshService,
        IOptions<SteamBotOptions> steamBotOptions,
        IAppLogService appLogService)
    {
        _dbContext = dbContext;
        _steamBotInventoryClient = steamBotInventoryClient;
        _marketPricingService = marketPricingService;
        _gameCatalog = gameCatalog;
        _steamInventoryRefreshService = steamInventoryRefreshService;
        _steamBotOptions = steamBotOptions.Value;
        _appLogService = appLogService;
    }

    public async Task<MarketPurchaseResult> PurchaseAsync(
        MarketPurchaseRequest request,
        Guid buyerAppUserId,
        CancellationToken cancellationToken = default)
    {
        async Task<MarketPurchaseResult> FailAsync(string message)
        {
            await _appLogService.WriteAsync(
                "Warning",
                $"Market purchase failed. BuyerAppUserId={buyerAppUserId}; RequestedAssetId={request.AssetId}; MarketHashName={request.MarketHashName ?? "<null>"}; Message={message}",
                nameof(MarketPurchaseService),
                cancellationToken: cancellationToken);
            return new MarketPurchaseResult { Message = message };
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Market purchase started. BuyerAppUserId={buyerAppUserId}; RequestedAssetId={request.AssetId}; ClassId={request.ClassId}; InstanceId={request.InstanceId}; MarketHashName={request.MarketHashName ?? "<null>"}",
            nameof(MarketPurchaseService),
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(request.AssetId))
        {
            return await FailAsync("Market item was not found.");
        }

        var buyer = await _dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == buyerAppUserId, cancellationToken);

        if (buyer is null)
        {
            return await FailAsync("Local user profile was not found.");
        }

        if (string.IsNullOrWhiteSpace(_steamBotOptions.BotSteamId))
        {
            return await FailAsync("This item is no longer available.");
        }

        var game = _gameCatalog.Get(request.GameType);
        var liveInventory = await _steamBotInventoryClient.GetInventoryAsync(_steamBotOptions.BotSteamId, game.Type, cancellationToken);
        if (!liveInventory.IsSuccess)
        {
            return await FailAsync("This item is no longer available.");
        }

        var liveAssetKeySet = new HashSet<string>(
            liveInventory.Items.Select(item => BuildAssetKey(game.SteamAppId, game.SteamContextId.ToString(), item.AssetId)),
            StringComparer.Ordinal);
        var purchaseReservations = await _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .Where(
                item => item.AppId == game.SteamAppId &&
                        item.ContextId == game.SteamContextId.ToString())
            .ToListAsync(cancellationToken);
        var reservedAssetSet = new HashSet<string>(StringComparer.Ordinal);
        var reservedDiagnostics = new List<string>();
        foreach (var purchase in purchaseReservations)
        {
            var assetKey = BuildAssetKey(purchase.AppId, purchase.ContextId, purchase.AssetId);
            var decision = MarketReservationPolicy.GetDecision(purchase, liveAssetKeySet.Contains(assetKey));
            if (!decision.IsReserved)
            {
                continue;
            }

            reservedAssetSet.Add(purchase.AssetId);
            reservedDiagnostics.Add($"AssetId={purchase.AssetId}; PurchaseId={purchase.Id}; Status={purchase.Status}; DeliveryStatus={purchase.DeliveryStatus ?? "<null>"}; Reason={decision.Reason}");
            if (decision.ShouldWarn)
            {
                await _appLogService.WriteAsync(
                    "Warning",
                    $"Delivered item still appears in bot inventory. BuyerAppUserId={buyerAppUserId}; Game={game.Key}; AssetId={purchase.AssetId}; PurchaseId={purchase.Id}; SourceTradeOperationId={purchase.SourceTradeOperationId?.ToString() ?? "<null>"}",
                    nameof(MarketPurchaseService),
                    cancellationToken: cancellationToken);
            }
        }

        if (reservedDiagnostics.Count > 0)
        {
            await _appLogService.WriteAsync(
                "Debug",
                $"Market purchase reserved assets excluded. BuyerAppUserId={buyerAppUserId}; Game={game.Key}; Count={reservedDiagnostics.Count}; Items={string.Join(" || ", reservedDiagnostics.Take(20))}",
                nameof(MarketPurchaseService),
                cancellationToken: cancellationToken);
        }

        var matchingItems = liveInventory.Items
            .Where(item => MatchesRequestGroup(item, request) && !reservedAssetSet.Contains(item.AssetId))
            .ToList();
        var candidates = matchingItems
            .Where(item => item.Tradable == true)
            .ToList();
        if (candidates.Count == 0)
        {
            if (matchingItems.Count > 0)
            {
                return await FailAsync("This item is temporarily protected by Steam and cannot be delivered yet.");
            }

            return await FailAsync("Market item was not found.");
        }

        var candidateAssetIds = candidates
            .Select(candidate => candidate.AssetId)
            .ToArray();
        var sourceOperations = await _dbContext.TradeOperations
            .AsNoTracking()
            .Where(
                item => item.AppId == game.SteamAppId &&
                        item.ContextId == game.SteamContextId.ToString() &&
                        item.BotAssetId != null &&
                        candidateAssetIds.Contains(item.BotAssetId))
            .OrderByDescending(item => item.CreditedAtUtc ?? item.ReceivedByBotAtUtc ?? item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        var sourceOperationByAssetId = sourceOperations
            .GroupBy(item => item.BotAssetId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var inventoryItem = SelectCandidate(candidates, sourceOperationByAssetId, request, buyerAppUserId);
        if (inventoryItem is null)
        {
            return await FailAsync("Buying your own market item is not allowed.");
        }

        sourceOperationByAssetId.TryGetValue(inventoryItem.AssetId, out var sourceOperation);

        var price = await _marketPricingService.CalculatePriceAsync(inventoryItem, cancellationToken);
        if (buyer.Balance < price)
        {
            return await FailAsync("Not enough balance to buy this item.");
        }

        if (reservedAssetSet.Contains(inventoryItem.AssetId))
        {
            return await FailAsync("This item is no longer available.");
        }

        var executionStrategy = _dbContext.Database.CreateExecutionStrategy();
        var reservationResult = await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var isAlreadyReserved = await _dbContext.MarketPurchaseRecords
                .AsNoTracking()
                .AnyAsync(
                    item => item.AppId == game.SteamAppId &&
                            item.ContextId == game.SteamContextId.ToString() &&
                            item.AssetId == inventoryItem.AssetId &&
                            item.Status == "Sold" &&
                            (item.DeliveryStatus == null ||
                             MarketReservationPolicy.ActiveReservationDeliveryStatuses.Contains(item.DeliveryStatus)),
                    cancellationToken);
            if (isAlreadyReserved)
            {
                return await FailAsync("This item is no longer available.");
            }

            var deductedBuyerCount = await _dbContext.AppUsers
                .Where(user => user.Id == buyer.Id && user.Balance >= price)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(user => user.Balance, user => user.Balance - price),
                    cancellationToken);
            if (deductedBuyerCount != 1)
            {
                return await FailAsync("Not enough balance to buy this item.");
            }

            var now = DateTime.UtcNow;

            _dbContext.BalanceTransactions.Add(new BalanceTransaction
            {
                Id = Guid.NewGuid(),
                AppUserId = buyer.Id,
                Amount = -price,
                Type = "PurchaseFromMarket",
                CreatedAtUtc = now
            });

            _dbContext.MarketPurchaseRecords.Add(new MarketPurchaseRecord
            {
                Id = Guid.NewGuid(),
                GameType = game.Type,
                SourceTradeOperationId = sourceOperation?.Id,
                BuyerAppUserId = buyer.Id,
                AppId = game.SteamAppId,
                ContextId = game.SteamContextId.ToString(),
                AssetId = inventoryItem.AssetId,
                ClassId = inventoryItem.ClassId,
                InstanceId = inventoryItem.InstanceId,
                ItemName = string.IsNullOrWhiteSpace(inventoryItem.Name) ? "Unknown Item" : inventoryItem.Name,
                MarketHashName = MarketHashNameUtility.ResolvePrimary(inventoryItem),
                IconUrl = inventoryItem.IconUrl,
                Price = price,
                Status = "Sold",
                CreatedAtUtc = now,
                PurchasedAtUtc = now,
                UpdatedAtUtc = now,
                DeliveryStatus = "PendingDelivery"
            });

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return await FailAsync("This item is no longer available.");
            }

            return new MarketPurchaseResult
            {
                Success = true,
                Message = "Purchase completed. Delivery trade will start automatically."
            };
        });

        if (!reservationResult.Success)
        {
            return reservationResult;
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Market purchase finished. BuyerAppUserId={buyerAppUserId}; PurchasedAssetId={inventoryItem.AssetId}; SourceTradeOperationId={sourceOperation?.Id.ToString() ?? "<null>"}; Price={price:0.##}; DeliveryStatus=PendingDelivery",
            nameof(MarketPurchaseService),
            cancellationToken: cancellationToken);
        await TryEnqueueInventoryRefreshAsync(
            buyer.SteamId,
            game.Type,
            SteamInventoryRefreshReasons.UserBoughtItem,
            cancellationToken);

        return reservationResult;
    }

    public Task<List<MarketPurchaseRecord>> GetRecentPurchasesAsync(
        Guid buyerAppUserId,
        GameType gameType,
        int count,
        CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(gameType);
        return _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .Where(item =>
                item.BuyerAppUserId == buyerAppUserId &&
                item.AppId == game.SteamAppId &&
                item.ContextId == game.SteamContextId.ToString())
            .OrderByDescending(item => item.PurchasedAtUtc)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    private static SteamInventoryItemDto? SelectCandidate(
        IReadOnlyList<SteamInventoryItemDto> candidates,
        IReadOnlyDictionary<string, TradeOperation> sourceOperationByAssetId,
        MarketPurchaseRequest request,
        Guid buyerAppUserId)
    {
        var exactAssetCandidate = candidates.FirstOrDefault(item => string.Equals(item.AssetId, request.AssetId, StringComparison.Ordinal));
        if (exactAssetCandidate is not null &&
            !IsOwnedByBuyer(exactAssetCandidate, sourceOperationByAssetId, buyerAppUserId))
        {
            return exactAssetCandidate;
        }

        return candidates.FirstOrDefault(item => !IsOwnedByBuyer(item, sourceOperationByAssetId, buyerAppUserId));
    }

    private static bool IsOwnedByBuyer(
        SteamInventoryItemDto item,
        IReadOnlyDictionary<string, TradeOperation> sourceOperationByAssetId,
        Guid buyerAppUserId)
    {
        return sourceOperationByAssetId.TryGetValue(item.AssetId, out var sourceOperation) &&
               sourceOperation.AppUserId == buyerAppUserId;
    }

    private async Task TryEnqueueInventoryRefreshAsync(
        string steamId,
        GameType gameType,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await _steamInventoryRefreshService.EnqueueRefreshAsync(
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
            await _appLogService.WriteAsync(
                "Warning",
                $"Inventory refresh enqueue failed after market purchase. SteamId={steamId}; GameType={(int)gameType}; Reason={reason}; Message={exception.Message}",
                nameof(MarketPurchaseService),
                exception,
                CancellationToken.None);
        }
    }

    private static bool MatchesRequestGroup(SteamInventoryItemDto item, MarketPurchaseRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.MarketHashName))
        {
            var itemMarketHashName = MarketHashNameUtility.ResolvePrimary(item);
            if (!string.Equals(
                    MarketHashNameUtility.Normalize(itemMarketHashName),
                    MarketHashNameUtility.Normalize(request.MarketHashName),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ItemName) &&
            !string.Equals(item.Name, request.ItemName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.ClassId) &&
            !string.Equals(item.ClassId, request.ClassId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.InstanceId) &&
            !string.Equals(item.InstanceId, request.InstanceId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string BuildAssetKey(int appId, string contextId, string assetId)
    {
        return $"{appId}:{contextId}:{assetId}";
    }
}
