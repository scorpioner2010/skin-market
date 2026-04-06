using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MarketPurchaseService : IMarketPurchaseService
{
    private readonly AppDbContext _dbContext;

    public MarketPurchaseService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<MarketPurchaseResult> PurchaseAsync(Guid marketItemId, Guid buyerAppUserId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var marketItem = await _dbContext.MarketItems
            .Include(item => item.SourceTradeOperation)
            .SingleOrDefaultAsync(item => item.Id == marketItemId, cancellationToken);

        if (marketItem is null)
        {
            return new MarketPurchaseResult { Message = "Market item was not found." };
        }

        if (marketItem.Status != "Available")
        {
            return new MarketPurchaseResult { Message = "This item is no longer available." };
        }

        var buyer = await _dbContext.AppUsers
            .SingleOrDefaultAsync(user => user.Id == buyerAppUserId, cancellationToken);

        if (buyer is null)
        {
            return new MarketPurchaseResult { Message = "Local user profile was not found." };
        }

        if (marketItem.SourceTradeOperation?.AppUserId == buyerAppUserId)
        {
            return new MarketPurchaseResult { Message = "Buying your own market item is not allowed." };
        }

        if (buyer.Balance < marketItem.Price)
        {
            return new MarketPurchaseResult { Message = "Not enough balance to buy this item." };
        }

        buyer.Balance -= marketItem.Price;

        _dbContext.BalanceTransactions.Add(new BalanceTransaction
        {
            Id = Guid.NewGuid(),
            AppUserId = buyer.Id,
            Amount = -marketItem.Price,
            Type = "PurchaseFromMarket",
            CreatedAtUtc = DateTime.UtcNow
        });

        marketItem.Status = "Sold";
        marketItem.BuyerAppUserId = buyer.Id;
        marketItem.PurchasedAtUtc = DateTime.UtcNow;
        marketItem.UpdatedAtUtc = DateTime.UtcNow;
        marketItem.DeliveryStatus = "PendingDelivery";

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new MarketPurchaseResult
        {
            Success = true,
            Message = "Purchase completed. Item is pending delivery."
        };
    }

    public Task<List<MarketItem>> GetRecentPurchasesAsync(Guid buyerAppUserId, int count, CancellationToken cancellationToken = default)
    {
        return _dbContext.MarketItems
            .AsNoTracking()
            .Where(item => item.BuyerAppUserId == buyerAppUserId)
            .OrderByDescending(item => item.PurchasedAtUtc)
            .Take(count)
            .ToListAsync(cancellationToken);
    }
}
