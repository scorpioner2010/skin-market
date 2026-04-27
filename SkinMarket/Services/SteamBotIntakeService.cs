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
    private readonly IAppLogService _appLogService;

    public SteamBotIntakeService(
        AppDbContext dbContext,
        ISteamTradeClient steamTradeClient,
        IOptions<SteamBotOptions> options,
        IAppLogService appLogService)
    {
        _dbContext = dbContext;
        _steamTradeClient = steamTradeClient;
        _options = options.Value;
        _appLogService = appLogService;
    }

    public async Task<BotIntakeResult> CreateIntakeRequestAsync(Guid tradeOperationId, Guid appUserId, CancellationToken cancellationToken = default)
    {
        async Task<BotIntakeResult> FailAsync(string status, string message)
        {
            await _appLogService.WriteAsync(
                "Warning",
                $"Intake request processing failed. TradeOperationId={tradeOperationId}; AppUserId={appUserId}; Status={status}; Message={message}",
                nameof(SteamBotIntakeService),
                cancellationToken: cancellationToken);
            return new BotIntakeResult
            {
                Success = false,
                NewStatus = status,
                Message = message
            };
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Intake request processing started. TradeOperationId={tradeOperationId}; AppUserId={appUserId}",
            nameof(SteamBotIntakeService),
            cancellationToken: cancellationToken);

        var operation = await _dbContext.TradeOperations
            .SingleOrDefaultAsync(
                item => item.Id == tradeOperationId && item.AppUserId == appUserId,
                cancellationToken);

        if (operation is null)
        {
            return await FailAsync("Failed", "Sale request was not found.");
        }

        if (!string.Equals(operation.Status, "Pending", StringComparison.Ordinal) &&
            !string.Equals(operation.Status, "Failed", StringComparison.Ordinal))
        {
            await _appLogService.WriteAsync(
                "Warning",
                $"Intake request processing skipped because status is not eligible. TradeOperationId={tradeOperationId}; AppUserId={appUserId}; Status={operation.Status}; OfferId={operation.TradeOfferId ?? "<null>"}",
                nameof(SteamBotIntakeService),
                cancellationToken: cancellationToken);
            return new BotIntakeResult
            {
                NewStatus = operation.Status,
                TradeOfferId = operation.TradeOfferId,
                Message = "Trade intake is available only for pending or failed sale requests."
            };
        }

        if (!_options.Enabled || !HasConfiguredCredentials())
        {
            operation.Status = "Failed";
            operation.UpdatedAtUtc = DateTime.UtcNow;
            operation.ErrorMessage = "Bot integration is not fully configured yet. Complete bot settings to enable real trade creation.";
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new BotIntakeResult
            {
                Success = false,
                NewStatus = operation.Status,
                Message = operation.ErrorMessage
            };
        }

        var seller = await _dbContext.AppUsers
            .SingleOrDefaultAsync(user => user.Id == appUserId, cancellationToken);

        if (seller is null)
        {
            return await FailAsync("Failed", "Local user profile was not found.");
        }

        if (string.IsNullOrWhiteSpace(seller.TradeUrl))
        {
            return await FailAsync("Failed", "Seller Trade URL is required before intake can start.");
        }

        operation.Status = "BotPending";
        operation.UpdatedAtUtc = DateTime.UtcNow;
        operation.ErrorMessage = null;
        operation.TradeOfferId = null;
        operation.BotAssetId = null;
        operation.BotClassId = null;
        operation.BotInstanceId = null;
        operation.BotTradeUrl = string.IsNullOrWhiteSpace(_options.BotTradeUrl) ? null : _options.BotTradeUrl;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var intakeResult = await _steamTradeClient.CreateIntakeTradeAsync(operation, seller, cancellationToken);
        operation.Status = intakeResult.Success ? intakeResult.NewStatus : "Failed";
        operation.TradeOfferId = intakeResult.TradeOfferId;
        operation.UpdatedAtUtc = DateTime.UtcNow;
        operation.BotTradeUrl = string.IsNullOrWhiteSpace(_options.BotTradeUrl) ? null : _options.BotTradeUrl;
        operation.ErrorMessage = intakeResult.Success ? null : intakeResult.Message;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _appLogService.WriteAsync(
            intakeResult.Success ? "Info" : "Warning",
            $"Intake request processing finished. TradeOperationId={operation.Id}; AppUserId={appUserId}; Success={intakeResult.Success}; Status={operation.Status}; OfferId={operation.TradeOfferId ?? "<null>"}; Message={intakeResult.Message}",
            nameof(SteamBotIntakeService),
            cancellationToken: cancellationToken);

        return intakeResult;
    }

    private bool HasConfiguredCredentials()
    {
        return !string.IsNullOrWhiteSpace(_options.Username) &&
               !string.IsNullOrWhiteSpace(_options.Password) &&
               !string.IsNullOrWhiteSpace(_options.BotSteamId);
    }
}
