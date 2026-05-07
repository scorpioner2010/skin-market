using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IPriceDiagnosticLogService
{
    Task LogAsync(PriceDiagnosticEvent log, CancellationToken cancellationToken = default);

    Task LogProblemAsync(
        string eventType,
        string source,
        string failureReason,
        GameType? gameType = null,
        int? appId = null,
        string? marketHashName = null,
        string? assetId = null,
        int? httpStatusCode = null,
        string? endpoint = null,
        string? priceType = null,
        decimal? priceUsd = null,
        string? originalCurrency = null,
        decimal? confidenceScore = null,
        string? status = null,
        long? durationMs = null,
        string? detailsJson = null,
        CancellationToken cancellationToken = default);

    IReadOnlyList<PriceDiagnosticEvent> GetRecent(
        int limit = 100,
        string? source = null,
        string? status = null,
        string? eventType = null,
        string? marketHashName = null,
        int? gameType = null,
        DateTime? fromUtc = null);

    Task LogResolveStartedAsync(
        GameType gameType,
        int appId,
        string marketHashName,
        int snapshotCount,
        CancellationToken cancellationToken = default);

    Task LogSourceResultAsync(
        GameType gameType,
        int appId,
        string marketHashName,
        PriceSourceResult result,
        string eventType = "SourceFinished",
        int? httpStatusCode = null,
        string? endpoint = null,
        long? durationMs = null,
        string? detailsJson = null,
        CancellationToken cancellationToken = default);

    Task LogFinalSelectionAsync(
        GameType gameType,
        int appId,
        string marketHashName,
        ItemPriceResolutionResult result,
        CancellationToken cancellationToken = default);

    Task LogNoReliablePriceAsync(
        GameType gameType,
        int appId,
        string marketHashName,
        string failureReason,
        CancellationToken cancellationToken = default);

    Task LogMarketFallbackBlockedAsync(
        GameType? gameType,
        int? appId,
        string? marketHashName,
        string? assetId,
        string failureReason,
        CancellationToken cancellationToken = default);

    Task LogMarketBuyBlockedNoPriceAsync(
        GameType gameType,
        int appId,
        string? marketHashName,
        string? assetId,
        string failureReason,
        CancellationToken cancellationToken = default);
}
