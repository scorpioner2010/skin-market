using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;

namespace SkinMarket.Services;

public sealed class SteamTradeAutomationService : BackgroundService
{
    private static readonly TimeSpan AutomationInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(3);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<SteamBotOptions> _options;
    private readonly ILogger<SteamTradeAutomationService> _logger;

    public SteamTradeAutomationService(
        IServiceScopeFactory scopeFactory,
        IOptions<SteamBotOptions> options,
        ILogger<SteamTradeAutomationService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Steam trade automation worker started.");

        using var timer = new PeriodicTimer(AutomationInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AutomateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Steam trade automation iteration failed.");
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

        _logger.LogInformation("Steam trade automation worker stopped.");
    }

    private async Task AutomateAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return;
        }

        var health = await GetHealthAsync(options.ServiceUrl, cancellationToken);
        if (!health.Reachable || !health.Ready)
        {
            _logger.LogDebug(
                "Skipping trade automation because bot service is not ready. Reachable={Reachable} Ready={Ready} LastError={LastError}",
                health.Reachable,
                health.Ready,
                health.LastError ?? "<none>");
            return;
        }

        var pendingIntakes = await LoadPendingIntakesAsync(cancellationToken);
        var pendingDeliveries = await LoadPendingDeliveriesAsync(cancellationToken);
        if (pendingIntakes.Count == 0 && pendingDeliveries.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Steam trade automation picked up {IntakeCount} intake(s) and {DeliveryCount} delivery(ies).",
            pendingIntakes.Count,
            pendingDeliveries.Count);

        foreach (var pendingIntake in pendingIntakes)
        {
            await ProcessPendingIntakeAsync(pendingIntake, cancellationToken);
        }

        foreach (var pendingDelivery in pendingDeliveries)
        {
            await ProcessPendingDeliveryAsync(pendingDelivery, cancellationToken);
        }
    }

    private async Task<List<PendingIntakeItem>> LoadPendingIntakesAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.TradeOperations
            .AsNoTracking()
            .Where(operation => operation.Status == "Pending")
            .OrderBy(operation => operation.CreatedAtUtc)
            .Take(25)
            .Select(operation => new PendingIntakeItem(operation.Id, operation.AppUserId))
            .ToListAsync(cancellationToken);
    }

    private async Task<List<PendingDeliveryItem>> LoadPendingDeliveriesAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .Where(item => item.Status == "Sold" &&
                           item.BuyerAppUserId != null &&
                           item.DeliveryStatus == "PendingDelivery")
            .OrderBy(item => item.PurchasedAtUtc ?? item.CreatedAtUtc)
            .Take(25)
            .Select(item => new PendingDeliveryItem(item.Id, item.BuyerAppUserId!.Value))
            .ToListAsync(cancellationToken);
    }

    private async Task ProcessPendingIntakeAsync(PendingIntakeItem pendingItem, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var intakeService = scope.ServiceProvider.GetRequiredService<ISteamBotIntakeService>();
        var appLogService = scope.ServiceProvider.GetRequiredService<IAppLogService>();

        var result = await intakeService.CreateIntakeRequestAsync(
            pendingItem.TradeOperationId,
            pendingItem.AppUserId,
            cancellationToken);

        await appLogService.WriteAsync(
            result.Success ? "Info" : "Warning",
            $"Automatic intake attempt finished. TradeOperationId={pendingItem.TradeOperationId}; Success={result.Success}; Status={result.NewStatus}; OfferId={result.TradeOfferId ?? "<null>"}; Message={result.Message}",
            nameof(SteamTradeAutomationService),
            cancellationToken: cancellationToken);
    }

    private async Task ProcessPendingDeliveryAsync(PendingDeliveryItem pendingItem, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var deliveryService = scope.ServiceProvider.GetRequiredService<IMarketDeliveryService>();
        var appLogService = scope.ServiceProvider.GetRequiredService<IAppLogService>();

        var result = await deliveryService.CreateDeliveryTradeAsync(
            pendingItem.MarketPurchaseId,
            pendingItem.BuyerAppUserId,
            cancellationToken);

        await appLogService.WriteAsync(
            result.Success ? "Info" : "Warning",
            $"Automatic delivery attempt finished. MarketPurchaseId={pendingItem.MarketPurchaseId}; Success={result.Success}; Status={result.NewStatus}; OfferId={result.DeliveryTradeOfferId ?? "<null>"}; Message={result.Message}",
            nameof(SteamTradeAutomationService),
            cancellationToken: cancellationToken);
    }

    private async Task PersistWorkerFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var appLogService = scope.ServiceProvider.GetRequiredService<IAppLogService>();
        await appLogService.WriteAsync(
            "Error",
            "Steam trade automation worker iteration failed.",
            nameof(SteamTradeAutomationService),
            exception,
            cancellationToken);
    }

    private static async Task<BotHealthSnapshot> GetHealthAsync(string serviceUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var baseUri))
        {
            return new BotHealthSnapshot(false, false, "Service URL is invalid.");
        }

        var healthUri = new Uri(baseUri, "/healthz");
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(HealthCheckTimeout);

            using var client = new HttpClient();
            using var response = await client.GetAsync(healthUri, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new BotHealthSnapshot(false, false, $"HTTP {(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var ready = false;
            var enabled = false;
            string? lastError = null;
            if (root.TryGetProperty("bot", out var botElement) && botElement.ValueKind == JsonValueKind.Object)
            {
                if (botElement.TryGetProperty("ready", out var readyElement) &&
                    readyElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    ready = readyElement.GetBoolean();
                }

                if (botElement.TryGetProperty("enabled", out var enabledElement) &&
                    enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    enabled = enabledElement.GetBoolean();
                }

                if (botElement.TryGetProperty("lastError", out var lastErrorElement) &&
                    lastErrorElement.ValueKind == JsonValueKind.String)
                {
                    lastError = lastErrorElement.GetString();
                }
            }

            return new BotHealthSnapshot(true, enabled && ready, lastError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BotHealthSnapshot(false, false, "Health check timed out.");
        }
        catch (Exception exception)
        {
            return new BotHealthSnapshot(false, false, exception.Message);
        }
    }

    private sealed record PendingIntakeItem(Guid TradeOperationId, Guid AppUserId);
    private sealed record PendingDeliveryItem(Guid MarketPurchaseId, Guid BuyerAppUserId);
    private sealed record BotHealthSnapshot(bool Reachable, bool Ready, string? LastError);
}
