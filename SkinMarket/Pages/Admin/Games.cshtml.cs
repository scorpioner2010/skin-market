using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SkinMarket.Pages.Admin;

public class GamesModel : PageModel
{
    public IReadOnlyList<AdminGameListItem> Games { get; private set; } = [];
    public AdminGameListItem? SelectedGame { get; private set; }

    public void OnGet(string? gameKey)
    {
        Games =
        [
            new AdminGameListItem("minefield", "Minefield", "API ready")
        ];

        if (!string.IsNullOrWhiteSpace(gameKey))
        {
            SelectedGame = Games.FirstOrDefault(game =>
                string.Equals(game.Key, gameKey, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed record AdminGameListItem(string Key, string Name, string Status);
}
