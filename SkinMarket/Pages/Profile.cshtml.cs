using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
    private const string AdminGiftCode = "7789";
    private const string DiagnosticsSource = "ProfileDiagnostics";
    private readonly AppDbContext _dbContext;
    private readonly IBalanceService _balanceService;
    private readonly ISteamProfileService _steamProfileService;
    private readonly IAppLogService _appLogService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly AppRuntimeState _runtimeState;

    public ProfileModel(
        AppDbContext dbContext,
        IBalanceService balanceService,
        ISteamProfileService steamProfileService,
        IAppLogService appLogService,
        IStringLocalizer<SharedResource> localizer,
        AppRuntimeState runtimeState)
    {
        _dbContext = dbContext;
        _balanceService = balanceService;
        _steamProfileService = steamProfileService;
        _appLogService = appLogService;
        _localizer = localizer;
        _runtimeState = runtimeState;
    }

    public AppUser? AppUser { get; private set; }
    public decimal Balance { get; private set; }
    public string? SteamPersonaName { get; private set; }
    public string? SteamAvatarUrl { get; private set; }
    [BindProperty]
    public TradeUrlInputModel Input { get; set; } = new();
    [BindProperty]
    public GiftCodeInputModel GiftCodeInput { get; set; } = new();
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

        var tradeUrl = Input.TradeUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(tradeUrl) && !IsValidTradeUrl(tradeUrl))
        {
            ModelState.AddModelError(
                $"{nameof(Input)}.{nameof(Input.TradeUrl)}",
                UiTextLocalizer.LocalizeMessage(_localizer, "Trade URL must be a valid Steam trade offer link."));
        }
        else if (!string.IsNullOrWhiteSpace(tradeUrl) && !SteamTradeUrlUtility.BelongsToSteamId(tradeUrl, appUser.SteamId))
        {
            ModelState.AddModelError(
                $"{nameof(Input)}.{nameof(Input.TradeUrl)}",
                "Trade URL belongs to another Steam account.");
        }

        if (!ModelState.IsValid)
        {
            AppUser = appUser;
            Balance = await _balanceService.GetBalanceAsync(appUser.Id, cancellationToken);
            SteamPersonaName = appUser.PersonaName ?? appUser.DisplayName;
            SteamAvatarUrl = appUser.AvatarUrl;
            return Page();
        }

        appUser.TradeUrl = string.IsNullOrWhiteSpace(tradeUrl) ? null : tradeUrl;
        await _dbContext.SaveChangesAsync(cancellationToken);

        SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Trade URL saved.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostActivateGiftCodeAsync(CancellationToken cancellationToken)
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

        if (!string.Equals(GiftCodeInput.Code?.Trim(), AdminGiftCode, StringComparison.Ordinal))
        {
            SuccessMessage = null;
            ErrorMessage = "Gift code is invalid.";
            await LoadProfileForUserAsync(appUser, cancellationToken);
            return Page();
        }

        if (!appUser.IsAdmin)
        {
            appUser.IsAdmin = true;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _appLogService.WriteAsync(
                "Warning",
                $"Admin access activated by gift code. AppUserId={appUser.Id}; SteamId={appUser.SteamId}",
                DiagnosticsSource,
                cancellationToken: cancellationToken);
        }

        await RefreshAuthCookieAsync(appUser, cancellationToken);
        SuccessMessage = "Gift code activated.";
        return RedirectToPage();
    }

    private async Task LoadProfileAsync(CancellationToken cancellationToken)
    {
        var claimSteamId = User.FindFirst("SteamId")?.Value ?? "<null>";
        var claimName = User.Identity?.Name ?? "<null>";
        var claimAvatarUrl = User.FindFirst("AvatarUrl")?.Value ?? "<null>";

        await _appLogService.WriteAsync(
            "Info",
            $"Profile page claims snapshot. IsAuthenticated={User.Identity?.IsAuthenticated ?? false}; ClaimSteamId={claimSteamId}; ClaimName={claimName}; ClaimAvatarUrl={claimAvatarUrl}",
            DiagnosticsSource,
            cancellationToken: cancellationToken);

        AppUser = await GetCurrentUserAsync(cancellationToken);
        if (AppUser is null)
        {
            await _appLogService.WriteAsync(
                "Warning",
                "Profile page could not load AppUser for current claims.",
                DiagnosticsSource,
                cancellationToken: cancellationToken);
            return;
        }

        await LoadProfileForUserAsync(AppUser, cancellationToken);
    }

    private async Task LoadProfileForUserAsync(AppUser appUser, CancellationToken cancellationToken)
    {
        AppUser = appUser;
        var claimName = User.Identity?.Name ?? "<null>";
        var claimAvatarUrl = User.FindFirst("AvatarUrl")?.Value ?? "<null>";
        Balance = await _balanceService.GetBalanceAsync(appUser.Id, cancellationToken);
        SteamPersonaName = appUser.PersonaName ?? appUser.DisplayName;
        SteamAvatarUrl = appUser.AvatarUrl;

        await _appLogService.WriteAsync(
            "Info",
            $"Profile page database snapshot. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; DisplayName={appUser.DisplayName}; PersonaName={appUser.PersonaName ?? "<null>"}; AvatarUrl={appUser.AvatarUrl ?? "<null>"}; TradeUrl={appUser.TradeUrl ?? "<null>"}",
            DiagnosticsSource,
            cancellationToken: cancellationToken);

        var profileSummary = await _steamProfileService.GetProfileAsync(appUser.SteamId, cancellationToken);
        await _appLogService.WriteAsync(
            "Info",
            profileSummary is null
                ? $"Profile page Steam API snapshot is empty. SteamId={appUser.SteamId}"
                : $"Profile page Steam API snapshot. SteamId={appUser.SteamId}; PersonaName={profileSummary.PersonaName}; AvatarUrl={profileSummary.AvatarFull ?? "<null>"}",
            DiagnosticsSource,
            cancellationToken: cancellationToken);

        if (profileSummary is not null)
        {
            SteamPersonaName = profileSummary.PersonaName;
            SteamAvatarUrl = profileSummary.AvatarFull;
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Profile page render snapshot. SteamId={appUser.SteamId}; RenderedPersonaName={SteamPersonaName ?? "<null>"}; RenderedAvatarUrl={SteamAvatarUrl ?? "<null>"}; DisplayName={appUser.DisplayName}; ClaimName={claimName}; ClaimAvatarUrl={claimAvatarUrl}",
            DiagnosticsSource,
            cancellationToken: cancellationToken);

        Input = new TradeUrlInputModel
        {
            TradeUrl = appUser.TradeUrl
        };
    }

    private async Task RefreshAuthCookieAsync(AppUser appUser, CancellationToken cancellationToken)
    {
        var displayName = string.IsNullOrWhiteSpace(appUser.PersonaName)
            ? appUser.DisplayName
            : appUser.PersonaName;
        var claims = new List<Claim>
        {
            new("AppUserId", appUser.Id.ToString()),
            new("SteamId", appUser.SteamId),
            new(ClaimTypes.NameIdentifier, appUser.SteamId),
            new(ClaimTypes.Name, displayName),
            new("IsAdmin", appUser.IsAdmin ? "true" : "false"),
            new("AvatarUrl", appUser.AvatarUrl ?? string.Empty)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
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
        return SteamTradeUrlUtility.IsValidTradeOfferUrl(tradeUrl);
    }

    public class TradeUrlInputModel
    {
        [StringLength(500)]
        public string? TradeUrl { get; set; }
    }

    public class GiftCodeInputModel
    {
        [StringLength(50)]
        public string? Code { get; set; }
    }
}
