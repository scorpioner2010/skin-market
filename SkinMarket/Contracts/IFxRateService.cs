namespace SkinMarket.Contracts;

public interface IFxRateService
{
    Task<FxRateResult> NormalizeToUsdAsync(decimal amount, string currency, CancellationToken cancellationToken = default);
}

public sealed record FxRateResult(
    bool Success,
    decimal? PriceUsd,
    decimal? FxRate,
    string? FailureReason,
    DateTime? ObservedAtUtc);
