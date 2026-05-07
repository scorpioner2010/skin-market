using SkinMarket.Contracts;

namespace SkinMarket.Services;

public class UsdOnlyFxRateService : IFxRateService
{
    public Task<FxRateResult> NormalizeToUsdAsync(decimal amount, string currency, CancellationToken cancellationToken = default)
    {
        if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new FxRateResult(true, amount, 1m, null, DateTime.UtcNow));
        }

        return Task.FromResult(new FxRateResult(
            false,
            null,
            null,
            $"FX conversion is not configured for {currency}.",
            DateTime.UtcNow));
    }
}
