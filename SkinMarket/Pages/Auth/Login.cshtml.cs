using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkinMarket.Contracts;

namespace SkinMarket.Pages.Auth;

public class LoginModel : PageModel
{
    private readonly ISteamOpenIdService _steamOpenIdService;

    public LoginModel(ISteamOpenIdService steamOpenIdService)
    {
        _steamOpenIdService = steamOpenIdService;
    }

    public IActionResult OnGet()
    {
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
