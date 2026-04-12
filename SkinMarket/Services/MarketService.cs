using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MarketService : IMarketService
{
    private readonly AppDbContext _dbContext;
    private readonly IMarketPricingService _marketPricingService;

    public MarketService(AppDbContext dbContext, IMarketPricingService marketPricingService)
    {
        _dbContext = dbContext;
        _marketPricingService = marketPricingService;
    }

    public async Task<MarketItem?> CreateFromTradeOperationAsync(Guid tradeOperationId, CancellationToken cancellationToken = default)
    {
        var existingItem = await _dbContext.MarketItems
            .SingleOrDefaultAsync(item => item.SourceTradeOperationId == tradeOperationId, cancellationToken);

        if (existingItem is not null)
        {
            return existingItem;
        }

        var operation = await _dbContext.TradeOperations
            .SingleOrDefaultAsync(item => item.Id == tradeOperationId, cancellationToken);

        if (operation is null || operation.Status != "Credited")
        {
            return null;
        }

        var marketItem = new MarketItem
        {
            Id = Guid.NewGuid(),
            SourceTradeOperationId = operation.Id,
            AssetId = operation.BotAssetId ?? operation.AssetId,
            ClassId = operation.BotClassId ?? operation.ClassId,
            InstanceId = operation.BotInstanceId ?? operation.InstanceId,
            ItemName = operation.ItemName,
            MarketHashName = operation.MarketHashName,
            IconUrl = operation.IconUrl,
            Price = await _marketPricingService.CalculatePriceAsync(operation, cancellationToken),
            Status = "Available",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _dbContext.MarketItems.Add(marketItem);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return marketItem;
    }

    public async Task<List<MarketItem>> GetAvailableItemsAsync(CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.MarketItems
            .Include(item => item.SourceTradeOperation)
            .Where(item => item.Status == "Available")
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var hasPriceUpdates = false;
        foreach (var item in items)
        {
            if (item.SourceTradeOperation is null)
            {
                continue;
            }

            var resolvedPrice = await _marketPricingService.CalculatePriceAsync(item.SourceTradeOperation, cancellationToken);
            if (item.Price != resolvedPrice)
            {
                item.Price = resolvedPrice;
                item.UpdatedAtUtc = DateTime.UtcNow;
                hasPriceUpdates = true;
            }
        }

        if (hasPriceUpdates)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return items;
    }
}
