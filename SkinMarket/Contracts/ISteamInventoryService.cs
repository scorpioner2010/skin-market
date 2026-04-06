using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ISteamInventoryService
{
    Task<SteamInventoryResultDto> GetInventoryAsync(string steamId, GameType gameType, CancellationToken cancellationToken = default);
}
