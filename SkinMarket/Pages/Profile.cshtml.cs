using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Localization;
using SkinMarket.Models;

namespace SkinMarket.Pages;

public class ProfileModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IBalanceService _balanceService;
    private readonly ISteamProfileService _steamProfileService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly AppRuntimeState _runtimeState;

    public ProfileModel(
        AppDbContext dbContext,
        IBalanceService balanceService,
        ISteamProfileService steamProfileService,
        IStringLocalizer<SharedResource> localizer,
        AppRuntimeState runtimeState)
    {
        _dbContext = dbContext;
        _balanceService = balanceService;
        _steamProfileService = steamProfileService;
        _localizer = localizer;
        _runtimeState = runtimeState;
    }

    public AppUser? AppUser { get; private set; }
    public decimal Balance { get; private set; }
    public string? SteamPersonaName { get; private set; }
    public string? SteamAvatarUrl { get; private set; }
    [BindProperty]
    public TradeUrlInputModel Input { get; set; } = new();
    [TempData]
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return;
        }

        await LoadProfileAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return Page();
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            return RedirectToPage();
        }

        if (!string.IsNullOrWhiteSpace(Input.TradeUrl) && !IsValidTradeUrl(Input.TradeUrl))
        {
            ModelState.AddModelError(
                $"{nameof(Input)}.{nameof(Input.TradeUrl)}",
                UiTextLocalizer.LocalizeMessage(_localizer, "Trade URL must be a valid Steam trade offer link."));
        }

        if (!ModelState.IsValid)
        {
            AppUser = appUser;
            Balance = await _balanceService.GetBalanceAsync(appUser.Id, cancellationToken);
            SteamPersonaName = appUser.PersonaName ?? appUser.DisplayName;
            SteamAvatarUrl = appUser.AvatarUrl;
            return Page();
        }

        appUser.TradeUrl = string.IsNullOrWhiteSpace(Input.TradeUrl) ? null : Input.TradeUrl.Trim();
        await _dbContext.SaveChangesAsync(cancellationToken);

        SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Trade URL saved.");
        return RedirectToPage();
    }

    private async Task LoadProfileAsync(CancellationToken cancellationToken)
    {
        AppUser = await GetCurrentUserAsync(cancellationToken);
        if (AppUser is null)
        {
            return;
        }

        Balance = await _balanceService.GetBalanceAsync(AppUser.Id, cancellationToken);
        SteamPersonaName = AppUser.PersonaName ?? AppUser.DisplayName;
        SteamAvatarUrl = AppUser.AvatarUrl;

        var profileSummary = await _steamProfileService.GetProfileAsync(AppUser.SteamId, cancellationToken);
        if (profileSummary is not null)
        {
            SteamPersonaName = profileSummary.PersonaName;
            SteamAvatarUrl = profileSummary.AvatarFull;
        }

        Input = new TradeUrlInputModel
        {
            TradeUrl = AppUser.TradeUrl
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
            return null;
        }

        return await _dbContext.AppUsers
            .SingleOrDefaultAsync(user => user.SteamId == steamId, cancellationToken);
    }

    private static bool IsValidTradeUrl(string tradeUrl)
    {
        if (!Uri.TryCreate(tradeUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps &&
               uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.Equals("/tradeoffer/new/", StringComparison.OrdinalIgnoreCase) &&
               uri.Query.Contains("partner=", StringComparison.OrdinalIgnoreCase);
    }

    public class TradeUrlInputModel
    {
        [StringLength(500)]
        public string? TradeUrl { get; set; }
    }
}
