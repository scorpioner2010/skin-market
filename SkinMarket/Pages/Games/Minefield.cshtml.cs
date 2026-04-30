using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages.Games;

public class MinefieldModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public MinefieldModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var isEnabled = await _dbContext.MinefieldGameSettings
            .Where(settings => settings.GameKey == MinefieldGameSettingsDefaults.GameKey)
            .Select(settings => (bool?)settings.IsEnabled)
            .SingleOrDefaultAsync(cancellationToken);

        return isEnabled == false ? RedirectToPage("/Games") : Page();
    }
}
