using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SkinMarket.Pages;

public class GamesModel : PageModel
{
    public IReadOnlyList<GameListItem> Games { get; private set; } = [];

    public void OnGet()
    {
        Games =
        [
            new GameListItem(
                "Minefield",
                "MF",
                "Open safe rows, avoid mines, and claim the multiplier before the field ends.",
                "API ready")
        ];
    }

    public sealed record GameListItem(string Name, string ShortName, string Description, string Status);
}
