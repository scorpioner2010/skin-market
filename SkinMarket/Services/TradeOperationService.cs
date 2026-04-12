using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class TradeOperationService : ITradeOperationService
{
    private readonly AppDbContext _dbContext;
    private readonly IGameCatalog _gameCatalog;

    public TradeOperationService(AppDbContext dbContext, IGameCatalog gameCatalog)
    {
        _dbContext = dbContext;
        _gameCatalog = gameCatalog;
    }

    public Task<bool> HasExistingSaleAsync(Guid appUserId, string assetId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TradeOperations.AnyAsync(
            operation => operation.AppUserId == appUserId &&
                         operation.AssetId == assetId &&
                         operation.Status != "Failed",
            cancellationToken);
    }

    public Task<bool> HasPendingSaleAsync(Guid appUserId, string assetId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TradeOperations.AnyAsync(
            operation => operation.AppUserId == appUserId &&
                         operation.AssetId == assetId &&
                         operation.Status == "Pending",
            cancellationToken);
    }

    public async Task CreatePendingSaleAsync(AppUser appUser, SteamInventoryItemDto item, CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(_gameCatalog.DefaultGameType);
        var operation = new TradeOperation
        {
            Id = Guid.NewGuid(),
            AppUserId = appUser.Id,
            SteamId = appUser.SteamId,
            AssetId = item.AssetId,
            ClassId = item.ClassId,
            InstanceId = item.InstanceId,
            AppId = game.SteamAppId,
            ContextId = game.SteamContextId.ToString(),
            ItemName = string.IsNullOrWhiteSpace(item.Name) ? "Unknown Item" : item.Name,
            MarketHashName = MarketHashNameUtility.ResolvePrimary(item),
            IconUrl = item.IconUrl,
            Status = "Pending",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _dbContext.TradeOperations.Add(operation);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Dictionary<string, TradeOperation>> GetLatestOperationsByAssetIdAsync(Guid appUserId, CancellationToken cancellationToken = default)
    {
        var operations = await _dbContext.TradeOperations
            .AsNoTracking()
            .Where(operation => operation.AppUserId == appUserId)
            .OrderByDescending(operation => operation.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return operations
            .GroupBy(operation => operation.AssetId)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    public Task<List<TradeOperation>> GetRecentOperationsAsync(Guid appUserId, int count, CancellationToken cancellationToken = default)
    {
        return _dbContext.TradeOperations
            .AsNoTracking()
            .Where(operation => operation.AppUserId == appUserId)
            .OrderByDescending(operation => operation.CreatedAtUtc)
            .Take(count)
            .ToListAsync(cancellationToken);
    }
}
