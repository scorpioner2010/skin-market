using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class GameCatalog : IGameCatalog
{
    private static readonly IReadOnlyList<GameDefinition> Games =
    [
        new GameDefinition
        {
            Type = GameType.CS2,
            Key = "cs2",
            DisplayName = "Counter-Strike 2",
            ShortName = "CS2",
            SteamAppId = 730,
            SteamContextId = 2,
            SupportsInventory = true,
            SupportsSteamMarketPricing = true,
            SupportsSkinportPricing = true
        },
        new GameDefinition
        {
            Type = GameType.Dota2,
            Key = "dota2",
            DisplayName = "Dota 2",
            ShortName = "Dota 2",
            SteamAppId = 570,
            SteamContextId = 2,
            SupportsInventory = true,
            SupportsSteamMarketPricing = false,
            SupportsSkinportPricing = false
        }
    ];

    public GameType DefaultGameType => GameType.CS2;

    public IReadOnlyList<GameDefinition> SupportedGames => Games;

    public GameDefinition Get(GameType gameType)
    {
        return Games.FirstOrDefault(item => item.Type == gameType)
               ?? Games.First(item => item.Type == DefaultGameType);
    }
}
