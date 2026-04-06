using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages;

public class PricingDebugModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ISteamInventoryService _steamInventoryService;
    private readonly ISteamMarketPriceService _steamMarketPriceService;
    private readonly ISkinportPricingService _skinportPricingService;
    private readonly IGameCatalog _gameCatalog;

    public PricingDebugModel(
        AppDbContext dbContext,
        ISteamInventoryService steamInventoryService,
        ISteamMarketPriceService steamMarketPriceService,
        ISkinportPricingService skinportPricingService,
        IGameCatalog gameCatalog)
    {
        _dbContext = dbContext;
        _steamInventoryService = steamInventoryService;
        _steamMarketPriceService = steamMarketPriceService;
        _skinportPricingService = skinportPricingService;
        _gameCatalog = gameCatalog;
    }

    public List<ItemPriceDiagnosticsResult> Items { get; private set; } = new();
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
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
            var steamResult = await _steamMarketPriceService.ProbePriceAsync(item, cancellationToken);
            var skinportResult = await _skinportPricingService.ProbePriceAsync(item, cancellationToken);

            var diagnosticsResult = new ItemPriceDiagnosticsResult
            {
                ItemName = item.Name,
                AssetId = item.AssetId,
                ClassId = item.ClassId,
                MarketHashName = item.MarketHashName,
                MarketName = item.MarketName,
                SteamPrice = steamResult.Price,
                SkinportPrice = skinportResult.Price,
                SteamError = steamResult.ErrorMessage,
                SkinportError = skinportResult.ErrorMessage
            };

            if (steamResult.Success && steamResult.Price.HasValue)
            {
                diagnosticsResult.FinalPrice = steamResult.Price;
                diagnosticsResult.FinalSource = "Steam";
            }
            else if (skinportResult.Success && skinportResult.Price.HasValue)
            {
                diagnosticsResult.FinalPrice = skinportResult.Price;
                diagnosticsResult.FinalSource = "Skinport";
            }

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
