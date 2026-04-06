using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ITradeOperationService
{
    Task<bool> HasExistingSaleAsync(Guid appUserId, string assetId, CancellationToken cancellationToken = default);
    Task<bool> HasPendingSaleAsync(Guid appUserId, string assetId, CancellationToken cancellationToken = default);
    Task CreatePendingSaleAsync(AppUser appUser, SteamInventoryItemDto item, CancellationToken cancellationToken = default);
    Task<Dictionary<string, TradeOperation>> GetLatestOperationsByAssetIdAsync(Guid appUserId, CancellationToken cancellationToken = default);
    Task<List<TradeOperation>> GetRecentOperationsAsync(Guid appUserId, int count, CancellationToken cancellationToken = default);
}
