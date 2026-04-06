using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ISkinportPricingService
{
    Task<IReadOnlyDictionary<string, SkinportItemDto>> GetPriceMapAsync(GameType gameType, CancellationToken cancellationToken = default);
    Task<ResolvedItemPrice> ResolvePriceAsync(string? itemName, GameType gameType, CancellationToken cancellationToken = default);
    Task<PriceSourceResult> ProbePriceAsync(string? itemName, GameType gameType, CancellationToken cancellationToken = default);
    Task<ResolvedItemPrice> ResolvePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default);
    Task<PriceSourceResult> ProbePriceAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default);
}
