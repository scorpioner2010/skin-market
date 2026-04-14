using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ISteamBotInventoryClient
{
    Task<SteamInventoryResultDto> GetInventoryAsync(string steamId, GameType gameType, CancellationToken cancellationToken = default);
}
