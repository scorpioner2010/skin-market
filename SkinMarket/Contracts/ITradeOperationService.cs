using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ITradeOperationService
{
    Task<bool> HasExistingSaleAsync(Guid appUserId, GameType gameType, string assetId, CancellationToken cancellationToken = default);
    Task<bool> HasPendingSaleAsync(Guid appUserId, GameType gameType, string assetId, CancellationToken cancellationToken = default);
    Task CreatePendingSaleAsync(AppUser appUser, SteamInventoryItemDto item, CancellationToken cancellationToken = default);
    Task<Dictionary<string, TradeOperation>> GetLatestOperationsByAssetIdAsync(Guid appUserId, GameType gameType, CancellationToken cancellationToken = default);
    Task<List<TradeOperation>> GetRecentOperationsAsync(Guid appUserId, GameType gameType, int count, CancellationToken cancellationToken = default);
}
