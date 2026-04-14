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
    private readonly SteamBotOptions _steamBotOptions;

    public MarketPurchaseService(
        AppDbContext dbContext,
        ISteamBotInventoryClient steamBotInventoryClient,
        IMarketPricingService marketPricingService,
        IGameCatalog gameCatalog,
        IOptions<SteamBotOptions> steamBotOptions)
    {
        _dbContext = dbContext;
        _steamBotInventoryClient = steamBotInventoryClient;
        _marketPricingService = marketPricingService;
        _gameCatalog = gameCatalog;
        _steamBotOptions = steamBotOptions.Value;
    }

    public async Task<MarketPurchaseResult> PurchaseAsync(
        MarketPurchaseRequest request,
        Guid buyerAppUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.AssetId))
        {
            return new MarketPurchaseResult { Message = "Market item was not found." };
        }

        var buyer = await _dbContext.AppUsers
            .SingleOrDefaultAsync(user => user.Id == buyerAppUserId, cancellationToken);

        if (buyer is null)
        {
            return new MarketPurchaseResult { Message = "Local user profile was not found." };
        }

        if (string.IsNullOrWhiteSpace(_steamBotOptions.BotSteamId))
        {
            return new MarketPurchaseResult { Message = "This item is no longer available." };
        }

        var game = _gameCatalog.Get(request.GameType);
        var liveInventory = await _steamBotInventoryClient.GetInventoryAsync(_steamBotOptions.BotSteamId, game.Type, cancellationToken);
        if (!liveInventory.IsSuccess)
        {
            return new MarketPurchaseResult { Message = "This item is no longer available." };
        }

        var reservedAssetIds = await _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .Where(
                item => item.AppId == game.SteamAppId &&
                        item.ContextId == game.SteamContextId.ToString())
            .Select(item => item.AssetId)
            .ToListAsync(cancellationToken);
        var reservedAssetSet = new HashSet<string>(reservedAssetIds, StringComparer.Ordinal);

        var candidates = liveInventory.Items
            .Where(item => MatchesRequestGroup(item, request) && !reservedAssetSet.Contains(item.AssetId))
            .ToList();
        if (candidates.Count == 0)
        {
            return new MarketPurchaseResult { Message = "Market item was not found." };
        }

        var sourceOperations = await _dbContext.TradeOperations
            .AsNoTracking()
            .Where(
                item => item.AppId == game.SteamAppId &&
                        item.ContextId == game.SteamContextId.ToString() &&
                        item.BotAssetId != null &&
                        candidates.Select(candidate => candidate.AssetId).Contains(item.BotAssetId))
            .OrderByDescending(item => item.CreditedAtUtc ?? item.ReceivedByBotAtUtc ?? item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        var sourceOperationByAssetId = sourceOperations
            .GroupBy(item => item.BotAssetId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var inventoryItem = SelectCandidate(candidates, sourceOperationByAssetId, request, buyerAppUserId);
        if (inventoryItem is null)
        {
            return new MarketPurchaseResult { Message = "Buying your own market item is not allowed." };
        }

        sourceOperationByAssetId.TryGetValue(inventoryItem.AssetId, out var sourceOperation);

        var price = await _marketPricingService.CalculatePriceAsync(inventoryItem, cancellationToken);
        if (buyer.Balance < price)
        {
            return new MarketPurchaseResult { Message = "Not enough balance to buy this item." };
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var duplicateSaleExists = await _dbContext.MarketPurchaseRecords
            .AnyAsync(
                item => item.AppId == game.SteamAppId &&
                        item.ContextId == game.SteamContextId.ToString() &&
                        item.AssetId == inventoryItem.AssetId,
                cancellationToken);
        if (duplicateSaleExists)
        {
            return new MarketPurchaseResult { Message = "This item is no longer available." };
        }

        var now = DateTime.UtcNow;
        buyer.Balance -= price;

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
            await transaction.RollbackAsync(cancellationToken);
            return new MarketPurchaseResult { Message = "This item is no longer available." };
        }

        return new MarketPurchaseResult
        {
            Success = true,
            Message = "Purchase completed. Delivery trade will start automatically."
        };
    }

    public Task<List<MarketPurchaseRecord>> GetRecentPurchasesAsync(
        Guid buyerAppUserId,
        int count,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .Where(item => item.BuyerAppUserId == buyerAppUserId)
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
}
