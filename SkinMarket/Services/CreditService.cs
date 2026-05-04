using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class CreditService : ICreditService
{
    private readonly AppDbContext _dbContext;
    private readonly IItemPricingService _itemPricingService;
    private readonly ISteamInventoryRefreshService _steamInventoryRefreshService;
    private readonly IGameCatalog _gameCatalog;
    private readonly IAppLogService _appLogService;

    public CreditService(
        AppDbContext dbContext,
        IItemPricingService itemPricingService,
        ISteamInventoryRefreshService steamInventoryRefreshService,
        IGameCatalog gameCatalog,
        IAppLogService appLogService)
    {
        _dbContext = dbContext;
        _itemPricingService = itemPricingService;
        _steamInventoryRefreshService = steamInventoryRefreshService;
        _gameCatalog = gameCatalog;
        _appLogService = appLogService;
    }

    public async Task<BotIntakeResult> ConfirmReceivedAndCreditAsync(Guid tradeOperationId, Guid appUserId, CancellationToken cancellationToken = default)
    {
        async Task<BotIntakeResult> FailAsync(string status, string message, string? offerId = null)
        {
            await _appLogService.WriteAsync(
                "Warning",
                $"Credit processing failed. TradeOperationId={tradeOperationId}; AppUserId={appUserId}; Status={status}; OfferId={offerId ?? "<null>"}; Message={message}",
                nameof(CreditService),
                cancellationToken: cancellationToken);
            return new BotIntakeResult
            {
                NewStatus = status,
                TradeOfferId = offerId,
                Message = message
            };
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Credit processing started. TradeOperationId={tradeOperationId}; AppUserId={appUserId}",
            nameof(CreditService),
            cancellationToken: cancellationToken);

        var operation = await _dbContext.TradeOperations
            .SingleOrDefaultAsync(item => item.Id == tradeOperationId && item.AppUserId == appUserId, cancellationToken);

        if (operation is null)
        {
            return await FailAsync("Failed", "Sale request was not found.");
        }

        if (operation.Status == "Credited" || operation.CreditedAtUtc.HasValue)
        {
            return await FailAsync(operation.Status, "This sale request was already credited.", operation.TradeOfferId);
        }

        if (operation.Status != "ReceivedByBot")
        {
            return await FailAsync(operation.Status, "Only Steam-confirmed intake requests can be credited.", operation.TradeOfferId);
        }

        var appUser = await _dbContext.AppUsers
            .SingleOrDefaultAsync(user => user.Id == appUserId, cancellationToken);

        if (appUser is null)
        {
            return await FailAsync("Failed", "Local user profile was not found.");
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
        await _appLogService.WriteAsync(
            "Info",
            $"Credit processing finished. TradeOperationId={operation.Id}; AppUserId={appUserId}; Amount={amount:0.##}; Status={operation.Status}",
            nameof(CreditService),
            cancellationToken: cancellationToken);
        await TryEnqueueInventoryRefreshAsync(
            appUser.SteamId,
            ResolveGameType(operation.AppId, operation.ContextId),
            SteamInventoryRefreshReasons.ItemCredited,
            cancellationToken);

        return new BotIntakeResult
        {
            Success = true,
            NewStatus = operation.Status,
            TradeOfferId = operation.TradeOfferId,
            Message = $"Balance credited by {amount:0.##}."
        };
    }

    private GameType ResolveGameType(int appId, string contextId)
    {
        return _gameCatalog.SupportedGames
            .FirstOrDefault(game => game.SteamAppId == appId &&
                                    string.Equals(game.SteamContextId.ToString(), contextId, StringComparison.Ordinal))
            ?.Type ?? _gameCatalog.DefaultGameType;
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
                $"Inventory refresh enqueue failed after credit. SteamId={steamId}; GameType={(int)gameType}; Reason={reason}; Message={exception.Message}",
                nameof(CreditService),
                exception,
                CancellationToken.None);
        }
    }
}
