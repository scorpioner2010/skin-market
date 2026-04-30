using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages;

public class GamesModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public GamesModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public IReadOnlyList<GameListItem> Games { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var minefieldEnabled = await _dbContext.MinefieldGameSettings
            .Where(settings => settings.GameKey == MinefieldGameSettingsDefaults.GameKey)
            .Select(settings => (bool?)settings.IsEnabled)
            .SingleOrDefaultAsync(cancellationToken);

        if (minefieldEnabled == false)
        {
            Games = [];
            return;
        }

        Games = new List<GameListItem>
        {
            new(
                "Minefield",
                "MF",
                "/games/minefield/icon.png",
                "/Games/Minefield",
                "Open safe rows, avoid mines, and claim the multiplier before the field ends.",
                "API ready")
        };
    }

    public sealed record GameListItem(
        string Name,
        string ShortName,
        string IconUrl,
        string PagePath,
        string Description,
        string Status);
}
