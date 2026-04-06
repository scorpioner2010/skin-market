using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;

namespace SkinMarket.Pages.Auth;

public class LoginModel : PageModel
{
    private readonly ISteamOpenIdService _steamOpenIdService;
    private readonly AppRuntimeState _runtimeState;

    public LoginModel(ISteamOpenIdService steamOpenIdService, AppRuntimeState runtimeState)
    {
        _steamOpenIdService = steamOpenIdService;
        _runtimeState = runtimeState;
    }

    public IActionResult OnGet()
    {
        if (_runtimeState.IsDegradedMode)
        {
            return RedirectToPage("/Index");
        }

        var callbackUrl = Url.Page(
            "/Auth/Callback",
            pageHandler: null,
            values: null,
            protocol: Request.Scheme,
            host: Request.Host.Value);

        if (string.IsNullOrWhiteSpace(callbackUrl))
        {
            return RedirectToPage("/Index");
        }

        var loginUrl = _steamOpenIdService.BuildLoginUrl(callbackUrl);
        return Redirect(loginUrl);
    }
}
