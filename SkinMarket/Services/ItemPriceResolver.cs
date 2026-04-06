using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class ItemPriceResolver : IItemPriceResolver
{
    private readonly AppDbContext _dbContext;
    private readonly ISteamMarketPriceService _steamMarketPriceService;
    private readonly ICsFloatPriceService _csFloatPriceService;
    private readonly ISkinportPricingService _skinportPricingService;
    private readonly IMemoryCache _memoryCache;
    private readonly PricingOptions _options;
    private readonly IGameCatalog _gameCatalog;
    private readonly ILogger<ItemPriceResolver> _logger;

    public ItemPriceResolver(
        AppDbContext dbContext,
        ISteamMarketPriceService steamMarketPriceService,
        ICsFloatPriceService csFloatPriceService,
        ISkinportPricingService skinportPricingService,
        IMemoryCache memoryCache,
        IOptions<PricingOptions> options,
        IGameCatalog gameCatalog,
        ILogger<ItemPriceResolver> logger)
    {
        _dbContext = dbContext;
        _steamMarketPriceService = steamMarketPriceService;
        _csFloatPriceService = csFloatPriceService;
        _skinportPricingService = skinportPricingService;
        _memoryCache = memoryCache;
        _options = options.Value;
        _gameCatalog = gameCatalog;
        _logger = logger;
    }

    public async Task<ItemPriceResolutionResult> ResolveAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        var marketHashName = MarketHashNameUtility.ResolvePrimary(item);
        return await ResolveInternalAsync(marketHashName, item.GameType, cancellationToken);
    }

    public async Task<ItemPriceResolutionResult> ResolveAsync(TradeOperation operation, CancellationToken cancellationToken = default)
    {
        var marketHashName = MarketHashNameUtility.ResolvePrimary(operation);
        return await ResolveInternalAsync(marketHashName, _gameCatalog.DefaultGameType, cancellationToken);
    }

    public async Task<ItemPriceResolutionResult> ResolveAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default)
    {
        return await ResolveInternalAsync(marketHashName, gameType, cancellationToken);
    }

    public async Task<ItemPriceResolutionResult> GetCachedAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default)
    {
        var results = await GetCachedAsync([marketHashName], gameType, cancellationToken);
        return results.GetValueOrDefault(MarketHashNameUtility.Normalize(marketHashName) ?? string.Empty) ?? CreateUnavailable("No cached price.", marketHashName);
    }

    public async Task<Dictionary<string, ItemPriceResolutionResult>> GetCachedAsync(
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

        var result = new Dictionary<string, ItemPriceResolutionResult>(StringComparer.Ordinal);
        if (normalizedNames.Count == 0)
        {
            return result;
        }

        var snapshots = await LoadSnapshotsAsync(normalizedNames, gameType, cancellationToken);
        foreach (var marketHashName in normalizedNames)
        {
            var memoryCacheKey = $"resolved-price::{(int)gameType}::{_options.PreferredCurrency}::{marketHashName}";
            if (_memoryCache.TryGetValue<ItemPriceResolutionResult>(memoryCacheKey, out var cached) && cached is not null)
            {
                result[marketHashName] = Clone(cached, isCachedOverride: true);
                continue;
            }

            if (snapshots.TryGetValue(marketHashName, out var snapshot))
            {
                var snapshotResult = FromSnapshot(snapshot);
                snapshotResult.NeedsRefresh = snapshot.ExpiresAtUtc < DateTime.UtcNow;
                result[marketHashName] = snapshotResult;
                continue;
            }

            result[marketHashName] = new ItemPriceResolutionResult
            {
                HasPrice = false,
                Currency = _options.PreferredCurrency,
                Source = "Unavailable",
                Status = "Unavailable",
                FailureReason = "No cached price.",
                ResolvedMarketHashName = marketHashName,
                NeedsRefresh = true
            };
        }

        return result;
    }

    public async Task<Dictionary<string, ItemPriceResolutionResult>> ResolveInventoryPricesAsync(
        IReadOnlyCollection<SteamInventoryItemDto> items,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ItemPriceResolutionResult>(StringComparer.Ordinal);
        var byHash = items
            .Select(item => MarketHashNameUtility.ResolvePrimary(item))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (byHash.Count == 0)
        {
            return result;
        }

        var snapshots = await LoadSnapshotsAsync(byHash, gameType, cancellationToken);
        var semaphore = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentPriceLookups));
        var tasks = byHash.Select(async marketHashName =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var resolved = await ResolveInternalAsync(marketHashName, gameType, cancellationToken, snapshots.GetValueOrDefault(marketHashName));
                lock (result)
                {
                    result[marketHashName] = resolved;
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return result;
    }

    private async Task<ItemPriceResolutionResult> ResolveInternalAsync(
        string? marketHashName,
        GameType gameType,
        CancellationToken cancellationToken,
        PriceSnapshot? preloadSnapshot = null)
    {
        var normalizedMarketHashName = MarketHashNameUtility.Normalize(marketHashName);
        if (string.IsNullOrWhiteSpace(normalizedMarketHashName))
        {
            return CreateUnavailable("MissingMarketHashName", null);
        }

        var memoryCacheKey = $"resolved-price::{(int)gameType}::{_options.PreferredCurrency}::{normalizedMarketHashName}";
        if (_memoryCache.TryGetValue<ItemPriceResolutionResult>(memoryCacheKey, out var cached) && cached is not null)
        {
            return Clone(cached, isCachedOverride: true);
        }

        var snapshot = preloadSnapshot ?? await LoadSnapshotAsync(normalizedMarketHashName, gameType, cancellationToken);
        if (snapshot is not null && snapshot.ExpiresAtUtc >= DateTime.UtcNow)
        {
            var snapshotResult = FromSnapshot(snapshot);
            _memoryCache.Set(memoryCacheKey, snapshotResult, TimeSpan.FromMinutes(_options.NegativeCacheMinutes));
            return Clone(snapshotResult, isCachedOverride: true);
        }

        PriceSourceResult? steamResult = null;
        PriceSourceResult? csFloatResult = null;
        PriceSourceResult? skinportResult = null;

        if (_options.EnableSteamSource)
        {
            steamResult = await _steamMarketPriceService.ProbePriceAsync(normalizedMarketHashName, gameType, cancellationToken);
            if (steamResult.Success && steamResult.Price.HasValue)
            {
                var resolved = CreateResolved(steamResult, normalizedMarketHashName, steamResult, null, null);
                await SaveSnapshotAsync(gameType, resolved, cancellationToken);
                _memoryCache.Set(memoryCacheKey, resolved, TimeSpan.FromMinutes(_options.SteamCacheMinutes));
                return resolved;
            }
        }

        if (_options.EnableCsFloatSource)
        {
            csFloatResult = await _csFloatPriceService.ProbePriceAsync(normalizedMarketHashName, gameType, cancellationToken);
            if (csFloatResult.Success && csFloatResult.Price.HasValue)
            {
                var resolved = CreateResolved(csFloatResult, normalizedMarketHashName, steamResult, csFloatResult, null);
                await SaveSnapshotAsync(gameType, resolved, cancellationToken);
                _memoryCache.Set(memoryCacheKey, resolved, TimeSpan.FromMinutes(_options.CsFloatCacheMinutes));
                return resolved;
            }
        }

        if (_options.EnableSkinportSource)
        {
            skinportResult = await _skinportPricingService.ProbePriceAsync(normalizedMarketHashName, gameType, cancellationToken);
            if (skinportResult.Success && skinportResult.Price.HasValue)
            {
                var resolved = CreateResolved(skinportResult, normalizedMarketHashName, steamResult, csFloatResult, skinportResult);
                await SaveSnapshotAsync(gameType, resolved, cancellationToken);
                _memoryCache.Set(memoryCacheKey, resolved, TimeSpan.FromMinutes(_options.SkinportHistoryCacheMinutes));
                return resolved;
            }
        }

        if (snapshot is not null && _options.AllowStaleSnapshotFallback && snapshot.HasPrice)
        {
            var staleResult = FromSnapshot(snapshot);
            staleResult.Status = "Cached";
            staleResult.IsCached = true;
            staleResult.FailureReason = BuildFailureReason(steamResult, csFloatResult, skinportResult) ?? staleResult.FailureReason;
            staleResult.SteamResult = steamResult;
            staleResult.CsFloatResult = csFloatResult;
            staleResult.SkinportResult = skinportResult;
            _memoryCache.Set(memoryCacheKey, staleResult, TimeSpan.FromMinutes(_options.NegativeCacheMinutes));
            return staleResult;
        }

        var unavailable = CreateUnavailable(BuildFailureReason(steamResult, csFloatResult, skinportResult), normalizedMarketHashName);
        unavailable.SteamResult = steamResult;
        unavailable.CsFloatResult = csFloatResult;
        unavailable.SkinportResult = skinportResult;

        await SaveSnapshotAsync(gameType, unavailable, cancellationToken);
        _memoryCache.Set(memoryCacheKey, unavailable, TimeSpan.FromMinutes(_options.NegativeCacheMinutes));
        return unavailable;
    }

    private async Task<Dictionary<string, PriceSnapshot>> LoadSnapshotsAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken)
    {
        var appId = _gameCatalog.Get(gameType).SteamAppId;
        var validFrom = DateTime.UtcNow.AddDays(-Math.Max(1, _options.StaleSnapshotDays));
        var snapshots = await _dbContext.PriceSnapshots
            .AsNoTracking()
            .Where(item =>
                item.AppId == appId &&
                item.Currency == _options.PreferredCurrency &&
                marketHashNames.Contains(item.MarketHashName) &&
                item.UpdatedAtUtc >= validFrom)
            .ToListAsync(cancellationToken);

        return snapshots.ToDictionary(item => item.MarketHashName, item => item, StringComparer.Ordinal);
    }

    private async Task<PriceSnapshot?> LoadSnapshotAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken)
    {
        var appId = _gameCatalog.Get(gameType).SteamAppId;
        var validFrom = DateTime.UtcNow.AddDays(-Math.Max(1, _options.StaleSnapshotDays));
        return await _dbContext.PriceSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.AppId == appId &&
                item.MarketHashName == marketHashName &&
                item.Currency == _options.PreferredCurrency &&
                item.UpdatedAtUtc >= validFrom,
                cancellationToken);
    }

    private async Task SaveSnapshotAsync(GameType gameType, ItemPriceResolutionResult result, CancellationToken cancellationToken)
    {
        var marketHashName = MarketHashNameUtility.Normalize(result.ResolvedMarketHashName);
        if (string.IsNullOrWhiteSpace(marketHashName))
        {
            return;
        }

        var appId = _gameCatalog.Get(gameType).SteamAppId;
        var existing = await _dbContext.PriceSnapshots
            .SingleOrDefaultAsync(item =>
                item.AppId == appId &&
                item.MarketHashName == marketHashName &&
                item.Currency == result.Currency,
                cancellationToken);

        var ttlMinutes = result.HasPrice
            ? Math.Max(_options.SnapshotCacheHours * 60, _options.SkinportItemsCacheMinutes)
            : _options.NegativeCacheMinutes;
        var updatedAtUtc = DateTime.UtcNow;
        if (existing is null)
        {
            existing = new PriceSnapshot
            {
                Id = Guid.NewGuid(),
                AppId = appId,
                MarketHashName = marketHashName
            };
            _dbContext.PriceSnapshots.Add(existing);
        }

        existing.Currency = result.Currency;
        existing.Source = result.Source;
        existing.Price = result.Price;
        existing.Status = result.Status;
        existing.HasPrice = result.HasPrice;
        existing.IsEstimated = result.IsEstimated;
        existing.FailureReason = result.FailureReason;
        existing.UpdatedAtUtc = updatedAtUtc;
        existing.ExpiresAtUtc = updatedAtUtc.AddMinutes(Math.Max(1, ttlMinutes));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private ItemPriceResolutionResult CreateResolved(
        PriceSourceResult sourceResult,
        string marketHashName,
        PriceSourceResult? steamResult,
        PriceSourceResult? csFloatResult,
        PriceSourceResult? skinportResult)
    {
        return new ItemPriceResolutionResult
        {
            HasPrice = true,
            Price = sourceResult.Price,
            Currency = sourceResult.Currency,
            Source = sourceResult.Source,
            Status = sourceResult.IsCached ? "Cached" : sourceResult.IsEstimated ? "Estimated" : "Live",
            IsCached = sourceResult.IsCached,
            IsEstimated = sourceResult.IsEstimated,
            LastUpdatedUtc = sourceResult.LastUpdatedUtc ?? DateTime.UtcNow,
            FailureReason = sourceResult.FailureReason,
            ResolvedMarketHashName = marketHashName,
            SteamResult = steamResult,
            CsFloatResult = csFloatResult,
            SkinportResult = skinportResult
        };
    }

    private static ItemPriceResolutionResult FromSnapshot(PriceSnapshot snapshot)
    {
        return new ItemPriceResolutionResult
        {
            HasPrice = snapshot.HasPrice && snapshot.Price.HasValue,
            Price = snapshot.Price,
            Currency = snapshot.Currency,
            Source = snapshot.Source,
            Status = snapshot.HasPrice ? "Cached" : snapshot.Status,
            IsCached = true,
            IsEstimated = snapshot.IsEstimated,
            LastUpdatedUtc = snapshot.UpdatedAtUtc,
            FailureReason = snapshot.FailureReason,
            ResolvedMarketHashName = snapshot.MarketHashName,
            NeedsRefresh = snapshot.ExpiresAtUtc < DateTime.UtcNow
        };
    }

    private static ItemPriceResolutionResult CreateUnavailable(string? failureReason, string? marketHashName)
    {
        return new ItemPriceResolutionResult
        {
            HasPrice = false,
            Currency = "USD",
            Source = "Unavailable",
            Status = "Unavailable",
            FailureReason = failureReason,
            ResolvedMarketHashName = marketHashName
        };
    }

    private static string? BuildFailureReason(params PriceSourceResult?[] results)
    {
        return results
            .Where(item => item is not null && !item.Success && !string.IsNullOrWhiteSpace(item.FailureReason))
            .Select(item => $"{item!.Source}: {item.FailureReason}")
            .FirstOrDefault();
    }

    private static ItemPriceResolutionResult Clone(ItemPriceResolutionResult source, bool isCachedOverride)
    {
        return new ItemPriceResolutionResult
        {
            HasPrice = source.HasPrice,
            Price = source.Price,
            Currency = source.Currency,
            Source = source.Source,
            Status = isCachedOverride && source.HasPrice && source.Status == "Live" ? "Cached" : source.Status,
            IsCached = isCachedOverride || source.IsCached,
            IsEstimated = source.IsEstimated,
            LastUpdatedUtc = source.LastUpdatedUtc,
            FailureReason = source.FailureReason,
            ResolvedMarketHashName = source.ResolvedMarketHashName,
            NeedsRefresh = source.NeedsRefresh,
            SteamResult = source.SteamResult,
            CsFloatResult = source.CsFloatResult,
            SkinportResult = source.SkinportResult
        };
    }
}
