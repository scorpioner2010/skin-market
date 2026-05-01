using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages;

public class GamesModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public GamesModel(AppDbContext dbContext, IStringLocalizer<SharedResource> localizer)
    {
        _dbContext = dbContext;
        _localizer = localizer;
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
                _localizer["Game_Minefield_Description"].Value,
                _localizer["Game_Minefield_Status"].Value)
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
