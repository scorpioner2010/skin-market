using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class PriceDiagnosticLogService : IPriceDiagnosticLogService
{
    private const int MaxTextLength = 4000;
    private const int DefaultPageLimit = 100;
    private const int MaxFileScanMultiplier = 100;
    private const int MaxFileScanLines = 10000;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _fileWriteLock = new(1, 1);
    private readonly LinkedList<PriceDiagnosticEvent> _events = new();
    private readonly PricingOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<PriceDiagnosticLogService> _logger;
    private DateTime _rateLimitWindowUtc = DateTime.UtcNow;
    private int _rateLimitCount;

    public PriceDiagnosticLogService(
        IOptions<PricingOptions> options,
        IWebHostEnvironment environment,
        ILogger<PriceDiagnosticLogService> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public Task LogAsync(PriceDiagnosticEvent log, CancellationToken cancellationToken = default)
    {
        if (!_options.EnablePriceProblemDiagnostics)
        {
            return Task.CompletedTask;
        }

        Normalize(log);
        if (!ShouldPersist(log) || IsNoisyCachedSnapshotLog(log) || !TryConsumeRateLimit(log))
        {
            return Task.CompletedTask;
        }

        AddToMemory(log);

        if (!_options.PriceDiagnosticsFileEnabled)
        {
            return Task.CompletedTask;
        }

        var fileLog = Clone(log);
        _ = Task.Run(() => WriteFileSafeAsync(fileLog, CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task LogProblemAsync(
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
        CancellationToken cancellationToken = default)
    {
        return LogAsync(new PriceDiagnosticEvent
        {
            Level = "Warning",
            EventType = eventType,
            GameType = gameType.HasValue ? (int)gameType.Value : null,
            AppId = appId,
            MarketHashName = marketHashName,
            AssetId = assetId,
            Source = source,
            PriceType = priceType,
            Status = status,
            PriceUsd = priceUsd,
            OriginalCurrency = originalCurrency,
            ConfidenceScore = confidenceScore,
            HttpStatusCode = httpStatusCode,
            Endpoint = endpoint,
            DurationMs = durationMs,
            FailureReason = failureReason,
            DetailsJson = detailsJson
        }, cancellationToken);
    }

    public IReadOnlyList<PriceDiagnosticEvent> GetRecent(
        int limit = 100,
        string? source = null,
        string? status = null,
        string? eventType = null,
        string? marketHashName = null,
        int? gameType = null,
        DateTime? fromUtc = null)
    {
        var take = limit <= 0 ? DefaultPageLimit : Math.Min(limit, Math.Max(1, _options.PriceDiagnosticsMemoryLimit));
        PriceDiagnosticEvent[] snapshot;
        lock (_sync)
        {
            snapshot = _events.ToArray();
        }

        var events = snapshot
            .Concat(ReadRecentFileEvents(take, fromUtc))
            .Where(item => !IsNoisyCachedSnapshotLog(item))
            .GroupBy(item => item.Id)
            .Select(group => group
                .OrderByDescending(item => item.CreatedAtUtc)
                .First())
            .ToList();

        IEnumerable<PriceDiagnosticEvent> query = events;
        if (fromUtc.HasValue)
        {
            query = query.Where(item => item.CreatedAtUtc >= fromUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(source) && !string.Equals(source, "all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item => string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item =>
                string.Equals(item.Status, status, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.EventType, status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(eventType) && !string.Equals(eventType, "all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item => string.Equals(item.EventType, eventType, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(marketHashName))
        {
            var normalizedSearch = MarketHashNameUtility.Normalize(marketHashName) ?? marketHashName.Trim();
            query = query.Where(item =>
                (item.MarketHashName?.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.NormalizedMarketHashName?.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (gameType.HasValue)
        {
            query = query.Where(item => item.GameType == gameType.Value);
        }

        return query
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(take)
            .Select(Clone)
            .ToList();
    }

    public Task LogResolveStartedAsync(
        GameType gameType,
        int appId,
        string marketHashName,
        int snapshotCount,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(new PriceDiagnosticEvent
        {
            Level = "Info",
            EventType = "ResolveStarted",
            GameType = (int)gameType,
            AppId = appId,
            MarketHashName = marketHashName,
            DetailsJson = $$"""{"snapshotCount":{{snapshotCount}}}"""
        }, cancellationToken);
    }

    public Task LogSourceResultAsync(
        GameType gameType,
        int appId,
        string marketHashName,
        PriceSourceResult result,
        string eventType = "SourceFinished",
        int? httpStatusCode = null,
        string? endpoint = null,
        long? durationMs = null,
        string? detailsJson = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedEventType = result.Success
            ? eventType
            : string.Equals(result.Status, "RateLimited", StringComparison.OrdinalIgnoreCase) ? "SourceRateLimited" :
            (result.FailureReason?.Contains("timeout", StringComparison.OrdinalIgnoreCase) ?? false) ? "SourceTimeout" :
            "SourceFailed";

        return LogAsync(new PriceDiagnosticEvent
        {
            Level = result.Success ? "Info" : ResolveFailureLevel(result),
            EventType = resolvedEventType,
            GameType = (int)gameType,
            AppId = appId,
            MarketHashName = marketHashName,
            Source = result.Source,
            PriceType = result.PriceType,
            Status = result.Status,
            PriceUsd = result.Price,
            OriginalPrice = result.OriginalPrice,
            OriginalCurrency = result.OriginalCurrency ?? result.Currency,
            FxRate = result.FxRate,
            ConfidenceScore = result.ConfidenceScore,
            IsEstimated = result.IsEstimated,
            IsCached = result.IsCached,
            IsStale = result.IsStale,
            HttpStatusCode = httpStatusCode,
            Endpoint = endpoint,
            DurationMs = durationMs,
            FailureReason = result.FailureReason,
            DetailsJson = detailsJson ?? result.ProvenanceJson
        }, cancellationToken);
    }

    public Task LogFinalSelectionAsync(
        GameType gameType,
        int appId,
        string marketHashName,
        ItemPriceResolutionResult result,
        CancellationToken cancellationToken = default)
    {
        if (!result.HasPrice && !_options.EnableVerbosePriceDiagnostics)
        {
            return Task.CompletedTask;
        }

        return LogAsync(new PriceDiagnosticEvent
        {
            Level = result.HasPrice ? "Info" : "Warning",
            EventType = result.HasPrice ? "FinalPriceSelected" : "NoReliablePrice",
            GameType = (int)gameType,
            AppId = appId,
            MarketHashName = marketHashName,
            Source = result.Source,
            PriceType = result.PriceType,
            Status = result.Status,
            PriceUsd = result.Price,
            OriginalPrice = result.OriginalPrice,
            OriginalCurrency = result.OriginalCurrency,
            FxRate = result.FxRate,
            ConfidenceScore = result.ConfidenceScore,
            IsEstimated = result.IsEstimated,
            IsCached = result.IsCached,
            IsStale = result.IsStale,
            FailureReason = result.FailureReason
        }, cancellationToken);
    }

    public Task LogNoReliablePriceAsync(
        GameType gameType,
        int appId,
        string marketHashName,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(new PriceDiagnosticEvent
        {
            Level = "Warning",
            EventType = "NoReliablePrice",
            GameType = (int)gameType,
            AppId = appId,
            MarketHashName = marketHashName,
            Source = PriceSourceNames.Unavailable,
            PriceType = PriceTypeNames.Unavailable,
            Status = "Unavailable",
            ConfidenceScore = 0m,
            FailureReason = failureReason
        }, cancellationToken);
    }

    public Task LogMarketFallbackBlockedAsync(
        GameType? gameType,
        int? appId,
        string? marketHashName,
        string? assetId,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(new PriceDiagnosticEvent
        {
            Level = "Warning",
            EventType = "FallbackBlocked",
            GameType = gameType.HasValue ? (int)gameType.Value : null,
            AppId = appId,
            MarketHashName = marketHashName,
            AssetId = assetId,
            Source = PriceSourceNames.Unavailable,
            PriceType = PriceTypeNames.Unavailable,
            Status = "Unavailable",
            FailureReason = failureReason
        }, cancellationToken);
    }

    public Task LogMarketBuyBlockedNoPriceAsync(
        GameType gameType,
        int appId,
        string? marketHashName,
        string? assetId,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        return LogAsync(new PriceDiagnosticEvent
        {
            Level = "Warning",
            EventType = "MarketBuyBlockedNoPrice",
            GameType = (int)gameType,
            AppId = appId,
            MarketHashName = marketHashName,
            AssetId = assetId,
            Source = PriceSourceNames.Unavailable,
            PriceType = PriceTypeNames.Unavailable,
            Status = "Unavailable",
            FailureReason = failureReason
        }, cancellationToken);
    }

    private void AddToMemory(PriceDiagnosticEvent log)
    {
        var max = Math.Max(1, _options.PriceDiagnosticsMemoryLimit);
        lock (_sync)
        {
            _events.AddLast(Clone(log));
            while (_events.Count > max)
            {
                _events.RemoveFirst();
            }
        }
    }

    private async Task WriteFileAsync(PriceDiagnosticEvent log, CancellationToken cancellationToken)
    {
        var directory = ResolveDirectory();
        Directory.CreateDirectory(directory);
        PruneOldFiles(directory);
        var filePath = ResolveCurrentFilePath(directory);
        var json = JsonSerializer.Serialize(log);
        await File.AppendAllTextAsync(filePath, json + Environment.NewLine, cancellationToken);
    }

    private async Task WriteFileSafeAsync(PriceDiagnosticEvent log, CancellationToken cancellationToken)
    {
        if (!_options.PriceDiagnosticsFileEnabled)
        {
            return;
        }

        try
        {
            await _fileWriteLock.WaitAsync(cancellationToken);
            try
            {
                await WriteFileAsync(log, cancellationToken);
            }
            finally
            {
                _fileWriteLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to write price diagnostic problem event {EventType}.", log.EventType);
        }
    }

    private IReadOnlyList<PriceDiagnosticEvent> ReadRecentFileEvents(int limit, DateTime? fromUtc)
    {
        if (!_options.PriceDiagnosticsFileEnabled)
        {
            return [];
        }

        var directory = ResolveDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var scanLimit = Math.Max(limit, Math.Min(MaxFileScanLines, limit * MaxFileScanMultiplier));
        var results = new List<PriceDiagnosticEvent>(Math.Min(scanLimit, Math.Max(limit, 1)));

        foreach (var file in EnumerateRecentLogFiles(directory))
        {
            foreach (var line in ReadRecentLines(file.FullName, scanLimit))
            {
                var item = DeserializeEvent(line);
                if (item is null)
                {
                    continue;
                }

                if (fromUtc.HasValue && item.CreatedAtUtc < fromUtc.Value)
                {
                    continue;
                }

                if (IsNoisyCachedSnapshotLog(item))
                {
                    continue;
                }

                results.Add(item);
                if (results.Count >= scanLimit)
                {
                    return results;
                }
            }
        }

        return results;
    }

    private IEnumerable<FileInfo> EnumerateRecentLogFiles(string directory)
    {
        var maxFiles = Math.Max(1, _options.PriceDiagnosticsMaxFiles);
        var problemFiles = Directory
            .EnumerateFiles(directory, "price-problems-*.log")
            .Concat(Directory.EnumerateFiles(directory, "price-diagnostics-*.log"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileInfo(path));

        return problemFiles
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(maxFiles);
    }

    private IEnumerable<string> ReadRecentLines(string filePath, int maxLines)
    {
        if (maxLines <= 0)
        {
            yield break;
        }

        string[] lines;
        try
        {
            lines = ReadRecentLinesCore(filePath, maxLines);
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception, "Failed to read price diagnostic log file {FilePath}.", filePath);
            yield break;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogDebug(exception, "Access denied reading price diagnostic log file {FilePath}.", filePath);
            yield break;
        }

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            yield return lines[index];
        }
    }

    private static string[] ReadRecentLinesCore(string filePath, int maxLines)
    {
        const int bufferSize = 8192;
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            return [];
        }

        var buffer = new byte[bufferSize];
        var tail = new StringBuilder();
        var position = stream.Length;
        var newlineCount = 0;

        while (position > 0 && newlineCount <= maxLines)
        {
            var bytesToRead = (int)Math.Min(bufferSize, position);
            position -= bytesToRead;
            stream.Seek(position, SeekOrigin.Begin);
            var read = stream.Read(buffer, 0, bytesToRead);
            var chunk = Encoding.UTF8.GetString(buffer, 0, read);
            newlineCount += chunk.Count(character => character == '\n');
            tail.Insert(0, chunk);
        }

        return tail
            .ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(maxLines)
            .ToArray();
    }

    private PriceDiagnosticEvent? DeserializeEvent(string line)
    {
        try
        {
            var item = JsonSerializer.Deserialize<PriceDiagnosticEvent>(line);
            if (item is null)
            {
                return null;
            }

            Normalize(item);
            return item;
        }
        catch (JsonException exception)
        {
            _logger.LogDebug(exception, "Failed to parse price diagnostic log line.");
            return null;
        }
    }

    private string ResolveDirectory()
    {
        var configured = string.IsNullOrWhiteSpace(_options.PriceDiagnosticsDirectory)
            ? "logs/prices"
            : _options.PriceDiagnosticsDirectory.Trim();
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(_environment.ContentRootPath, configured);
    }

    private string ResolveCurrentFilePath(string directory)
    {
        var baseName = $"price-problems-{DateTime.UtcNow:yyyy-MM-dd}";
        var maxBytes = Math.Max(1, _options.PriceDiagnosticsMaxFileSizeMb) * 1024L * 1024L;
        var filePath = Path.Combine(directory, $"{baseName}.log");
        if (!File.Exists(filePath) || new FileInfo(filePath).Length < maxBytes)
        {
            return filePath;
        }

        for (var index = 1; index < 100; index++)
        {
            var candidate = Path.Combine(directory, $"{baseName}.{index}.log");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length < maxBytes)
            {
                return candidate;
            }
        }

        return filePath;
    }

    private void PruneOldFiles(string directory)
    {
        var maxFiles = Math.Max(1, _options.PriceDiagnosticsMaxFiles);
        var files = Directory.GetFiles(directory, "price-problems-*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(maxFiles)
            .ToList();
        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to prune old price diagnostic log file {FilePath}.", file.FullName);
            }
        }
    }

    private static string ResolveFailureLevel(PriceSourceResult result)
    {
        return string.Equals(result.Status, "RateLimited", StringComparison.OrdinalIgnoreCase) ||
               (result.FailureReason?.Contains("timeout", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (result.FailureReason?.Contains("429", StringComparison.OrdinalIgnoreCase) ?? false)
            ? "Warning"
            : "Info";
    }

    private bool ShouldPersist(PriceDiagnosticEvent log)
    {
        if (_options.EnableVerbosePriceDiagnostics)
        {
            return true;
        }

        return log.EventType is
            "NoReliablePrice" or
            "SourceFailed" or
            "SourceNotFound" or
            "SourceReturnedNoUsablePrice" or
            "SourceRateLimited" or
            "SourceSkipped" or
            "SourceTimeout" or
            "SourceRejected" or
            "CurrencyMismatch" or
            "ParseFailed" or
            "ExternalApiError" or
            "FinalResolutionFailed";
    }

    private static bool IsNoisyCachedSnapshotLog(PriceDiagnosticEvent log)
    {
        if (!string.Equals(log.Source, "Resolver", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return log.FailureReason?.Contains("Cached price snapshot is stale", StringComparison.OrdinalIgnoreCase) == true ||
               log.FailureReason?.Contains("Only estimated cached price", StringComparison.OrdinalIgnoreCase) == true ||
               log.FailureReason?.Contains("No cached price snapshot exists", StringComparison.OrdinalIgnoreCase) == true ||
               log.FailureReason?.Contains("Cached snapshots exist but none are reliable", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool TryConsumeRateLimit(PriceDiagnosticEvent log)
    {
        if (log.EventType is "FinalResolutionFailed" or "NoReliablePrice")
        {
            return true;
        }

        var maxPerMinute = Math.Max(1, _options.PriceDiagnosticsMaxLogsPerMinute);
        var now = DateTime.UtcNow;
        lock (_sync)
        {
            if (now - _rateLimitWindowUtc >= TimeSpan.FromMinutes(1))
            {
                _rateLimitWindowUtc = now;
                _rateLimitCount = 0;
            }

            if (_rateLimitCount >= maxPerMinute)
            {
                return false;
            }

            _rateLimitCount++;
            return true;
        }
    }

    private static void Normalize(PriceDiagnosticEvent log)
    {
        log.Id = log.Id == Guid.Empty ? Guid.NewGuid() : log.Id;
        log.CreatedAtUtc = log.CreatedAtUtc == default ? DateTime.UtcNow : log.CreatedAtUtc;
        log.Level = Normalize(log.Level, "Info", 20);
        log.EventType = Normalize(log.EventType, "Unknown", 80);
        log.MarketHashName = TrimOrNull(log.MarketHashName, 300);
        log.NormalizedMarketHashName = TrimOrNull(
            MarketHashNameUtility.Normalize(log.NormalizedMarketHashName) ??
            MarketHashNameUtility.Normalize(log.MarketHashName),
            300);
        log.AssetId = TrimOrNull(log.AssetId, 100);
        log.Source = TrimOrNull(log.Source, 50);
        log.PriceType = TrimOrNull(log.PriceType, 50);
        log.Status = TrimOrNull(log.Status, 50);
        log.OriginalCurrency = TrimOrNull(log.OriginalCurrency, 10);
        log.Endpoint = TrimOrNull(log.Endpoint, 500);
        log.FailureReason = TrimOrNull(log.FailureReason, 1000);
        log.DetailsJson = TrimOrNull(log.DetailsJson, MaxTextLength);
    }

    private static PriceDiagnosticEvent Clone(PriceDiagnosticEvent item)
    {
        return new PriceDiagnosticEvent
        {
            Id = item.Id,
            CreatedAtUtc = item.CreatedAtUtc,
            Level = item.Level,
            EventType = item.EventType,
            GameType = item.GameType,
            AppId = item.AppId,
            MarketHashName = item.MarketHashName,
            NormalizedMarketHashName = item.NormalizedMarketHashName,
            AssetId = item.AssetId,
            Source = item.Source,
            PriceType = item.PriceType,
            Status = item.Status,
            PriceUsd = item.PriceUsd,
            OriginalPrice = item.OriginalPrice,
            OriginalCurrency = item.OriginalCurrency,
            FxRate = item.FxRate,
            ConfidenceScore = item.ConfidenceScore,
            IsEstimated = item.IsEstimated,
            IsCached = item.IsCached,
            IsStale = item.IsStale,
            HttpStatusCode = item.HttpStatusCode,
            Endpoint = item.Endpoint,
            DurationMs = item.DurationMs,
            FailureReason = item.FailureReason,
            DetailsJson = item.DetailsJson
        };
    }

    private static string Normalize(string? value, string fallback, int maxLength)
    {
        return TrimOrNull(value, maxLength) ?? fallback;
    }

    private static string? TrimOrNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
