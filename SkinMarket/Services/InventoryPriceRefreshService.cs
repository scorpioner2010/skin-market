using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class InventoryPriceRefreshService : BackgroundService, IInventoryPriceRefreshService
{
    private readonly Channel<PriceRefreshWorkItem> _channel = Channel.CreateUnbounded<PriceRefreshWorkItem>();
    private readonly ConcurrentDictionary<string, PriceRefreshTracker> _trackers = new(StringComparer.Ordinal);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InventoryPriceRefreshService> _logger;
    private readonly SemaphoreSlim _workerSemaphore;
    private readonly PricingOptions _options;
    private int _pendingCount;

    public InventoryPriceRefreshService(
        IServiceScopeFactory scopeFactory,
        IOptions<PricingOptions> options,
        ILogger<InventoryPriceRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        var maxConcurrency = Math.Max(1, _options.MaxConcurrentPriceLookups);
        _workerSemaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public async Task<Dictionary<string, ItemPriceResolutionResult>> GetCurrentPricesAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IItemPriceResolver>();
        var prices = await resolver.GetCachedAsync(marketHashNames, gameType, cancellationToken);
        var withTrackerStates = OverlayTrackerStates(prices, marketHashNames, gameType);
        _logger.LogDebug(
            "Read current inventory price statuses for {Count} items in {GameType}. Refreshing={RefreshingCount}",
            marketHashNames.Count,
            gameType,
            withTrackerStates.Values.Count(item => item.Status == "Refreshing"));
        return withTrackerStates;
    }

    public async Task QueueRefreshAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
        foreach (var originalName in marketHashNames)
        {
            var normalizedName = MarketHashNameUtility.Normalize(originalName);
            if (!string.IsNullOrWhiteSpace(normalizedName) &&
                !string.Equals(originalName, normalizedName, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Inventory price refresh normalized market hash name from {OriginalMarketHashName} to {NormalizedMarketHashName}.",
                    originalName,
                    normalizedName);
                await PersistAppLogAsync(
                    "Info",
                    $"Queue normalize. Before={originalName}; After={normalizedName}",
                    nameof(InventoryPriceRefreshService),
                    null,
                    cancellationToken);
            }
        }

        var normalizedNames = marketHashNames
            .Select(MarketHashNameUtility.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalizedNames.Count == 0)
        {
            return;
        }

        var current = await GetCurrentPricesAsync(normalizedNames, gameType, cancellationToken);
        foreach (var marketHashName in normalizedNames)
        {
            if (!current.TryGetValue(marketHashName, out var price) || !price.NeedsRefresh)
            {
                _logger.LogDebug(
                    "Skipping inventory price queue for {GameType} / {MarketHashName} because refresh is not needed.",
                    gameType,
                    marketHashName);
                await PersistAppLogAsync(
                    "Info",
                    $"Queue skipped. GameType={(int)gameType}; MarketHashName={marketHashName}; Reason=RefreshNotNeeded",
                    nameof(InventoryPriceRefreshService),
                    null,
                    CancellationToken.None);
                continue;
            }

            var key = BuildKey(gameType, marketHashName);
            var trackerCreated = false;
            var tracker = _trackers.GetOrAdd(key, _ =>
            {
                trackerCreated = true;
                return new PriceRefreshTracker();
            });
            if (trackerCreated)
            {
                await PersistAppLogAsync(
                    "Info",
                    $"Tracker created. GameType={(int)gameType}; MarketHashName={marketHashName}",
                    nameof(InventoryPriceRefreshService),
                    null,
                    CancellationToken.None);
            }

            if (TryResetStaleTracker(tracker, gameType, marketHashName, out var staleResetMessage))
            {
                await PersistAppLogAsync(
                    "Info",
                    staleResetMessage,
                    nameof(InventoryPriceRefreshService),
                    null,
                    CancellationToken.None);
            }

            if (tracker.State is PriceRefreshState.Queued or PriceRefreshState.Refreshing)
            {
                _logger.LogDebug(
                    "Inventory price refresh already in progress for {GameType} / {MarketHashName}.",
                    gameType,
                    marketHashName);
                await PersistAppLogAsync(
                    "Info",
                    $"Queue skipped. GameType={(int)gameType}; MarketHashName={marketHashName}; Reason=AlreadyInProgress; TrackerState={tracker.State}",
                    nameof(InventoryPriceRefreshService),
                    null,
                    CancellationToken.None);
                continue;
            }

            tracker.State = PriceRefreshState.Queued;
            tracker.LastUpdatedUtc = DateTime.UtcNow;
            tracker.FailureReason = null;
            var pendingAfterEnqueue = Interlocked.Increment(ref _pendingCount);
            _logger.LogInformation(
                "Queued inventory price refresh for {GameType} / {MarketHashName}.",
                gameType,
                marketHashName);
            await PersistAppLogAsync(
                "Info",
                $"Queued item. GameType={(int)gameType}; MarketHashName={marketHashName}; CurrentStatus={price.Status}; CurrentFailure={price.FailureReason}; Pending={pendingAfterEnqueue}",
                nameof(InventoryPriceRefreshService),
                null,
                CancellationToken.None);
            await _channel.Writer.WriteAsync(new PriceRefreshWorkItem(gameType, marketHashName), cancellationToken);
        }
    }

    public async Task<Dictionary<string, ItemPriceResolutionResult>> GetStatusAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
        return await GetCurrentPricesAsync(marketHashNames, gameType, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Inventory price refresh worker started. MaxConcurrency={MaxConcurrency} RefreshTimeoutSeconds={RefreshTimeoutSeconds}",
            _options.MaxConcurrentPriceLookups,
            _options.RefreshTimeoutSeconds);
        await PersistAppLogAsync(
            "Info",
            $"Worker started. MaxConcurrency={_options.MaxConcurrentPriceLookups}; RefreshTimeoutSeconds={_options.RefreshTimeoutSeconds}; RefreshingStateTimeoutSeconds={_options.RefreshingStateTimeoutSeconds}",
            nameof(InventoryPriceRefreshService),
            null,
            CancellationToken.None);

        _ = RunHeartbeatAsync(stoppingToken);

        var runningTasks = new HashSet<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var hasItems = await _channel.Reader.WaitToReadAsync(stoppingToken);
                if (!hasItems)
                {
                    await PersistAppLogAsync(
                        "Info",
                        "Worker wait ended. Queue closed.",
                        nameof(InventoryPriceRefreshService),
                        null,
                        CancellationToken.None);
                    break;
                }

                while (_channel.Reader.TryRead(out var workItem))
                {
                    var pendingAfterDequeue = Math.Max(0, Interlocked.Decrement(ref _pendingCount));
                    await PersistAppLogAsync(
                        "Info",
                        $"Worker dequeued item. GameType={(int)workItem.GameType}; MarketHashName={workItem.MarketHashName}; Pending={pendingAfterDequeue}",
                        nameof(InventoryPriceRefreshService),
                        null,
                        CancellationToken.None);

                    await _workerSemaphore.WaitAsync(stoppingToken);
                    var task = ProcessWorkItemAsync(workItem, stoppingToken).ContinueWith(_ =>
                    {
                        _workerSemaphore.Release();
                    }, TaskScheduler.Default);

                    lock (runningTasks)
                    {
                        runningTasks.Add(task);
                    }

                    _ = task.ContinueWith(completedTask =>
                    {
                        lock (runningTasks)
                        {
                            runningTasks.Remove(completedTask);
                        }
                    }, TaskScheduler.Default);
                }

                if (Volatile.Read(ref _pendingCount) == 0)
                {
                    await PersistAppLogAsync(
                        "Info",
                        "Worker queue empty.",
                        nameof(InventoryPriceRefreshService),
                        null,
                        CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await PersistAppLogAsync(
                "Info",
                "Worker stopping.",
                nameof(InventoryPriceRefreshService),
                null,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await PersistAppLogAsync(
                "Error",
                $"Worker loop crashed. ExceptionType={exception.GetType().Name}; Message={exception.Message}",
                nameof(InventoryPriceRefreshService),
                exception,
                CancellationToken.None);
            throw;
        }
    }

    private async Task ProcessWorkItemAsync(PriceRefreshWorkItem workItem, CancellationToken cancellationToken)
    {
        var key = BuildKey(workItem.GameType, workItem.MarketHashName);
        var tracker = _trackers.GetOrAdd(key, _ => new PriceRefreshTracker());
        tracker.State = PriceRefreshState.Refreshing;
        tracker.LastUpdatedUtc = DateTime.UtcNow;
        tracker.FailureReason = null;

        _logger.LogInformation(
            "Starting inventory price refresh for {GameType} / {MarketHashName}.",
            workItem.GameType,
            workItem.MarketHashName);
            await PersistAppLogAsync(
                "Info",
                $"Worker start. GameType={(int)workItem.GameType}; MarketHashName={workItem.MarketHashName}",
                nameof(InventoryPriceRefreshService),
                null,
                CancellationToken.None);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.RefreshTimeoutSeconds)));

            await using var scope = _scopeFactory.CreateAsyncScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IItemPriceResolver>();
            var result = await resolver.ResolveAsync(workItem.MarketHashName, workItem.GameType, timeoutCts.Token);

            tracker.State = result.HasPrice ? PriceRefreshState.Completed : PriceRefreshState.Failed;
            tracker.LastUpdatedUtc = DateTime.UtcNow;
            tracker.FailureReason = result.FailureReason;
            _logger.LogInformation(
                "Finished inventory price refresh for {GameType} / {MarketHashName}. HasPrice={HasPrice} Status={Status} Source={Source} Failure={FailureReason}",
                workItem.GameType,
                workItem.MarketHashName,
                result.HasPrice,
                result.Status,
                result.Source,
                result.FailureReason);
            await PersistAppLogAsync(
                result.HasPrice ? "Info" : "Warning",
                $"Worker done. GameType={(int)workItem.GameType}; MarketHashName={workItem.MarketHashName}; HasPrice={result.HasPrice}; Status={result.Status}; Source={result.Source}; PriceType={result.PriceType}; PriceUsd={result.Price?.ToString() ?? "<null>"}; Estimated={result.IsEstimated}; Cached={result.IsCached}; Stale={result.IsStale}; Confidence={result.ConfidenceScore}; Failure={result.FailureReason}",
                nameof(InventoryPriceRefreshService),
                null,
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tracker.State = PriceRefreshState.Failed;
            tracker.LastUpdatedUtc = DateTime.UtcNow;
            tracker.FailureReason = "Cancelled";
            _logger.LogWarning(
                "Inventory price refresh cancelled because the worker is stopping for {GameType} / {MarketHashName}.",
                workItem.GameType,
                workItem.MarketHashName);
        }
        catch (OperationCanceledException)
        {
            const string failureReason = "Price refresh timed out.";
            await PersistFailureSnapshotAsync(workItem, failureReason, cancellationToken);
            await PersistAppLogAsync("Warning", failureReason, nameof(InventoryPriceRefreshService), null, cancellationToken);
            tracker.State = PriceRefreshState.Failed;
            tracker.LastUpdatedUtc = DateTime.UtcNow;
            tracker.FailureReason = failureReason;
            _logger.LogWarning(
                "Inventory price refresh timed out for {GameType} / {MarketHashName}.",
                workItem.GameType,
                workItem.MarketHashName);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Inventory price refresh failed for {MarketHashName}.", workItem.MarketHashName);
            await PersistFailureSnapshotAsync(workItem, exception.Message, cancellationToken);
            await PersistAppLogAsync("Error", exception.Message, nameof(InventoryPriceRefreshService), exception, cancellationToken);
            tracker.State = PriceRefreshState.Failed;
            tracker.LastUpdatedUtc = DateTime.UtcNow;
            tracker.FailureReason = exception.Message;
        }
    }

    private Dictionary<string, ItemPriceResolutionResult> OverlayTrackerStates(
        Dictionary<string, ItemPriceResolutionResult> prices,
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType)
    {
        foreach (var marketHashName in marketHashNames)
        {
            var normalizedName = MarketHashNameUtility.Normalize(marketHashName);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                continue;
            }

            if (!prices.TryGetValue(normalizedName, out var result))
            {
                result = new ItemPriceResolutionResult
                {
                    Currency = "USD",
                    Source = "Unavailable",
                    Status = "Unavailable",
                    FailureReason = "No cached price.",
                    ResolvedMarketHashName = normalizedName,
                    NeedsRefresh = true
                };
                prices[normalizedName] = result;
            }

            var trackerKey = BuildKey(gameType, normalizedName);
            if (!_trackers.TryGetValue(trackerKey, out var tracker))
            {
                continue;
            }

            if (TryResetStaleTracker(tracker, gameType, normalizedName, out var _))
            {
            }

            if (!result.HasPrice && tracker.State is PriceRefreshState.Queued or PriceRefreshState.Refreshing)
            {
                result.Status = "Refreshing";
            }
            else if (result.HasPrice &&
                     !result.NeedsRefresh &&
                     tracker.State == PriceRefreshState.Completed &&
                     tracker.LastUpdatedUtc >= DateTime.UtcNow.AddMinutes(-1))
            {
                result.Status = result.IsEstimated ? "Estimated" : "Live";
                result.IsCached = false;
                result.NeedsRefresh = false;
            }
            else if (tracker.State == PriceRefreshState.Failed)
            {
                result.FailureReason = tracker.FailureReason ?? result.FailureReason;
                result.Status = "Unavailable";
                result.NeedsRefresh = IsRetryableFailure(result.FailureReason);
            }
        }

        return prices;
    }

    private async Task PersistFailureSnapshotAsync(
        PriceRefreshWorkItem workItem,
        string failureReason,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var gameCatalog = scope.ServiceProvider.GetRequiredService<IGameCatalog>();
            var appId = gameCatalog.Get(workItem.GameType).SteamAppId;
            var marketHashName = MarketHashNameUtility.Normalize(workItem.MarketHashName);
            if (string.IsNullOrWhiteSpace(marketHashName))
            {
                return;
            }

            var existing = await dbContext.PriceSnapshots.SingleOrDefaultAsync(
                item => item.AppId == appId &&
                        item.MarketHashName == marketHashName &&
                        item.Currency == _options.PreferredCurrency &&
                        item.Source == PriceSourceNames.Unavailable &&
                        item.PriceType == PriceTypeNames.Unavailable,
                cancellationToken);

            var updatedAtUtc = DateTime.UtcNow;
            if (existing is null)
            {
                existing = new PriceSnapshot
                {
                    Id = Guid.NewGuid(),
                    AppId = appId,
                    MarketHashName = marketHashName
                };
                dbContext.PriceSnapshots.Add(existing);
            }

            existing.Currency = _options.PreferredCurrency;
            existing.Source = PriceSourceNames.Unavailable;
            existing.PriceType = PriceTypeNames.Unavailable;
            existing.Price = null;
            existing.PriceUsd = null;
            existing.Status = "Unavailable";
            existing.HasPrice = false;
            existing.IsEstimated = false;
            existing.ConfidenceScore = 0m;
            existing.ObservedAtUtc = updatedAtUtc;
            existing.FailureReason = failureReason;
            existing.UpdatedAtUtc = updatedAtUtc;
            existing.TtlSeconds = Math.Max(1, _options.NegativeCacheMinutes) * 60;
            existing.ExpiresAtUtc = updatedAtUtc.AddSeconds(existing.TtlSeconds);

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Saved terminal failure snapshot for {GameType} / {MarketHashName}. Failure={FailureReason}",
                workItem.GameType,
                marketHashName,
                failureReason);
            await PersistAppLogAsync(
                IsRetryableFailure(failureReason) ? "Warning" : "Info",
                $"Final failure snapshot written. GameType={(int)workItem.GameType}; MarketHashName={marketHashName}; Failure={failureReason}; Retryable={IsRetryableFailure(failureReason)}",
                nameof(InventoryPriceRefreshService),
                null,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to persist terminal failure snapshot for {GameType} / {MarketHashName}.",
                workItem.GameType,
                workItem.MarketHashName);
        }
    }

    private async Task PersistAppLogAsync(
        string level,
        string message,
        string source,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var appLogService = scope.ServiceProvider.GetRequiredService<IAppLogService>();
            await appLogService.WriteAsync(level, message, source, exception, cancellationToken);
        }
        catch (Exception logException)
        {
            _logger.LogError(logException, "Failed to persist background log for {Source}.", source);
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PersistAppLogAsync(
                    "Info",
                    $"Worker heartbeat. Pending={Volatile.Read(ref _pendingCount)}; Trackers={_trackers.Count}; ActiveWorkers={Math.Max(0, _options.MaxConcurrentPriceLookups - _workerSemaphore.CurrentCount)}",
                    nameof(InventoryPriceRefreshService),
                    null,
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private bool TryResetStaleTracker(PriceRefreshTracker tracker, GameType gameType, string marketHashName, out string message)
    {
        message = string.Empty;
        if (tracker.State is not (PriceRefreshState.Queued or PriceRefreshState.Refreshing))
        {
            return false;
        }

        if (tracker.LastUpdatedUtc > DateTime.UtcNow.AddSeconds(-Math.Max(5, _options.RefreshingStateTimeoutSeconds)))
        {
            return false;
        }

        tracker.State = PriceRefreshState.Failed;
        tracker.LastUpdatedUtc = DateTime.UtcNow;
        tracker.FailureReason ??= "Price refresh timed out.";
        message = $"Tracker stale reset. GameType={(int)gameType}; MarketHashName={marketHashName}; NewState={tracker.State}; Failure={tracker.FailureReason}";
        _logger.LogWarning(
            "Inventory price tracker expired for {GameType} / {MarketHashName}. Marking as failed.",
            gameType,
            marketHashName);
        return true;
    }

    private static bool IsRetryableFailure(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
        {
            return false;
        }

        return failureReason.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               failureReason.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               failureReason.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
               failureReason.Contains("transient", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildKey(GameType gameType, string marketHashName)
    {
        return $"{(int)gameType}::{marketHashName}";
    }

    private sealed record PriceRefreshWorkItem(GameType GameType, string MarketHashName);

    private sealed class PriceRefreshTracker
    {
        public PriceRefreshState State { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
        public string? FailureReason { get; set; }
    }

    private enum PriceRefreshState
    {
        Queued,
        Refreshing,
        Completed,
        Failed
    }
}
