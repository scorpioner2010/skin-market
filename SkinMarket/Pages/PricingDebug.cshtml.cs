using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;
using SkinMarket.Services;

namespace SkinMarket.Pages;

public class PricingDebugModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ISteamInventoryRefreshService _steamInventoryRefreshService;
    private readonly IInventoryPriceRefreshService _inventoryPriceRefreshService;
    private readonly IGameCatalog _gameCatalog;
    private readonly AppRuntimeState _runtimeState;

    public PricingDebugModel(
        AppDbContext dbContext,
        ISteamInventoryRefreshService steamInventoryRefreshService,
        IInventoryPriceRefreshService inventoryPriceRefreshService,
        IGameCatalog gameCatalog,
        AppRuntimeState runtimeState)
    {
        _dbContext = dbContext;
        _steamInventoryRefreshService = steamInventoryRefreshService;
        _inventoryPriceRefreshService = inventoryPriceRefreshService;
        _gameCatalog = gameCatalog;
        _runtimeState = runtimeState;
    }

    public List<ItemPriceDiagnosticsResult> Items { get; private set; } = new();
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return;
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            return;
        }

        var gameType = _gameCatalog.DefaultGameType;
        var game = _gameCatalog.Get(gameType);
        var snapshot = await _steamInventoryRefreshService.GetLatestSnapshotAsync(appUser.SteamId, gameType, cancellationToken);
        if (snapshot is null)
        {
            await _steamInventoryRefreshService.EnqueueRefreshAsync(
                appUser.SteamId,
                gameType,
                SteamInventoryRefreshPriority.Normal,
                cancellationToken,
                reason: SteamInventoryRefreshReasons.InitialLoad);
            ErrorMessage = "Inventory snapshot is not cached yet. A background refresh has been queued.";
            return;
        }

        var marketHashNames = snapshot.Items
            .Select(MarketHashNameUtility.ResolvePrimary)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var currentPrices = await _inventoryPriceRefreshService.GetCurrentPricesAsync(marketHashNames, gameType, cancellationToken);
        var normalizedNames = marketHashNames
            .Select(MarketHashNameUtility.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();
        var snapshots = await _dbContext.PriceSnapshots
            .AsNoTracking()
            .Where(item =>
                item.AppId == game.SteamAppId &&
                !item.IsSelected &&
                normalizedNames.Contains(item.MarketHashName))
            .ToListAsync(cancellationToken);

        var refreshTargets = new List<string>();
        foreach (var item in snapshot.Items)
        {
            var marketHashName = MarketHashNameUtility.ResolvePrimary(item);
            var normalizedName = MarketHashNameUtility.Normalize(marketHashName);
            var steamResult = GetSnapshotResult(snapshots, normalizedName, PriceSourceNames.Steam);
            var csFloatResult = GetSnapshotResult(snapshots, normalizedName, PriceSourceNames.CSFloat);
            var skinportResult = GetSnapshotResult(snapshots, normalizedName, PriceSourceNames.Skinport);
            var dMarketResult = GetSnapshotResult(snapshots, normalizedName, PriceSourceNames.DMarket);
            var finalResult = normalizedName is not null && currentPrices.TryGetValue(normalizedName, out var cached)
                ? cached
                : new ItemPriceResolutionResult
                {
                    HasPrice = false,
                    Source = PriceSourceNames.Unavailable,
                    PriceType = PriceTypeNames.Unavailable,
                    Status = "Unavailable",
                    FailureReason = "No cached price."
                };
            if (!string.IsNullOrWhiteSpace(marketHashName) && finalResult.NeedsRefresh)
            {
                refreshTargets.Add(marketHashName);
            }

            var diagnosticsResult = new ItemPriceDiagnosticsResult
            {
                ItemName = item.Name,
                AssetId = item.AssetId,
                ClassId = item.ClassId,
                MarketHashName = item.MarketHashName,
                MarketName = item.MarketName,
                ResolvedMarketHashName = finalResult.ResolvedMarketHashName,
                SteamPrice = steamResult.Price,
                SteamStatus = steamResult.Status,
                CsFloatPrice = csFloatResult.Price,
                CsFloatStatus = csFloatResult.Status,
                SkinportPrice = skinportResult.Price,
                SkinportStatus = skinportResult.Status,
                DMarketPrice = dMarketResult.Price,
                DMarketStatus = dMarketResult.Status,
                SteamError = steamResult.ErrorMessage,
                CsFloatError = csFloatResult.ErrorMessage,
                SkinportError = skinportResult.ErrorMessage,
                DMarketError = dMarketResult.ErrorMessage,
                FinalPrice = finalResult.Price,
                FinalSource = finalResult.Source,
                FinalPriceType = finalResult.PriceType,
                ConfidenceScore = finalResult.ConfidenceScore,
                FinalStatus = finalResult.Status,
                FailureReason = finalResult.FailureReason
            };

            Items.Add(diagnosticsResult);
        }

        if (refreshTargets.Count > 0)
        {
            await _inventoryPriceRefreshService.QueueRefreshAsync(refreshTargets, gameType, cancellationToken);
        }
    }

    private static PriceSourceResult GetSnapshotResult(
        IReadOnlyCollection<PriceSnapshot> snapshots,
        string? normalizedMarketHashName,
        string source)
    {
        var snapshot = snapshots
            .Where(item => item.MarketHashName == normalizedMarketHashName && item.Source == source)
            .OrderBy(item => item.HasPrice ? 0 : 1)
            .ThenBy(item => item.ExpiresAtUtc <= DateTime.UtcNow ? 1 : 0)
            .ThenByDescending(item => item.ConfidenceScore)
            .FirstOrDefault();

        if (snapshot is null)
        {
            return new PriceSourceResult
            {
                Source = source,
                Status = "Unavailable",
                PriceType = PriceTypeNames.Unavailable,
                FailureReason = "No cached snapshot."
            };
        }

        return new PriceSourceResult
        {
            Success = snapshot.HasPrice,
            Source = snapshot.Source,
            PriceType = snapshot.PriceType,
            Price = snapshot.PriceUsd ?? snapshot.Price,
            Status = snapshot.HasPrice && snapshot.ExpiresAtUtc <= DateTime.UtcNow ? "Stale" : snapshot.Status,
            FailureReason = snapshot.FailureReason
        };
    }

    private async Task<AppUser?> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return null;
        }

        var steamId = User.FindFirst("SteamId")?.Value;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            ErrorMessage = "SteamID is not available for the current session.";
            return null;
        }

        var appUser = await _dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.SteamId == steamId, cancellationToken);

        if (appUser is null)
        {
            ErrorMessage = "Local user profile was not found.";
        }

        return appUser;
    }
}
