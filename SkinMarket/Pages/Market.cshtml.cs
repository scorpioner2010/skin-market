using SkinMarket.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using SkinMarket.Models;
using SkinMarket.Data;
using SkinMarket.Localization;

namespace SkinMarket.Pages;

public class MarketModel : PageModel
{
    private readonly IMarketService _marketService;
    private readonly IMarketPurchaseService _marketPurchaseService;
    private readonly IMarketDeliveryService _marketDeliveryService;
    private readonly AppDbContext _dbContext;
    private readonly IBalanceService _balanceService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public MarketModel(
        IMarketService marketService,
        IMarketPurchaseService marketPurchaseService,
        IMarketDeliveryService marketDeliveryService,
        AppDbContext dbContext,
        IBalanceService balanceService,
        IStringLocalizer<SharedResource> localizer)
    {
        _marketService = marketService;
        _marketPurchaseService = marketPurchaseService;
        _marketDeliveryService = marketDeliveryService;
        _dbContext = dbContext;
        _balanceService = balanceService;
        _localizer = localizer;
    }

    public List<MarketItem> Items { get; private set; } = new();
    public List<MarketItem> Purchases { get; private set; } = new();
    public Guid? CurrentUserId { get; private set; }
    public decimal CurrentBalance { get; private set; }
    [TempData]
    public string? SuccessMessage { get; set; }
    [TempData]
    public string? ErrorMessage { get; set; }
    [BindProperty]
    public Guid MarketItemId { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadCurrentUserAsync(cancellationToken);
        Items = await _marketService.GetAvailableItemsAsync(cancellationToken);
        if (CurrentUserId.HasValue)
        {
            Purchases = await _marketPurchaseService.GetRecentPurchasesAsync(CurrentUserId.Value, 10, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostBuyAsync(CancellationToken cancellationToken)
    {
        await LoadCurrentUserAsync(cancellationToken);
        if (!CurrentUserId.HasValue)
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to buy market items.");
            return RedirectToPage();
        }

        var result = await _marketPurchaseService.PurchaseAsync(MarketItemId, CurrentUserId.Value, cancellationToken);
        if (result.Success)
        {
            SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }
        else
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateDeliveryTradeAsync(CancellationToken cancellationToken)
    {
        await LoadCurrentUserAsync(cancellationToken);
        if (!CurrentUserId.HasValue)
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to create delivery trade.");
            return RedirectToPage();
        }

        var result = await _marketDeliveryService.CreateDeliveryTradeAsync(MarketItemId, CurrentUserId.Value, cancellationToken);
        if (result.Success)
        {
            SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }
        else
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmDeliveredAsync(CancellationToken cancellationToken)
    {
        await LoadCurrentUserAsync(cancellationToken);
        if (!CurrentUserId.HasValue)
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to confirm delivery.");
            return RedirectToPage();
        }

        var result = await _marketDeliveryService.ConfirmDeliveredAsync(MarketItemId, CurrentUserId.Value, cancellationToken);
        if (result.Success)
        {
            SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }
        else
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }

        return RedirectToPage();
    }

    private async Task LoadCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return;
        }

        var steamId = User.FindFirst("SteamId")?.Value;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return;
        }

        var user = await _dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.SteamId == steamId, cancellationToken);

        if (user is null)
        {
            return;
        }

        CurrentUserId = user.Id;
        CurrentBalance = await _balanceService.GetBalanceAsync(user.Id, cancellationToken);
    }
}
