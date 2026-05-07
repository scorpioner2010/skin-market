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
    private readonly ISteamInventoryService _steamInventoryService;
    private readonly IItemPriceResolver _itemPriceResolver;
    private readonly ISteamMarketPriceService _steamMarketPriceService;
    private readonly ICsFloatPriceService _csFloatPriceService;
    private readonly ISkinportPricingService _skinportPricingService;
    private readonly IDMarketPricingService _dMarketPricingService;
    private readonly IGameCatalog _gameCatalog;
    private readonly AppRuntimeState _runtimeState;

    public PricingDebugModel(
        AppDbContext dbContext,
        ISteamInventoryService steamInventoryService,
        IItemPriceResolver itemPriceResolver,
        ISteamMarketPriceService steamMarketPriceService,
        ICsFloatPriceService csFloatPriceService,
        ISkinportPricingService skinportPricingService,
        IDMarketPricingService dMarketPricingService,
        IGameCatalog gameCatalog,
        AppRuntimeState runtimeState)
    {
        _dbContext = dbContext;
        _steamInventoryService = steamInventoryService;
        _itemPriceResolver = itemPriceResolver;
        _steamMarketPriceService = steamMarketPriceService;
        _csFloatPriceService = csFloatPriceService;
        _skinportPricingService = skinportPricingService;
        _dMarketPricingService = dMarketPricingService;
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

        var inventory = await _steamInventoryService.GetInventoryAsync(appUser.SteamId, _gameCatalog.DefaultGameType, cancellationToken);
        if (!inventory.IsSuccess)
        {
            ErrorMessage = inventory.ErrorMessage;
            return;
        }

        foreach (var item in inventory.Items)
        {
            var marketHashName = MarketHashNameUtility.ResolvePrimary(item);
            var steamResult = string.IsNullOrWhiteSpace(marketHashName)
                ? new PriceSourceResult { Source = "Steam", Status = "Unavailable", FailureReason = "MissingMarketHashName" }
                : await _steamMarketPriceService.ProbePriceAsync(marketHashName, item.GameType, cancellationToken);
            var csFloatResult = string.IsNullOrWhiteSpace(marketHashName)
                ? new PriceSourceResult { Source = "CSFloat", Status = "Unavailable", FailureReason = "MissingMarketHashName" }
                : await _csFloatPriceService.ProbePriceAsync(marketHashName, item.GameType, cancellationToken);
            var skinportResult = string.IsNullOrWhiteSpace(marketHashName)
                ? new PriceSourceResult { Source = "Skinport", Status = "Unavailable", FailureReason = "MissingMarketHashName" }
                : await _skinportPricingService.ProbePriceAsync(marketHashName, item.GameType, cancellationToken);
            var dMarketResult = string.IsNullOrWhiteSpace(marketHashName)
                ? new PriceSourceResult { Source = "DMarket", Status = "Unavailable", FailureReason = "MissingMarketHashName" }
                : await _dMarketPricingService.ProbePriceAsync(marketHashName, item.GameType, cancellationToken);
            var finalResult = await _itemPriceResolver.ResolveAsync(item, cancellationToken);

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
