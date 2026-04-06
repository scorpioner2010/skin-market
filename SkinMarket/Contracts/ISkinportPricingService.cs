using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ISkinportPricingService
{
    Task<PriceSourceResult> ProbePriceAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, SkinportOutOfStockItemDto>> GetOutOfStockPriceMapAsync(GameType gameType, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, SkinportSalesHistoryDto>> GetSalesHistoryAsync(
        IReadOnlyCollection<string> marketHashNames,
        GameType gameType,
        CancellationToken cancellationToken = default);
}
