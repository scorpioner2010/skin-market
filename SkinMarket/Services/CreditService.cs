using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class CreditService : ICreditService
{
    private readonly AppDbContext _dbContext;
    private readonly IItemPricingService _itemPricingService;
    private readonly IMarketService _marketService;

    public CreditService(AppDbContext dbContext, IItemPricingService itemPricingService, IMarketService marketService)
    {
        _dbContext = dbContext;
        _itemPricingService = itemPricingService;
        _marketService = marketService;
    }

    public async Task<BotIntakeResult> ConfirmReceivedAndCreditAsync(Guid tradeOperationId, Guid appUserId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var operation = await _dbContext.TradeOperations
            .SingleOrDefaultAsync(item => item.Id == tradeOperationId && item.AppUserId == appUserId, cancellationToken);

        if (operation is null)
        {
            return new BotIntakeResult
            {
                NewStatus = "Failed",
                Message = "Sale request was not found."
            };
        }

        if (operation.Status == "Credited" || operation.CreditedAtUtc.HasValue)
        {
            return new BotIntakeResult
            {
                NewStatus = operation.Status,
                TradeOfferId = operation.TradeOfferId,
                Message = "This sale request was already credited."
            };
        }

        if (operation.Status != "TradeCreated" && operation.Status != "AwaitingUserAction" && operation.Status != "ReceivedByBot")
        {
            return new BotIntakeResult
            {
                NewStatus = operation.Status,
                TradeOfferId = operation.TradeOfferId,
                Message = "Only trade-created requests can be credited."
            };
        }

        var appUser = await _dbContext.AppUsers
            .SingleOrDefaultAsync(user => user.Id == appUserId, cancellationToken);

        if (appUser is null)
        {
            return new BotIntakeResult
            {
                NewStatus = "Failed",
                Message = "Local user profile was not found."
            };
        }

        if (operation.Status != "ReceivedByBot")
        {
            operation.Status = "ReceivedByBot";
            operation.UpdatedAtUtc = DateTime.UtcNow;
        }

        var amount = await _itemPricingService.CalculatePriceAsync(operation, cancellationToken);
        operation.CreditAmount = amount;
        operation.CreditedAtUtc = DateTime.UtcNow;
        operation.Status = "Credited";
        operation.UpdatedAtUtc = DateTime.UtcNow;
        operation.ErrorMessage = null;

        appUser.Balance += amount;

        _dbContext.BalanceTransactions.Add(new BalanceTransaction
        {
            Id = Guid.NewGuid(),
            AppUserId = appUser.Id,
            Amount = amount,
            Type = "CreditFromSale",
            CreatedAtUtc = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _marketService.CreateFromTradeOperationAsync(operation.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new BotIntakeResult
        {
            Success = true,
            NewStatus = operation.Status,
            TradeOfferId = operation.TradeOfferId,
            Message = $"Balance credited by {amount:0.##}."
        };
    }
}
