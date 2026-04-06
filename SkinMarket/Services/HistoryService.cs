using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class HistoryService : IHistoryService
{
    private readonly AppDbContext _dbContext;
    private readonly IBalanceService _balanceService;

    public HistoryService(AppDbContext dbContext, IBalanceService balanceService)
    {
        _dbContext = dbContext;
        _balanceService = balanceService;
    }

    public async Task<HistoryPageData?> GetHistoryAsync(Guid appUserId, CancellationToken cancellationToken = default)
    {
        var userExists = await _dbContext.AppUsers
            .AsNoTracking()
            .AnyAsync(user => user.Id == appUserId, cancellationToken);

        if (!userExists)
        {
            return null;
        }

        var sales = await _dbContext.TradeOperations
            .AsNoTracking()
            .Where(operation => operation.AppUserId == appUserId)
            .OrderByDescending(operation => operation.CreatedAtUtc)
            .Select(operation => new SaleHistoryItem
            {
                ItemName = operation.ItemName,
                AssetId = operation.AssetId,
                Status = operation.Status,
                CreditAmount = operation.CreditAmount,
                CreatedAtUtc = operation.CreatedAtUtc,
                CreditedAtUtc = operation.CreditedAtUtc,
                TradeOfferId = operation.TradeOfferId,
                ErrorMessage = operation.ErrorMessage
            })
            .ToListAsync(cancellationToken);

        var purchases = await _dbContext.MarketItems
            .AsNoTracking()
            .Where(item => item.BuyerAppUserId == appUserId)
            .OrderByDescending(item => item.PurchasedAtUtc)
            .Select(item => new PurchaseHistoryItem
            {
                ItemName = item.ItemName,
                Price = item.Price,
                Status = item.Status,
                DeliveryStatus = item.DeliveryStatus,
                PurchasedAtUtc = item.PurchasedAtUtc,
                DeliveryTradeOfferId = item.DeliveryTradeOfferId,
                DeliveredAtUtc = item.DeliveredAtUtc,
                DeliveryErrorMessage = item.DeliveryErrorMessage
            })
            .ToListAsync(cancellationToken);

        var balanceTransactions = await _dbContext.BalanceTransactions
            .AsNoTracking()
            .Where(transaction => transaction.AppUserId == appUserId)
            .OrderByDescending(transaction => transaction.CreatedAtUtc)
            .Select(transaction => new BalanceHistoryItem
            {
                Type = transaction.Type,
                Amount = transaction.Amount,
                CreatedAtUtc = transaction.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new HistoryPageData
        {
            CurrentBalance = await _balanceService.GetBalanceAsync(appUserId, cancellationToken),
            Sales = sales,
            Purchases = purchases,
            BalanceTransactions = balanceTransactions
        };
    }
}
