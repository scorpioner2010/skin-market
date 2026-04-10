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
    private readonly ISteamOpenIdService _steamOpenIdService;
    private readonly ISteamProfileService _steamProfileService;
    private readonly AppDbContext _dbContext;
    private readonly AppRuntimeState _runtimeState;

    public CallbackModel(
        ISteamOpenIdService steamOpenIdService,
        ISteamProfileService steamProfileService,
        AppDbContext dbContext,
        AppRuntimeState runtimeState)
    {
        _steamOpenIdService = steamOpenIdService;
        _steamProfileService = steamProfileService;
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
            return RedirectToPage("/Profile");
        }

        var appUser = await _dbContext.AppUsers
            .SingleOrDefaultAsync(user => user.SteamId == steamId, cancellationToken);

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

        var profileSummary = await _steamProfileService.GetProfileAsync(steamId, cancellationToken);
        if (profileSummary is not null)
        {
            appUser.PersonaName = profileSummary.PersonaName;
            appUser.AvatarUrl = profileSummary.AvatarFull;
            appUser.DisplayName = profileSummary.PersonaName;
            await _dbContext.SaveChangesAsync(cancellationToken);
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
            new("AvatarUrl", appUser.AvatarUrl ?? string.Empty)
        };

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
