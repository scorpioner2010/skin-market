using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamBotIntakeService : ISteamBotIntakeService
{
    private readonly AppDbContext _dbContext;
    private readonly ISteamTradeClient _steamTradeClient;
    private readonly SteamBotOptions _options;

    public SteamBotIntakeService(
        AppDbContext dbContext,
        ISteamTradeClient steamTradeClient,
        IOptions<SteamBotOptions> options)
    {
        _dbContext = dbContext;
        _steamTradeClient = steamTradeClient;
        _options = options.Value;
    }

    public async Task<BotIntakeResult> CreateIntakeRequestAsync(Guid tradeOperationId, Guid appUserId, CancellationToken cancellationToken = default)
    {
        var operation = await _dbContext.TradeOperations
            .SingleOrDefaultAsync(
                item => item.Id == tradeOperationId && item.AppUserId == appUserId,
                cancellationToken);

        if (operation is null)
        {
            return new BotIntakeResult
            {
                NewStatus = "Failed",
                Message = "Sale request was not found."
            };
        }

        if (!string.Equals(operation.Status, "Pending", StringComparison.Ordinal))
        {
            return new BotIntakeResult
            {
                NewStatus = operation.Status,
                TradeOfferId = operation.TradeOfferId,
                Message = "Trade intake is available only for pending sale requests."
            };
        }

        operation.Status = "BotPending";
        operation.UpdatedAtUtc = DateTime.UtcNow;
        operation.ErrorMessage = null;
        operation.BotTradeUrl = string.IsNullOrWhiteSpace(_options.BotTradeUrl) ? null : _options.BotTradeUrl;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (!_options.Enabled || !HasConfiguredCredentials())
        {
            operation.Status = "AwaitingUserAction";
            operation.UpdatedAtUtc = DateTime.UtcNow;
            operation.ErrorMessage = "Bot integration is not fully configured yet. Complete bot settings to enable real trade creation.";
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new BotIntakeResult
            {
                Success = true,
                NewStatus = operation.Status,
                Message = operation.ErrorMessage
            };
        }

        var intakeResult = await _steamTradeClient.CreateIntakeTradeAsync(operation, cancellationToken);
        operation.Status = intakeResult.NewStatus;
        operation.TradeOfferId = intakeResult.TradeOfferId;
        operation.UpdatedAtUtc = DateTime.UtcNow;
        operation.BotTradeUrl = string.IsNullOrWhiteSpace(_options.BotTradeUrl) ? null : _options.BotTradeUrl;
        operation.ErrorMessage = intakeResult.Success ? null : intakeResult.Message;

        if (!intakeResult.Success && string.IsNullOrWhiteSpace(operation.Status))
        {
            operation.Status = "Failed";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return intakeResult;
    }

    private bool HasConfiguredCredentials()
    {
        return !string.IsNullOrWhiteSpace(_options.BotSteamId) &&
               !string.IsNullOrWhiteSpace(_options.BotTradeUrl) &&
               !string.IsNullOrWhiteSpace(_options.ApiKey);
    }
}
