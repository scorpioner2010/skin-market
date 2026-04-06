using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ISteamMarketPriceService
{
    Task<ResolvedItemPrice> ResolvePriceAsync(string? itemName, GameType gameType, CancellationToken cancellationToken = default);
    Task<PriceSourceResult> ProbePriceAsync(string? itemName, GameType gameType, CancellationToken cancellationToken = default);
    Task<ResolvedItemPrice> ResolvePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default);
    Task<PriceSourceResult> ProbePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default);
}
