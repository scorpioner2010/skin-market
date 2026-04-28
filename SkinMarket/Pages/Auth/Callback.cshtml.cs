using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Pages.Auth;

public class CallbackModel : PageModel
{
    private const string DiagnosticsSource = "AuthCallbackDiagnostics";
    private readonly ISteamOpenIdService _steamOpenIdService;
    private readonly ISteamProfileService _steamProfileService;
    private readonly IAppLogService _appLogService;
    private readonly AppDbContext _dbContext;
    private readonly AppRuntimeState _runtimeState;

    public CallbackModel(
        ISteamOpenIdService steamOpenIdService,
        ISteamProfileService steamProfileService,
        IAppLogService appLogService,
        AppDbContext dbContext,
        AppRuntimeState runtimeState)
    {
        _steamOpenIdService = steamOpenIdService;
        _steamProfileService = steamProfileService;
        _appLogService = appLogService;
        _dbContext = dbContext;
        _runtimeState = runtimeState;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            return RedirectToPage("/Index");
        }

        var steamId = await _steamOpenIdService.ValidateAndExtractSteamIdAsync(Request.Query, cancellationToken);
        if (string.IsNullOrWhiteSpace(steamId))
        {
            await _appLogService.WriteAsync(
                "Warning",
                "Auth callback did not resolve a SteamId from the OpenID response.",
                DiagnosticsSource,
                cancellationToken: cancellationToken);
            return RedirectToPage("/Profile");
        }

        var appUser = await _dbContext.AppUsers
            .SingleOrDefaultAsync(user => user.SteamId == steamId, cancellationToken);
        var existingUser = appUser is not null;

        if (appUser is null)
        {
            var newUser = new AppUser
            {
                Id = Guid.NewGuid(),
                SteamId = steamId,
                DisplayName = "Steam User",
                PersonaName = "Steam User",
                Balance = 0m,
                CreatedAtUtc = DateTime.UtcNow
            };

            _dbContext.AppUsers.Add(newUser);

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                appUser = newUser;
            }
            catch (DbUpdateException)
            {
                _dbContext.Entry(newUser).State = EntityState.Detached;
                appUser = await _dbContext.AppUsers
                    .SingleAsync(user => user.SteamId == steamId, cancellationToken);
            }
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Auth callback loaded user snapshot. SteamId={steamId}; ExistingUser={existingUser}; AppUserId={appUser.Id}; DisplayName={appUser.DisplayName}; PersonaName={appUser.PersonaName ?? "<null>"}; AvatarUrl={appUser.AvatarUrl ?? "<null>"}",
            DiagnosticsSource,
            cancellationToken: cancellationToken);

        var profileSummary = await _steamProfileService.GetProfileAsync(steamId, cancellationToken);
        await _appLogService.WriteAsync(
            "Info",
            profileSummary is null
                ? $"Auth callback received no Steam profile summary. SteamId={steamId}"
                : $"Auth callback received Steam profile summary. SteamId={steamId}; PersonaName={profileSummary.PersonaName}; AvatarUrl={profileSummary.AvatarFull ?? "<null>"}",
            DiagnosticsSource,
            cancellationToken: cancellationToken);

        if (profileSummary is not null)
        {
            appUser.PersonaName = profileSummary.PersonaName;
            appUser.AvatarUrl = profileSummary.AvatarFull;
            appUser.DisplayName = profileSummary.PersonaName;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _appLogService.WriteAsync(
                "Info",
                $"Auth callback persisted refreshed Steam profile. SteamId={steamId}; AppUserId={appUser.Id}; DisplayName={appUser.DisplayName}; PersonaName={appUser.PersonaName ?? "<null>"}; AvatarUrl={appUser.AvatarUrl ?? "<null>"}",
                DiagnosticsSource,
                cancellationToken: cancellationToken);
        }

        var displayName = string.IsNullOrWhiteSpace(appUser.PersonaName)
            ? appUser.DisplayName
            : appUser.PersonaName;

        var claims = new List<Claim>
        {
            new("AppUserId", appUser.Id.ToString()),
            new("SteamId", steamId),
            new(ClaimTypes.NameIdentifier, steamId),
            new(ClaimTypes.Name, displayName),
            new("IsAdmin", appUser.IsAdmin ? "true" : "false"),
            new("AvatarUrl", appUser.AvatarUrl ?? string.Empty)
        };

        await _appLogService.WriteAsync(
            "Info",
            $"Auth callback issuing auth cookie. SteamId={steamId}; ClaimName={displayName}; ClaimAvatarUrl={appUser.AvatarUrl ?? "<empty>"}; AppUserId={appUser.Id}",
            DiagnosticsSource,
            cancellationToken: cancellationToken);

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true
            });

        return RedirectToPage("/Profile");
    }
}
