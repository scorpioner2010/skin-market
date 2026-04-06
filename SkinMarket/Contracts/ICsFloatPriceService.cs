using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ICsFloatPriceService
{
    Task<PriceSourceResult> ProbePriceAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default);
}
