using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ISteamInventoryRefreshService
{
    Task<SteamInventorySnapshotResult?> GetLatestSnapshotAsync(
        string steamId,
        GameType gameType,
        CancellationToken cancellationToken = default);

    Task<SteamInventoryRefreshStatus> GetStatusAsync(
        string steamId,
        GameType gameType,
        CancellationToken cancellationToken = default);

    Task<SteamInventoryRefreshStatus> EnqueueRefreshAsync(
        string steamId,
        GameType gameType,
        SteamInventoryRefreshPriority priority,
        CancellationToken cancellationToken = default,
        bool forceFreshness = false,
        string? reason = null);
}
