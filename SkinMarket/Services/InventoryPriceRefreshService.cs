using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
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

    public InventoryPriceRefreshService(
        IServiceScopeFactory scopeFactory,
        IOptions<PricingOptions> options,
        ILogger<InventoryPriceRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var maxConcurrency = Math.Max(1, options.Value.MaxConcurrentPriceLookups);
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
        return OverlayTrackerStates(prices, marketHashNames, gameType);
    }

    public async Task QueueRefreshAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
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
                continue;
            }

            var key = BuildKey(gameType, marketHashName);
            var tracker = _trackers.GetOrAdd(key, _ => new PriceRefreshTracker());
            if (tracker.State is PriceRefreshState.Queued or PriceRefreshState.Refreshing)
            {
                continue;
            }

            tracker.State = PriceRefreshState.Queued;
            tracker.LastUpdatedUtc = DateTime.UtcNow;
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
        var runningTasks = new HashSet<Task>();
        await foreach (var workItem in _channel.Reader.ReadAllAsync(stoppingToken))
        {
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
    }

    private async Task ProcessWorkItemAsync(PriceRefreshWorkItem workItem, CancellationToken cancellationToken)
    {
        var key = BuildKey(workItem.GameType, workItem.MarketHashName);
        var tracker = _trackers.GetOrAdd(key, _ => new PriceRefreshTracker());
        tracker.State = PriceRefreshState.Refreshing;
        tracker.LastUpdatedUtc = DateTime.UtcNow;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IItemPriceResolver>();
            var result = await resolver.ResolveAsync(workItem.MarketHashName, workItem.GameType, cancellationToken);

            tracker.State = result.HasPrice ? PriceRefreshState.Completed : PriceRefreshState.Failed;
            tracker.LastUpdatedUtc = DateTime.UtcNow;
            tracker.FailureReason = result.FailureReason;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tracker.State = PriceRefreshState.Failed;
            tracker.LastUpdatedUtc = DateTime.UtcNow;
            tracker.FailureReason = "Cancelled";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Inventory price refresh failed for {MarketHashName}.", workItem.MarketHashName);
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

            if (!result.HasPrice && tracker.State is PriceRefreshState.Queued or PriceRefreshState.Refreshing)
            {
                result.Status = "Refreshing";
            }
            else if (result.HasPrice && tracker.State == PriceRefreshState.Completed && tracker.LastUpdatedUtc >= DateTime.UtcNow.AddMinutes(-1))
            {
                result.Status = result.IsEstimated ? "Estimated" : "Live";
                result.IsCached = false;
                result.NeedsRefresh = false;
            }
            else if (tracker.State == PriceRefreshState.Failed && string.IsNullOrWhiteSpace(result.FailureReason))
            {
                result.FailureReason = tracker.FailureReason;
            }
        }

        return prices;
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
