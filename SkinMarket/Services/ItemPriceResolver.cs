using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Text.Json;
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
    private readonly IDMarketPricingService _dMarketPricingService;
    private readonly IFxRateService _fxRateService;
    private readonly IMemoryCache _memoryCache;
    private readonly PricingOptions _options;
    private readonly IGameCatalog _gameCatalog;
    private readonly ILogger<ItemPriceResolver> _logger;
    private readonly IAppLogService _appLogService;
    private readonly IPriceDiagnosticLogService _priceDiagnosticLogService;

    public ItemPriceResolver(
        AppDbContext dbContext,
        ISteamMarketPriceService steamMarketPriceService,
        ICsFloatPriceService csFloatPriceService,
        ISkinportPricingService skinportPricingService,
        IDMarketPricingService dMarketPricingService,
        IFxRateService fxRateService,
        IMemoryCache memoryCache,
        IOptions<PricingOptions> options,
        IGameCatalog gameCatalog,
        ILogger<ItemPriceResolver> logger,
        IAppLogService appLogService,
        IPriceDiagnosticLogService priceDiagnosticLogService)
    {
        _dbContext = dbContext;
        _steamMarketPriceService = steamMarketPriceService;
        _csFloatPriceService = csFloatPriceService;
        _skinportPricingService = skinportPricingService;
        _dMarketPricingService = dMarketPricingService;
        _fxRateService = fxRateService;
        _memoryCache = memoryCache;
        _options = options.Value;
        _gameCatalog = gameCatalog;
        _logger = logger;
        _appLogService = appLogService;
        _priceDiagnosticLogService = priceDiagnosticLogService;
    }

    public async Task<ItemPriceResolutionResult> ResolveAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        var marketHashName = MarketHashNameUtility.ResolvePrimary(item);
        return await ResolveInternalAsync(marketHashName, item.GameType, true, cancellationToken);
    }

    public async Task<ItemPriceResolutionResult> ResolveAsync(TradeOperation operation, CancellationToken cancellationToken = default)
    {
        var marketHashName = MarketHashNameUtility.ResolvePrimary(operation);
        var gameType = _gameCatalog.SupportedGames
            .FirstOrDefault(game =>
                game.SteamAppId == operation.AppId &&
                game.SteamContextId.ToString() == operation.ContextId)
            ?.Type ?? _gameCatalog.DefaultGameType;
        return await ResolveInternalAsync(marketHashName, gameType, true, cancellationToken);
    }

    public async Task<ItemPriceResolutionResult> ResolveAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default)
    {
        return await ResolveInternalAsync(marketHashName, gameType, true, cancellationToken);
    }

    public async Task<ItemPriceResolutionResult> GetCachedAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default)
    {
        var results = await GetCachedAsync([marketHashName], gameType, cancellationToken);
        return results.GetValueOrDefault(MarketHashNameUtility.Normalize(marketHashName) ?? string.Empty)
               ?? CreateUnavailable("No cached price.", marketHashName);
    }

    public async Task<Dictionary<string, ItemPriceResolutionResult>> GetCachedAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
        var normalizedNames = NormalizeNames(marketHashNames);
        var result = new Dictionary<string, ItemPriceResolutionResult>(StringComparer.Ordinal);
        if (normalizedNames.Count == 0)
        {
            return result;
        }

        var snapshots = await LoadSnapshotsAsync(normalizedNames, gameType, cancellationToken);
        foreach (var marketHashName in normalizedNames)
        {
            var snapshotsForName = snapshots.GetValueOrDefault(marketHashName) ?? [];
            var selected = SelectSelectedSnapshot(snapshotsForName, marketHashName);
            if (selected is not null)
            {
                selected.NeedsRefresh = selected.IsStale || selected.ExpiresAtUtc <= DateTime.UtcNow;
                result[marketHashName] = selected;
                continue;
            }

            var sourceFallback = SelectBestSnapshot(snapshotsForName, marketHashName);
            if (sourceFallback is not null)
            {
                var persistedSelection = await SaveSelectedSnapshotAsync(gameType, sourceFallback, cancellationToken);
                persistedSelection.NeedsRefresh = persistedSelection.IsStale || persistedSelection.ExpiresAtUtc <= DateTime.UtcNow;
                result[marketHashName] = persistedSelection;
                continue;
            }

            var unavailable = CreateUnavailable("No cached price.", marketHashName);
            unavailable.NeedsRefresh = true;
            result[marketHashName] = unavailable;
        }

        return result;
    }

    public async Task<Dictionary<string, ItemPriceResolutionResult>> ResolveInventoryPricesAsync(
        IReadOnlyCollection<SteamInventoryItemDto> items,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ItemPriceResolutionResult>(StringComparer.Ordinal);
        var byHash = NormalizeNames(items.Select(MarketHashNameUtility.ResolvePrimary).Where(name => name is not null).Cast<string>().ToList());
        if (byHash.Count == 0)
        {
            return result;
        }

        var semaphore = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentPriceLookups));
        var tasks = byHash.Select(async marketHashName =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var resolved = await ResolveInternalAsync(marketHashName, gameType, true, cancellationToken);
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
        bool probeSources,
        CancellationToken cancellationToken)
    {
        var normalizedMarketHashName = MarketHashNameUtility.Normalize(marketHashName);
        if (string.IsNullOrWhiteSpace(normalizedMarketHashName))
        {
            await LogVerboseAppAsync("Warning", "Missing market hash name.", nameof(ItemPriceResolver), cancellationToken: cancellationToken);
            return CreateUnavailable("MissingMarketHashName", null);
        }

        var game = _gameCatalog.Get(gameType);
        var existingSnapshots = await LoadSnapshotsForNameAsync(normalizedMarketHashName, gameType, cancellationToken);
        var memoryCacheKey = BuildMemoryCacheKey(gameType, normalizedMarketHashName);
        var selectedSnapshot = SelectSelectedSnapshot(existingSnapshots, normalizedMarketHashName);
        if (selectedSnapshot is { IsStale: false } &&
            selectedSnapshot.ExpiresAtUtc > DateTime.UtcNow)
        {
            selectedSnapshot.NeedsRefresh = false;
            _memoryCache.Set(memoryCacheKey, selectedSnapshot, GetMemoryDuration(selectedSnapshot));
            return Clone(selectedSnapshot, true);
        }

        if (_memoryCache.TryGetValue<ItemPriceResolutionResult>(memoryCacheKey, out var cached) &&
            cached is not null &&
            !cached.NeedsRefresh &&
            selectedSnapshot is not null &&
            cached.ExpiresAtUtc == selectedSnapshot.ExpiresAtUtc)
        {
            return Clone(cached, true);
        }

        if (!probeSources)
        {
            if (selectedSnapshot is not null)
            {
                selectedSnapshot.NeedsRefresh = selectedSnapshot.IsStale || selectedSnapshot.ExpiresAtUtc <= DateTime.UtcNow;
                return selectedSnapshot;
            }

            var sourceFallback = SelectBestSnapshot(existingSnapshots, normalizedMarketHashName);
            if (sourceFallback is not null)
            {
                var persistedSelection = await SaveSelectedSnapshotAsync(gameType, sourceFallback, cancellationToken);
                persistedSelection.NeedsRefresh = persistedSelection.IsStale || persistedSelection.ExpiresAtUtc <= DateTime.UtcNow;
                return persistedSelection;
            }

            var unavailableCached = CreateUnavailable("No cached price.", normalizedMarketHashName);
            unavailableCached.NeedsRefresh = true;
            return unavailableCached;
        }

        await _priceDiagnosticLogService.LogResolveStartedAsync(
            gameType,
            game.SteamAppId,
            normalizedMarketHashName,
            existingSnapshots.Count,
            cancellationToken);
        await LogVerboseAppAsync(
            "Info",
            $"Resolve start. GameType={(int)gameType}; MarketHashName={normalizedMarketHashName}; Steam={_options.EnableSteamSource}; CSFloat={_options.EnableCsFloatSource}; Skinport={_options.EnableSkinportSource}; DMarket={_options.EnableDMarketSource}; SnapshotCount={existingSnapshots.Count}",
            nameof(ItemPriceResolver),
            cancellationToken: cancellationToken);

        var sourceResults = new List<PriceSourceResult>();
        PriceSourceResult? skinportResult = null;
        PriceSourceResult? dMarketResult = null;
        PriceSourceResult? steamResult = null;
        PriceSourceResult? csFloatResult = null;

        if (_options.EnableSkinportSource)
        {
            skinportResult = await ProbeAndNormalizeAsync(
                () => _skinportPricingService.ProbePriceAsync(normalizedMarketHashName, gameType, cancellationToken),
                PriceSourceNames.Skinport,
                normalizedMarketHashName,
                gameType,
                game.SteamAppId,
                cancellationToken);
            sourceResults.Add(skinportResult);
            await SaveSnapshotAsync(gameType, skinportResult, cancellationToken);
        }

        if (_options.EnableDMarketSource)
        {
            dMarketResult = await ProbeAndNormalizeAsync(
                () => _dMarketPricingService.ProbePriceAsync(normalizedMarketHashName, gameType, cancellationToken),
                PriceSourceNames.DMarket,
                normalizedMarketHashName,
                gameType,
                game.SteamAppId,
                cancellationToken);
            sourceResults.Add(dMarketResult);
            await SaveSnapshotAsync(gameType, dMarketResult, cancellationToken);
        }

        if (_options.EnableSteamSource)
        {
            steamResult = await ProbeAndNormalizeAsync(
                () => _steamMarketPriceService.ProbePriceAsync(normalizedMarketHashName, gameType, cancellationToken),
                PriceSourceNames.Steam,
                normalizedMarketHashName,
                gameType,
                game.SteamAppId,
                cancellationToken);
            sourceResults.Add(steamResult);
            await SaveSnapshotAsync(gameType, steamResult, cancellationToken);
        }

        if (_options.EnableCsFloatSource && game.SteamAppId == 730)
        {
            csFloatResult = await ProbeAndNormalizeAsync(
                () => _csFloatPriceService.ProbePriceAsync(normalizedMarketHashName, gameType, cancellationToken),
                PriceSourceNames.CSFloat,
                normalizedMarketHashName,
                gameType,
                game.SteamAppId,
                cancellationToken);
            sourceResults.Add(csFloatResult);
            await SaveSnapshotAsync(gameType, csFloatResult, cancellationToken);
        }

        var refreshedSnapshots = await LoadSnapshotsForNameAsync(normalizedMarketHashName, gameType, cancellationToken);
        var selectedCandidate = SelectBestSourceResult(sourceResults, normalizedMarketHashName)
                                ?? SelectBestSnapshot(refreshedSnapshots, normalizedMarketHashName, freshOnly: true);
        var selected = selectedCandidate is not null
            ? await SaveSelectedSnapshotAsync(gameType, selectedCandidate, cancellationToken)
            : selectedSnapshot
              ?? SelectBestSnapshot(refreshedSnapshots, normalizedMarketHashName)
              ?? CreateUnavailable(BuildFailureReason(sourceResults), normalizedMarketHashName);

        selected.SteamResult = steamResult;
        selected.CsFloatResult = csFloatResult;
        selected.SkinportResult = skinportResult;
        selected.DMarketResult = dMarketResult;
        selected.NeedsRefresh = !selected.HasPrice || selected.IsStale || selected.ExpiresAtUtc <= DateTime.UtcNow;

        if (!selected.HasPrice)
        {
            await SaveSnapshotAsync(gameType, ToSourceResult(selected), cancellationToken);
            await LogFinalResolutionFailedAsync(
                game,
                normalizedMarketHashName,
                selected.FailureReason ?? "No reliable price.",
                sourceResults,
                existingSnapshots,
                cancellationToken);
        }

        _memoryCache.Set(memoryCacheKey, selected, GetMemoryDuration(selected));
        await _priceDiagnosticLogService.LogFinalSelectionAsync(
            gameType,
            game.SteamAppId,
            normalizedMarketHashName,
            selected,
            cancellationToken);
        await LogVerboseAppAsync(
            selected.HasPrice ? "Info" : "Warning",
            $"Resolved final. GameType={(int)gameType}; MarketHashName={normalizedMarketHashName}; HasPrice={selected.HasPrice}; Source={selected.Source}; PriceType={selected.PriceType}; PriceUsd={selected.Price?.ToString() ?? "<null>"}; Estimated={selected.IsEstimated}; Cached={selected.IsCached}; Stale={selected.IsStale}; Confidence={selected.ConfidenceScore}; Failure={selected.FailureReason}",
            nameof(ItemPriceResolver),
            cancellationToken: cancellationToken);

        return selected;
    }

    private async Task<PriceSourceResult> ProbeAndNormalizeAsync(
        Func<Task<PriceSourceResult>> probe,
        string source,
        string marketHashName,
        GameType gameType,
        int appId,
        CancellationToken cancellationToken)
    {
        var result = await probe();
        result.Source = string.IsNullOrWhiteSpace(result.Source) ? source : result.Source;
        result.ResolvedMarketHashName ??= marketHashName;
        result.Currency = string.IsNullOrWhiteSpace(result.Currency) ? _options.PreferredCurrency : result.Currency;
        result.PriceType = string.IsNullOrWhiteSpace(result.PriceType) ? PriceTypeNames.Unavailable : result.PriceType;
        result.ObservedAtUtc ??= result.LastUpdatedUtc ?? DateTime.UtcNow;
        result.LastUpdatedUtc ??= DateTime.UtcNow;
        result.TtlSeconds ??= GetFreshSnapshotSeconds(result);
        result.ExpiresAtUtc ??= DateTime.UtcNow.AddSeconds(Math.Max(60, result.TtlSeconds.Value));

        if (result.Success && result.Price.HasValue && !string.Equals(result.Currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            var fx = await _fxRateService.NormalizeToUsdAsync(result.Price.Value, result.Currency, cancellationToken);
            if (!fx.Success || !fx.PriceUsd.HasValue)
            {
                await LogRejectedSourceAsync(result, marketHashName, fx.FailureReason ?? "Non-USD price rejected.", cancellationToken);
                return new PriceSourceResult
                {
                    Source = result.Source,
                    Status = "Unavailable",
                    PriceType = PriceTypeNames.Unavailable,
                    Currency = "USD",
                    FailureReason = fx.FailureReason ?? "Non-USD price rejected.",
                    ResolvedMarketHashName = marketHashName,
                    LastUpdatedUtc = DateTime.UtcNow
                };
            }

            result.OriginalPrice = result.Price;
            result.OriginalCurrency = result.Currency;
            result.Price = Math.Round(fx.PriceUsd.Value, 2, MidpointRounding.AwayFromZero);
            result.Currency = "USD";
            result.FxRate = fx.FxRate;
        }

        if (result.Success && result.Price.HasValue && result.Price <= 0)
        {
            result.Success = false;
            result.FailureReason = "Source returned a non-positive price.";
            result.Price = null;
            result.PriceType = PriceTypeNames.Unavailable;
        }

        LogSourceResult(result);
        await _priceDiagnosticLogService.LogSourceResultAsync(
            gameType,
            appId,
            marketHashName,
            result,
            cancellationToken: cancellationToken);
        return result;
    }

    private ItemPriceResolutionResult? SelectBestSourceResult(IReadOnlyCollection<PriceSourceResult> sourceResults, string marketHashName)
    {
        var candidates = sourceResults
            .Where(item => item.Success && item.Price.HasValue && string.Equals(item.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            .Select(item => FromSourceResult(item, false))
            .Where(item => item.ConfidenceScore > 0)
            .OrderBy(item => GetSelectionRank(item))
            .ThenByDescending(item => item.ConfidenceScore)
            .ThenBy(item => item.Price)
            .ToList();

        var selected = candidates.FirstOrDefault();
        foreach (var rejected in candidates.Skip(1))
        {
            _logger.LogInformation(
                "Price source rejected for {MarketHashName}. Source={Source} PriceType={PriceType} Confidence={Confidence} Reason=LowerPriorityThanSelected Selected={SelectedSource}/{SelectedPriceType}",
                marketHashName,
                rejected.Source,
                rejected.PriceType,
                rejected.ConfidenceScore,
                selected?.Source,
                selected?.PriceType);
        }

        return selected;
    }

    private ItemPriceResolutionResult? SelectBestSnapshot(
        IReadOnlyCollection<PriceSnapshot> snapshots,
        string marketHashName,
        bool staleOnly = false,
        bool freshOnly = false)
    {
        var now = DateTime.UtcNow;
        var staleThreshold = now.AddHours(-Math.Max(1, _options.StaleSnapshotHours > 0 ? _options.StaleSnapshotHours : _options.SnapshotCacheHours));
        var candidates = snapshots
            .Where(snapshot => !snapshot.IsSelected)
            .Where(snapshot => snapshot.HasPrice && (snapshot.PriceUsd ?? snapshot.Price).HasValue)
            .Where(snapshot => snapshot.ObservedAtUtc >= staleThreshold || snapshot.UpdatedAtUtc >= staleThreshold)
            .Select(snapshot => FromSnapshot(snapshot, now))
            .Where(item => !staleOnly || item.IsStale)
            .Where(item => !freshOnly || !item.IsStale && item.ExpiresAtUtc > now)
            .Where(item => item.ConfidenceScore > 0.25m)
            .OrderBy(item => GetSelectionRank(item))
            .ThenByDescending(item => item.ConfidenceScore)
            .ThenBy(item => item.Price)
            .ToList();

        var selected = candidates.FirstOrDefault();
        if (selected is not null)
        {
            selected.ResolvedMarketHashName = marketHashName;
        }

        return selected;
    }

    private ItemPriceResolutionResult? SelectSelectedSnapshot(
        IReadOnlyCollection<PriceSnapshot> snapshots,
        string marketHashName)
    {
        var now = DateTime.UtcNow;
        var staleThreshold = now.AddHours(-Math.Max(1, _options.StaleSnapshotHours > 0 ? _options.StaleSnapshotHours : _options.SnapshotCacheHours));
        var selected = snapshots
            .Where(snapshot => snapshot.IsSelected)
            .Where(snapshot => snapshot.HasPrice && (snapshot.PriceUsd ?? snapshot.Price).HasValue)
            .Where(snapshot => snapshot.ObservedAtUtc >= staleThreshold || snapshot.UpdatedAtUtc >= staleThreshold)
            .OrderByDescending(snapshot => snapshot.ExpiresAtUtc)
            .ThenByDescending(snapshot => snapshot.UpdatedAtUtc)
            .Select(snapshot => FromSnapshot(snapshot, now))
            .FirstOrDefault();

        if (selected is not null)
        {
            selected.ResolvedMarketHashName = marketHashName;
        }

        return selected;
    }

    private List<string> GetMissingExpectedSourceNames(
        IReadOnlyCollection<PriceSnapshot> snapshots,
        GameDefinition game)
    {
        var availableSources = snapshots
            .Where(snapshot => !snapshot.IsSelected)
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Source))
            .Select(snapshot => snapshot.Source)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        return GetExpectedSourceNames(game)
            .Where(source => !availableSources.Contains(source))
            .ToList();
    }

    private List<string> GetMissingHigherPrioritySourceNames(
        IReadOnlyCollection<PriceSnapshot> snapshots,
        GameDefinition game,
        ItemPriceResolutionResult selected)
    {
        var selectedRank = GetSourceSelectionRank(selected.Source);
        var now = DateTime.UtcNow;
        var availableFreshSources = snapshots
            .Where(snapshot => !snapshot.IsSelected)
            .Where(snapshot => snapshot.HasPrice && (snapshot.PriceUsd ?? snapshot.Price).HasValue)
            .Where(snapshot => snapshot.ExpiresAtUtc > now)
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Source))
            .Select(snapshot => snapshot.Source)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        return GetExpectedSourceNames(game)
            .Where(source => GetSourceSelectionRank(source) < selectedRank)
            .Where(source => !availableFreshSources.Contains(source))
            .ToList();
    }

    private List<string> GetExpectedSourceNames(GameDefinition game)
    {
        var expectedSources = new List<string>();
        if (_options.EnableSteamSource && game.SupportsSteamMarketPricing)
        {
            expectedSources.Add(PriceSourceNames.Steam);
        }

        if (_options.EnableSkinportSource && game.SupportsSkinportPricing)
        {
            expectedSources.Add(PriceSourceNames.Skinport);
        }

        if (_options.EnableDMarketSource)
        {
            expectedSources.Add(PriceSourceNames.DMarket);
        }

        if (_options.EnableCsFloatSource && game.SteamAppId == 730)
        {
            expectedSources.Add(PriceSourceNames.CSFloat);
        }

        return expectedSources;
    }

    private Task LogCachedSelectionSkippedSourcesAsync(
        GameDefinition game,
        string marketHashName,
        ItemPriceResolutionResult cachedSelection,
        IReadOnlyCollection<PriceSnapshot> snapshots,
        IReadOnlyCollection<string> missingExpectedSources,
        CancellationToken cancellationToken)
    {
        return _priceDiagnosticLogService.LogProblemAsync(
            "SourceSkipped",
            "Resolver",
            "Fresh cached selected price returned before probing missing source snapshots.",
            game.Type,
            game.SteamAppId,
            marketHashName,
            endpoint: "ItemPriceResolver cache short-circuit",
            priceType: cachedSelection.PriceType,
            priceUsd: cachedSelection.Price,
            confidenceScore: cachedSelection.ConfidenceScore,
            status: "CachedShortCircuit",
            detailsJson: JsonSerializer.Serialize(new
            {
                selected = new
                {
                    cachedSelection.Source,
                    cachedSelection.PriceType,
                    priceUsd = cachedSelection.Price,
                    cachedSelection.Status,
                    cachedSelection.ExpiresAtUtc,
                    cachedSelection.ConfidenceScore
                },
                missingExpectedSources,
                existingSources = snapshots
                    .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Source))
                    .GroupBy(snapshot => snapshot.Source, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        source = group.Key,
                        rows = group.Count(),
                        bestUpdatedAtUtc = group.Max(snapshot => snapshot.UpdatedAtUtc)
                    })
                    .ToList()
            }),
            cancellationToken: cancellationToken);
    }

    internal static int GetSelectionRank(ItemPriceResolutionResult result)
    {
        return (result.IsStale ? 100 : 0) + GetSourceSelectionRank(result.Source);
    }

    private static int GetSourceSelectionRank(string? source)
    {
        return source switch
        {
            PriceSourceNames.Steam => 10,
            PriceSourceNames.Skinport => 20,
            PriceSourceNames.DMarket => 30,
            PriceSourceNames.CSFloat => 80,
            _ => 100
        };
    }

    private async Task<Dictionary<string, List<PriceSnapshot>>> LoadSnapshotsAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken)
    {
        var appId = _gameCatalog.Get(gameType).SteamAppId;
        var validFrom = DateTime.UtcNow.AddHours(-Math.Max(1, _options.StaleSnapshotHours > 0 ? _options.StaleSnapshotHours : _options.SnapshotCacheHours));
        var snapshots = await _dbContext.PriceSnapshots
            .AsNoTracking()
            .Where(item =>
                item.AppId == appId &&
                item.Currency == _options.PreferredCurrency &&
                marketHashNames.Contains(item.MarketHashName) &&
                item.UpdatedAtUtc >= validFrom)
            .ToListAsync(cancellationToken);

        return snapshots
            .GroupBy(item => item.MarketHashName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
    }

    private async Task<List<PriceSnapshot>> LoadSnapshotsForNameAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken)
    {
        var appId = _gameCatalog.Get(gameType).SteamAppId;
        var validFrom = DateTime.UtcNow.AddHours(-Math.Max(1, _options.StaleSnapshotHours > 0 ? _options.StaleSnapshotHours : _options.SnapshotCacheHours));
        return await _dbContext.PriceSnapshots
            .AsNoTracking()
            .Where(item =>
                item.AppId == appId &&
                item.MarketHashName == marketHashName &&
                item.Currency == _options.PreferredCurrency &&
                item.UpdatedAtUtc >= validFrom)
            .ToListAsync(cancellationToken);
    }

    private async Task SaveSnapshotAsync(GameType gameType, PriceSourceResult result, CancellationToken cancellationToken)
    {
        var marketHashName = MarketHashNameUtility.Normalize(result.ResolvedMarketHashName);
        if (string.IsNullOrWhiteSpace(marketHashName))
        {
            return;
        }

        var appId = _gameCatalog.Get(gameType).SteamAppId;
        var priceType = string.IsNullOrWhiteSpace(result.PriceType) ? PriceTypeNames.Unavailable : result.PriceType;
        var source = string.IsNullOrWhiteSpace(result.Source) ? PriceSourceNames.Unavailable : result.Source;
        var existing = await _dbContext.PriceSnapshots
            .SingleOrDefaultAsync(item =>
                item.AppId == appId &&
                item.MarketHashName == marketHashName &&
                item.Currency == "USD" &&
                !item.IsSelected &&
                item.Source == source &&
                item.PriceType == priceType,
                cancellationToken);

        var updatedAtUtc = DateTime.UtcNow;
        var observedAtUtc = result.ObservedAtUtc ?? updatedAtUtc;
        var ttlSeconds = result.TtlSeconds ?? GetFreshSnapshotSeconds(result);
        if (existing is null)
        {
            existing = new PriceSnapshot
            {
                Id = Guid.NewGuid(),
                AppId = appId,
                MarketHashName = marketHashName,
                Source = source,
                PriceType = priceType
            };
            _dbContext.PriceSnapshots.Add(existing);
        }

        existing.Currency = "USD";
        existing.Source = source;
        existing.IsSelected = false;
        existing.SourceItemId = result.SourceItemId;
        existing.PriceType = priceType;
        existing.Price = result.Price;
        existing.PriceUsd = result.Price;
        existing.OriginalPrice = result.OriginalPrice ?? result.Price;
        existing.OriginalCurrency = result.OriginalCurrency ?? result.Currency;
        existing.FxRate = result.FxRate ?? (string.Equals(result.Currency, "USD", StringComparison.OrdinalIgnoreCase) ? 1m : null);
        existing.Quantity = result.Quantity;
        existing.Volume = result.Volume;
        existing.SalesCount = result.SalesCount;
        existing.BestBidUsd = result.BestBidUsd;
        existing.BestAskUsd = result.BestAskUsd;
        existing.Status = result.Success && result.Price.HasValue
            ? result.IsEstimated ? "Estimated" : "Live"
            : "Unavailable";
        existing.HasPrice = result.Success && result.Price.HasValue;
        existing.IsEstimated = result.IsEstimated;
        existing.ConfidenceScore = ClampConfidence(result.ConfidenceScore);
        existing.ObservedAtUtc = observedAtUtc;
        existing.FailureReason = result.FailureReason;
        existing.RawPayloadHash = result.RawPayloadHash;
        existing.ProvenanceJson = result.ProvenanceJson;
        existing.UpdatedAtUtc = updatedAtUtc;
        existing.TtlSeconds = Math.Max(60, ttlSeconds);
        existing.ExpiresAtUtc = result.ExpiresAtUtc ?? updatedAtUtc.AddSeconds(existing.TtlSeconds);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ItemPriceResolutionResult> SaveSelectedSnapshotAsync(
        GameType gameType,
        ItemPriceResolutionResult selected,
        CancellationToken cancellationToken)
    {
        var marketHashName = MarketHashNameUtility.Normalize(selected.ResolvedMarketHashName);
        if (string.IsNullOrWhiteSpace(marketHashName))
        {
            return selected;
        }

        var appId = _gameCatalog.Get(gameType).SteamAppId;
        var selectedRows = await _dbContext.PriceSnapshots
            .Where(item =>
                item.AppId == appId &&
                item.MarketHashName == marketHashName &&
                item.Currency == "USD" &&
                item.IsSelected)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);

        var updatedAtUtc = DateTime.UtcNow;
        var ttlSeconds = Math.Max(1, _options.SelectedPriceCacheHours) * 60 * 60;
        var existing = selectedRows.FirstOrDefault();
        if (existing is null)
        {
            existing = new PriceSnapshot
            {
                Id = Guid.NewGuid(),
                AppId = appId,
                MarketHashName = marketHashName,
                IsSelected = true
            };
            _dbContext.PriceSnapshots.Add(existing);
        }

        foreach (var duplicate in selectedRows.Skip(1))
        {
            _dbContext.PriceSnapshots.Remove(duplicate);
        }

        existing.Currency = "USD";
        existing.Source = string.IsNullOrWhiteSpace(selected.Source) ? PriceSourceNames.Unavailable : selected.Source;
        existing.IsSelected = true;
        existing.SourceItemId = null;
        existing.PriceType = string.IsNullOrWhiteSpace(selected.PriceType) ? PriceTypeNames.Unavailable : selected.PriceType;
        existing.Price = selected.Price;
        existing.PriceUsd = selected.Price;
        existing.OriginalPrice = selected.OriginalPrice ?? selected.Price;
        existing.OriginalCurrency = selected.OriginalCurrency ?? selected.Currency;
        existing.FxRate = selected.FxRate ?? 1m;
        existing.Quantity = selected.Quantity;
        existing.Volume = selected.Volume;
        existing.SalesCount = selected.SalesCount;
        existing.BestBidUsd = selected.BestBidUsd;
        existing.BestAskUsd = selected.BestAskUsd;
        existing.Status = selected.HasPrice
            ? selected.IsEstimated ? "Estimated" : "Selected"
            : "Unavailable";
        existing.HasPrice = selected.HasPrice && selected.Price.HasValue;
        existing.IsEstimated = selected.IsEstimated;
        existing.ConfidenceScore = ClampConfidence(selected.ConfidenceScore);
        existing.ObservedAtUtc = selected.ObservedAtUtc ?? selected.LastUpdatedUtc ?? updatedAtUtc;
        existing.FailureReason = selected.FailureReason;
        existing.RawPayloadHash = null;
        existing.ProvenanceJson = selected.Provenance;
        existing.UpdatedAtUtc = updatedAtUtc;
        existing.TtlSeconds = ttlSeconds;
        existing.ExpiresAtUtc = updatedAtUtc.AddSeconds(ttlSeconds);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return FromSnapshot(existing, DateTime.UtcNow);
    }

    private ItemPriceResolutionResult FromSourceResult(PriceSourceResult source, bool isCached)
    {
        var now = DateTime.UtcNow;
        var expiresAt = source.ExpiresAtUtc ?? now.AddSeconds(source.TtlSeconds ?? GetFreshSnapshotSeconds(source));
        return new ItemPriceResolutionResult
        {
            HasPrice = source.Success && source.Price.HasValue,
            Price = source.Price,
            Currency = "USD",
            Source = source.Source,
            PriceType = source.PriceType,
            Status = source.Success && source.Price.HasValue
                ? source.IsEstimated ? "Estimated" : isCached ? "Cached" : "Live"
                : "Unavailable",
            IsCached = isCached || source.IsCached,
            IsEstimated = source.IsEstimated,
            IsStale = source.IsStale || expiresAt <= now,
            ConfidenceScore = ClampConfidence(source.ConfidenceScore),
            LastUpdatedUtc = source.LastUpdatedUtc,
            ObservedAtUtc = source.ObservedAtUtc,
            ExpiresAtUtc = expiresAt,
            FailureReason = source.FailureReason,
            OriginalPrice = source.OriginalPrice,
            OriginalCurrency = source.OriginalCurrency,
            FxRate = source.FxRate,
            Quantity = source.Quantity,
            Volume = source.Volume,
            SalesCount = source.SalesCount,
            BestBidUsd = source.BestBidUsd,
            BestAskUsd = source.BestAskUsd,
            Provenance = source.ProvenanceJson,
            ResolvedMarketHashName = source.ResolvedMarketHashName
        };
    }

    private ItemPriceResolutionResult FromSnapshot(PriceSnapshot snapshot, DateTime now)
    {
        var price = snapshot.PriceUsd ?? snapshot.Price;
        var isStale = snapshot.ExpiresAtUtc <= now;
        var confidence = ClampConfidence(isStale
            ? ApplyStalePenalty(snapshot.ConfidenceScore, snapshot.ObservedAtUtc == default ? snapshot.UpdatedAtUtc : snapshot.ObservedAtUtc)
            : snapshot.ConfidenceScore);
        return new ItemPriceResolutionResult
        {
            HasPrice = snapshot.HasPrice && price.HasValue,
            Price = price,
            Currency = "USD",
            Source = snapshot.Source,
            PriceType = string.IsNullOrWhiteSpace(snapshot.PriceType) ? PriceTypeNames.Unavailable : snapshot.PriceType,
            Status = snapshot.HasPrice ? isStale ? "Stale" : "Cached" : snapshot.Status,
            IsCached = true,
            IsEstimated = snapshot.IsEstimated,
            IsStale = isStale,
            ConfidenceScore = confidence,
            LastUpdatedUtc = snapshot.UpdatedAtUtc,
            ObservedAtUtc = snapshot.ObservedAtUtc == default ? snapshot.UpdatedAtUtc : snapshot.ObservedAtUtc,
            ExpiresAtUtc = snapshot.ExpiresAtUtc,
            FailureReason = snapshot.FailureReason,
            OriginalPrice = snapshot.OriginalPrice,
            OriginalCurrency = snapshot.OriginalCurrency,
            FxRate = snapshot.FxRate,
            Quantity = snapshot.Quantity,
            Volume = snapshot.Volume,
            SalesCount = snapshot.SalesCount,
            BestBidUsd = snapshot.BestBidUsd,
            BestAskUsd = snapshot.BestAskUsd,
            Provenance = snapshot.ProvenanceJson,
            ResolvedMarketHashName = snapshot.MarketHashName,
            NeedsRefresh = isStale
        };
    }

    private PriceSourceResult ToSourceResult(ItemPriceResolutionResult result)
    {
        return new PriceSourceResult
        {
            Success = result.HasPrice,
            Price = result.Price,
            Currency = "USD",
            Source = result.Source,
            PriceType = result.PriceType,
            Status = result.Status,
            IsCached = result.IsCached,
            IsEstimated = result.IsEstimated,
            IsStale = result.IsStale,
            ConfidenceScore = result.ConfidenceScore,
            LastUpdatedUtc = result.LastUpdatedUtc,
            ObservedAtUtc = result.ObservedAtUtc,
            ExpiresAtUtc = result.ExpiresAtUtc,
            FailureReason = result.FailureReason,
            ResolvedMarketHashName = result.ResolvedMarketHashName
        };
    }

    private int GetFreshSnapshotSeconds(PriceSourceResult result)
    {
        var minutes = result.Source switch
        {
            PriceSourceNames.Steam => _options.SteamCacheMinutes,
            PriceSourceNames.CSFloat => _options.CsFloatCacheMinutes,
            PriceSourceNames.Skinport when result.PriceType == PriceTypeNames.LowestListing => _options.SkinportItemsCacheMinutes,
            PriceSourceNames.Skinport when result.PriceType is PriceTypeNames.AvgSale or PriceTypeNames.Suggested => _options.SkinportOutOfStockCacheMinutes,
            PriceSourceNames.Skinport => _options.SkinportHistoryCacheMinutes,
            PriceSourceNames.DMarket when result.PriceType == PriceTypeNames.LowestListing => _options.DMarketLiveCacheMinutes,
            PriceSourceNames.DMarket => _options.DMarketSalesHistoryCacheMinutes,
            _ => _options.NegativeCacheMinutes
        };

        return Math.Max(1, minutes) * 60;
    }

    private TimeSpan GetMemoryDuration(ItemPriceResolutionResult result)
    {
        if (result.ExpiresAtUtc.HasValue)
        {
            var remaining = result.ExpiresAtUtc.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                return remaining;
            }
        }

        return TimeSpan.FromMinutes(Math.Max(1, _options.NegativeCacheMinutes));
    }

    private decimal ApplyStalePenalty(decimal confidence, DateTime observedAtUtc)
    {
        var ageHours = Math.Max(0, (decimal)(DateTime.UtcNow - observedAtUtc).TotalHours);
        var staleWindow = Math.Max(1, _options.StaleSnapshotHours > 0 ? _options.StaleSnapshotHours : _options.SnapshotCacheHours);
        var penalty = Math.Min(0.45m, ageHours / staleWindow * 0.35m);
        return ClampConfidence(confidence - penalty);
    }

    private async Task LogRejectedSourceAsync(PriceSourceResult result, string marketHashName, string reason, CancellationToken cancellationToken)
    {
        await _priceDiagnosticLogService.LogProblemAsync(
            string.Equals(result.Currency, "USD", StringComparison.OrdinalIgnoreCase) ? "SourceRejected" : "CurrencyMismatch",
            result.Source,
            reason,
            marketHashName: marketHashName,
            priceType: result.PriceType,
            priceUsd: result.Price,
            originalCurrency: result.Currency,
            confidenceScore: result.ConfidenceScore,
            status: result.Status,
            detailsJson: JsonSerializer.Serialize(new
            {
                result.ResolvedMarketHashName,
                result.OriginalPrice,
                result.OriginalCurrency,
                result.FxRate
            }),
            cancellationToken: cancellationToken);
        await LogVerboseAppAsync(
            "Warning",
            $"Price source rejected. MarketHashName={marketHashName}; Source={result.Source}; PriceType={result.PriceType}; Currency={result.Currency}; Reason={reason}",
            nameof(ItemPriceResolver),
            cancellationToken: cancellationToken);
    }

    private Task LogFinalResolutionFailedAsync(
        GameDefinition game,
        string marketHashName,
        string finalReason,
        IReadOnlyCollection<PriceSourceResult> sourceResults,
        IReadOnlyCollection<PriceSnapshot> existingSnapshots,
        CancellationToken cancellationToken)
    {
        return _priceDiagnosticLogService.LogProblemAsync(
            "FinalResolutionFailed",
            "Resolver",
            finalReason,
            game.Type,
            game.SteamAppId,
            marketHashName,
            status: "NoReliablePrice",
            detailsJson: JsonSerializer.Serialize(new
            {
                itemName = marketHashName,
                game = game.DisplayName,
                finalReason,
                sources = BuildSourceProblemSummary(sourceResults),
                staleSnapshotsRejected = existingSnapshots
                    .Where(snapshot => snapshot.HasPrice && snapshot.ExpiresAtUtc <= DateTime.UtcNow)
                    .OrderByDescending(snapshot => snapshot.UpdatedAtUtc)
                    .Take(8)
                    .Select(snapshot => new
                    {
                        snapshot.Source,
                        snapshot.PriceType,
                        priceUsd = snapshot.PriceUsd ?? snapshot.Price,
                        snapshot.ConfidenceScore,
                        snapshot.ExpiresAtUtc,
                        snapshot.FailureReason
                    })
                    .ToList()
            }),
            cancellationToken: cancellationToken);
    }

    private static IReadOnlyList<object> BuildSourceProblemSummary(IReadOnlyCollection<PriceSourceResult> sourceResults)
    {
        return sourceResults
            .GroupBy(result => string.IsNullOrWhiteSpace(result.Source) ? PriceSourceNames.Unavailable : result.Source)
            .Select(group => group.OrderByDescending(result => result.Success).First())
            .Select(result => new
            {
                source = result.Source,
                state = result.Success && result.Price.HasValue ? "Found" :
                    result.Status == "RateLimited" ? "Failed" :
                    (result.FailureReason?.Contains("not return this title", StringComparison.OrdinalIgnoreCase) == true ||
                     result.FailureReason?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true) ? "NotFound" :
                    "Failed",
                reason = result.FailureReason,
                result.Status,
                result.PriceType,
                priceUsd = result.Price,
                result.Currency,
                result.ConfidenceScore,
                result.IsEstimated,
                result.IsCached,
                result.IsStale
            })
            .Cast<object>()
            .ToList();
    }

    private Task LogVerboseAppAsync(
        string level,
        string message,
        string? source = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        return _options.EnableVerbosePriceDiagnostics
            ? _appLogService.WriteAsync(level, message, source, exception, cancellationToken)
            : Task.CompletedTask;
    }

    private void LogSourceResult(PriceSourceResult result)
    {
        _logger.LogInformation(
            "Price source result. Source={Source} MarketHashName={MarketHashName} PriceType={PriceType} PriceUsd={PriceUsd} Currency={Currency} Confidence={Confidence} Success={Success} Failure={Failure}",
            result.Source,
            result.ResolvedMarketHashName,
            result.PriceType,
            result.Price,
            result.Currency,
            result.ConfidenceScore,
            result.Success,
            result.FailureReason);
    }

    private string BuildMemoryCacheKey(GameType gameType, string marketHashName)
    {
        return $"resolved-price::{(int)gameType}::{_options.PreferredCurrency}::{marketHashName}";
    }

    private static List<string> NormalizeNames(IEnumerable<string?> marketHashNames)
    {
        return marketHashNames
            .Select(MarketHashNameUtility.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsRetryableFailure(ItemPriceResolutionResult result)
    {
        return !result.HasPrice && IsRetryableFailure(result.FailureReason);
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
               failureReason.Contains("transient", StringComparison.OrdinalIgnoreCase) ||
               failureReason.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
               failureReason.Contains("429", StringComparison.OrdinalIgnoreCase);
    }

    private static ItemPriceResolutionResult CreateUnavailable(string? failureReason, string? marketHashName)
    {
        return new ItemPriceResolutionResult
        {
            HasPrice = false,
            Currency = "USD",
            Source = PriceSourceNames.Unavailable,
            PriceType = PriceTypeNames.Unavailable,
            Status = "Unavailable",
            FailureReason = failureReason,
            ResolvedMarketHashName = marketHashName,
            ConfidenceScore = 0m
        };
    }

    private static string? BuildFailureReason(IEnumerable<PriceSourceResult> results)
    {
        return results
            .Where(item => !item.Success && !string.IsNullOrWhiteSpace(item.FailureReason))
            .Select(item => $"{item.Source}: {item.FailureReason}")
            .FirstOrDefault() ?? "No reliable price.";
    }

    private static ItemPriceResolutionResult Clone(ItemPriceResolutionResult source, bool isCachedOverride)
    {
        return new ItemPriceResolutionResult
        {
            HasPrice = source.HasPrice,
            Price = source.Price,
            Currency = source.Currency,
            Source = source.Source,
            PriceType = source.PriceType,
            Status = isCachedOverride && source.HasPrice && source.Status == "Live" ? "Cached" : source.Status,
            IsCached = isCachedOverride || source.IsCached,
            IsEstimated = source.IsEstimated,
            IsStale = source.IsStale,
            ConfidenceScore = source.ConfidenceScore,
            LastUpdatedUtc = source.LastUpdatedUtc,
            ObservedAtUtc = source.ObservedAtUtc,
            ExpiresAtUtc = source.ExpiresAtUtc,
            FailureReason = source.FailureReason,
            OriginalPrice = source.OriginalPrice,
            OriginalCurrency = source.OriginalCurrency,
            FxRate = source.FxRate,
            Quantity = source.Quantity,
            Volume = source.Volume,
            SalesCount = source.SalesCount,
            BestBidUsd = source.BestBidUsd,
            BestAskUsd = source.BestAskUsd,
            Provenance = source.Provenance,
            ResolvedMarketHashName = source.ResolvedMarketHashName,
            NeedsRefresh = source.NeedsRefresh,
            SteamResult = source.SteamResult,
            CsFloatResult = source.CsFloatResult,
            SkinportResult = source.SkinportResult,
            DMarketResult = source.DMarketResult
        };
    }

    private static decimal ClampConfidence(decimal confidence)
    {
        if (confidence < 0)
        {
            return 0;
        }

        return confidence > 1 ? 1 : confidence;
    }
}
