using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IGameCatalog
{
    GameType DefaultGameType { get; }
    IReadOnlyList<GameDefinition> SupportedGames { get; }
    GameDefinition Get(GameType gameType);
}
