using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IItemPriceResolver
{
    Task<ItemPriceResolutionResult> ResolveAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default);
    Task<ItemPriceResolutionResult> ResolveAsync(TradeOperation operation, CancellationToken cancellationToken = default);
    Task<ItemPriceResolutionResult> ResolveAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default);
    Task<ItemPriceResolutionResult> GetCachedAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default);
    Task<Dictionary<string, ItemPriceResolutionResult>> GetCachedAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default);
    Task<Dictionary<string, ItemPriceResolutionResult>> ResolveInventoryPricesAsync(
        IReadOnlyCollection<SteamInventoryItemDto> items,
        GameType gameType,
        CancellationToken cancellationToken = default);
}
