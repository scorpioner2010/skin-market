using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IInventoryPriceRefreshService
{
    Task<Dictionary<string, ItemPriceResolutionResult>> GetCurrentPricesAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default);

    Task QueueRefreshAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default);

    Task<Dictionary<string, ItemPriceResolutionResult>> GetStatusAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default);
}
