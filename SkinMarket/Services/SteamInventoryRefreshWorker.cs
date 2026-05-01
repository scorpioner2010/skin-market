using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamInventoryRefreshWorker : BackgroundService, ISteamInventoryRefreshService
{
    private const string IconBaseUrl = "https://community.akamai.steamstatic.com/economy/image/";
    private const int BodySnippetLength = 300;
    private const int MaxInventoryPages = 50;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGameCatalog _gameCatalog;
    private readonly IAppLogService _appLogService;
    private readonly ILogger<SteamInventoryRefreshWorker> _logger;
    private readonly SteamInventoryRefreshOptions _options;
    private readonly object _queueLock = new();
    private readonly PriorityQueue<SteamInventoryRefreshJob, (int Priority, long Sequence)> _queue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly ConcurrentDictionary<string, SteamInventoryRefreshPriority> _queuedKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _steamRequestDelayLock = new(1, 1);
    private readonly object _globalRateLimitLock = new();
    private DateTime? _lastSteamInventoryRequestUtc;
    private DateTime? _globalNextAllowedRefreshUtc;
    private int _globalRateLimitStrikeCount;
    private long _sequence;

    public SteamInventoryRefreshWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IGameCatalog gameCatalog,
        IAppLogService appLogService,
        IOptions<SteamInventoryRefreshOptions> options,
        ILogger<SteamInventoryRefreshWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _gameCatalog = gameCatalog;
        _appLogService = appLogService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SteamInventorySnapshotResult?> GetLatestSnapshotAsync(
        string steamId,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalizedSteamId = NormalizeSteamId(steamId);
        var snapshot = await dbContext.SteamInventorySnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.SteamId == normalizedSteamId && item.GameType == gameType,
                cancellationToken);

        if (snapshot?.LastSuccessRefreshUtc is null)
        {
            return null;
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<SteamInventoryItemDto>>(snapshot.ItemsJson, SerializerOptions) ?? [];
            foreach (var item in items)
            {
                item.GameType = gameType;
            }

            return new SteamInventorySnapshotResult
            {
                LastSuccessRefreshUtc = snapshot.LastSuccessRefreshUtc.Value,
                Items = items
            };
        }
        catch (JsonException exception)
        {
            await _appLogService.WriteAsync(
                "Error",
                $"Inventory snapshot JSON is invalid. SteamId={normalizedSteamId}; GameType={(int)gameType}; Message={exception.Message}",
                nameof(SteamInventoryRefreshWorker),
                exception,
                CancellationToken.None);
            return null;
        }
    }

    public async Task<SteamInventoryRefreshStatus> GetStatusAsync(
        string steamId,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalizedSteamId = NormalizeSteamId(steamId);
        var snapshot = await dbContext.SteamInventorySnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.SteamId == normalizedSteamId && item.GameType == gameType,
                cancellationToken);

        var effectiveNextAllowedUtc = await GetEffectiveNextAllowedUtcAsync(dbContext, snapshot, cancellationToken);
        return BuildStatus(snapshot, BuildKey(normalizedSteamId, gameType), effectiveNextAllowedUtc);
    }

    public async Task<SteamInventoryRefreshStatus> EnqueueRefreshAsync(
        string steamId,
        GameType gameType,
        SteamInventoryRefreshPriority priority,
        CancellationToken cancellationToken = default)
    {
        var normalizedSteamId = NormalizeSteamId(steamId);
        var key = BuildKey(normalizedSteamId, gameType);
        var refreshLock = _refreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var snapshot = await GetOrCreateSnapshotAsync(dbContext, normalizedSteamId, gameType, cancellationToken);
            var now = DateTime.UtcNow;
            var effectiveNextAllowedUtc = await GetEffectiveNextAllowedUtcAsync(dbContext, snapshot, cancellationToken);
            if (dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (effectiveNextAllowedUtc is not null && effectiveNextAllowedUtc.Value > now)
            {
                await WriteRefreshLogAsync(
                    "Info",
                    "SkipEnqueue",
                    normalizedSteamId,
                    gameType,
                    priority,
                    reason: "CooldownActive",
                    snapshot: snapshot,
                    nextAllowedRefreshUtc: effectiveNextAllowedUtc,
                    cancellationToken: CancellationToken.None);
                return BuildStatus(snapshot, key, effectiveNextAllowedUtc);
            }

            if (snapshot.RefreshInProgress && !_queuedKeys.ContainsKey(key))
            {
                await WriteRefreshLogAsync(
                    "Info",
                    "SkipEnqueue",
                    normalizedSteamId,
                    gameType,
                    priority,
                    reason: "RefreshInProgress",
                    snapshot: snapshot,
                    cancellationToken: CancellationToken.None);
                return BuildStatus(snapshot, key, effectiveNextAllowedUtc);
            }

            if (_queuedKeys.TryGetValue(key, out var existingPriority))
            {
                if (priority < existingPriority &&
                    _queuedKeys.TryUpdate(key, priority, existingPriority))
                {
                    EnqueueJob(new SteamInventoryRefreshJob(normalizedSteamId, gameType, priority));
                    await WriteRefreshLogAsync(
                        "Info",
                        "PriorityUpgraded",
                        normalizedSteamId,
                        gameType,
                        priority,
                        reason: $"PreviousPriority={existingPriority}",
                        snapshot: snapshot,
                        cancellationToken: CancellationToken.None);
                }
                else
                {
                    await WriteRefreshLogAsync(
                        "Info",
                        "SkipEnqueue",
                        normalizedSteamId,
                        gameType,
                        priority,
                        reason: $"DuplicateQueued; ExistingPriority={existingPriority}",
                        snapshot: snapshot,
                        cancellationToken: CancellationToken.None);
                }

                return BuildStatus(snapshot, key, effectiveNextAllowedUtc);
            }

            snapshot.RefreshInProgress = true;
            await dbContext.SaveChangesAsync(cancellationToken);

            _queuedKeys[key] = priority;
            EnqueueJob(new SteamInventoryRefreshJob(normalizedSteamId, gameType, priority));
            await WriteRefreshLogAsync(
                "Info",
                "EnqueueJob",
                normalizedSteamId,
                gameType,
                priority,
                reason: "Queued",
                snapshot: snapshot,
                cancellationToken: CancellationToken.None);

            return BuildStatus(snapshot, key, effectiveNextAllowedUtc);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStaleInProgressFlagsAsync(stoppingToken);
        await HydrateGlobalRateLimitFromSnapshotsAsync(stoppingToken);
        await _appLogService.WriteAsync(
            "Info",
            $"Steam inventory refresh worker started. Concurrency=1; DelayBetweenSteamRequestsSeconds={Math.Max(1, _options.DelayBetweenSteamRequestsSeconds)}",
            nameof(SteamInventoryRefreshWorker),
            cancellationToken: CancellationToken.None);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(stoppingToken);
                if (!TryDequeueJob(out var job))
                {
                    continue;
                }

                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Steam inventory refresh worker loop failed.");
                await _appLogService.WriteAsync(
                    "Error",
                    $"Steam inventory refresh worker loop failed. ExceptionType={exception.GetType().Name}; Message={exception.Message}",
                    nameof(SteamInventoryRefreshWorker),
                    exception,
                    CancellationToken.None);
            }
        }
    }

    private void EnqueueJob(SteamInventoryRefreshJob job)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        lock (_queueLock)
        {
            _queue.Enqueue(job, ((int)job.Priority, sequence));
        }

        _queueSignal.Release();
    }

    private bool TryDequeueJob(out SteamInventoryRefreshJob job)
    {
        lock (_queueLock)
        {
            if (_queue.Count > 0)
            {
                job = _queue.Dequeue();
                return true;
            }
        }

        job = default;
        return false;
    }

    private async Task ProcessJobAsync(SteamInventoryRefreshJob job, CancellationToken cancellationToken)
    {
        var key = BuildKey(job.SteamId, job.GameType);
        if (_queuedKeys.TryGetValue(key, out var queuedPriority) && job.Priority > queuedPriority)
        {
            await WriteRefreshLogAsync(
                "Info",
                "SkipEnqueue",
                job.SteamId,
                job.GameType,
                job.Priority,
                reason: $"StaleLowerPriorityJob; QueuedPriority={queuedPriority}",
                cancellationToken: CancellationToken.None);
            return;
        }

        _queuedKeys.TryRemove(key, out _);
        var refreshLock = _refreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var snapshot = await GetOrCreateSnapshotAsync(dbContext, job.SteamId, job.GameType, cancellationToken);
            await WriteRefreshLogAsync(
                "Info",
                "WorkerStartedJob",
                job.SteamId,
                job.GameType,
                job.Priority,
                reason: "Dequeued",
                snapshot: snapshot,
                cancellationToken: CancellationToken.None);

            var effectiveNextAllowedUtc = await GetEffectiveNextAllowedUtcAsync(dbContext, snapshot, cancellationToken);
            var now = DateTime.UtcNow;
            if (effectiveNextAllowedUtc is not null && effectiveNextAllowedUtc.Value > now)
            {
                snapshot.RefreshInProgress = false;
                snapshot.NextAllowedRefreshUtc = MaxUtc(snapshot.NextAllowedRefreshUtc, effectiveNextAllowedUtc);
                await dbContext.SaveChangesAsync(cancellationToken);
                await WriteRefreshLogAsync(
                    "Info",
                    "RefreshFinished",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    reason: "SkippedCooldownActive",
                    snapshot: snapshot,
                    nextAllowedRefreshUtc: effectiveNextAllowedUtc,
                    cancellationToken: CancellationToken.None);
                return;
            }

            snapshot.RefreshInProgress = true;
            snapshot.LastAttemptUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken);

            var fetchResult = await FetchInventoryAsync(job, snapshot, cancellationToken);
            if (fetchResult.IsSuccess)
            {
                var successUtc = DateTime.UtcNow;
                snapshot.ItemsJson = JsonSerializer.Serialize(fetchResult.Items, SerializerOptions);
                snapshot.LastSuccessRefreshUtc = successUtc;
                snapshot.LastAttemptUtc = successUtc;
                snapshot.LastErrorCode = null;
                snapshot.LastErrorMessage = null;
                snapshot.RateLimitStrikeCount = 0;
                snapshot.NextAllowedRefreshUtc = successUtc.AddMinutes(Math.Max(1, _options.SnapshotFreshnessMinutes));
                snapshot.RefreshInProgress = false;
                ResetGlobalRateLimit();
                await dbContext.SaveChangesAsync(cancellationToken);

                await WriteRefreshLogAsync(
                    "Info",
                    "SnapshotSaved",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    reason: "Success",
                    parsedItemCount: fetchResult.Items.Count,
                    snapshot: snapshot,
                    cancellationToken: CancellationToken.None);
                await WriteRefreshLogAsync(
                    "Info",
                    "RefreshFinished",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    reason: "Success",
                    parsedItemCount: fetchResult.Items.Count,
                    snapshot: snapshot,
                    cancellationToken: CancellationToken.None);
                return;
            }

            var failureUtc = DateTime.UtcNow;
            snapshot.LastAttemptUtc = failureUtc;
            snapshot.LastErrorCode = fetchResult.ErrorCode;
            snapshot.LastErrorMessage = Truncate(fetchResult.ErrorMessage ?? "Steam inventory refresh failed.", 1000);
            snapshot.RefreshInProgress = false;

            if (fetchResult.IsRateLimited)
            {
                snapshot.RateLimitStrikeCount = Math.Max(1, snapshot.RateLimitStrikeCount + 1);
                var adaptiveBackoff = GetRateLimitBackoff(snapshot.RateLimitStrikeCount);
                var backoff = Max(adaptiveBackoff, fetchResult.RetryAfterDelay);
                snapshot.NextAllowedRefreshUtc = failureUtc.Add(backoff);
                ActivateGlobalRateLimit(backoff);
                await WriteRefreshLogAsync(
                    "Warning",
                    "RateLimited",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    reason: $"BackoffSeconds={(int)Math.Ceiling(backoff.TotalSeconds)}; AdaptiveBackoffSeconds={(int)Math.Ceiling(adaptiveBackoff.TotalSeconds)}",
                    httpStatusCode: fetchResult.HttpStatusCode,
                    pageNumber: fetchResult.PageNumber,
                    parsedItemCount: fetchResult.ParsedItemCount,
                    snapshot: snapshot,
                    nextAllowedRefreshUtc: snapshot.NextAllowedRefreshUtc,
                    rateLimitStrikeCount: snapshot.RateLimitStrikeCount,
                    retryAfter: fetchResult.RetryAfter,
                    responseHeaders: fetchResult.ResponseHeaders,
                    bodyLength: fetchResult.BodyLength,
                    bodySnippet: fetchResult.BodySnippet,
                    errorMessage: snapshot.LastErrorMessage,
                    cancellationToken: CancellationToken.None);
            }
            else
            {
                snapshot.NextAllowedRefreshUtc = failureUtc.AddMinutes(Math.Max(1, _options.FailedRefreshCooldownMinutes));
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await WriteRefreshLogAsync(
                fetchResult.IsRateLimited ? "Warning" : "Error",
                "RefreshFailed",
                job.SteamId,
                job.GameType,
                job.Priority,
                reason: fetchResult.ErrorCode,
                httpStatusCode: fetchResult.HttpStatusCode,
                pageNumber: fetchResult.PageNumber,
                parsedItemCount: fetchResult.ParsedItemCount,
                snapshot: snapshot,
                retryAfter: fetchResult.RetryAfter,
                responseHeaders: fetchResult.ResponseHeaders,
                bodyLength: fetchResult.BodyLength,
                bodySnippet: fetchResult.BodySnippet,
                errorMessage: snapshot.LastErrorMessage,
                cancellationToken: CancellationToken.None);
            await WriteRefreshLogAsync(
                fetchResult.IsRateLimited ? "Warning" : "Error",
                "RefreshFinished",
                job.SteamId,
                job.GameType,
                job.Priority,
                reason: "Failure",
                httpStatusCode: fetchResult.HttpStatusCode,
                pageNumber: fetchResult.PageNumber,
                parsedItemCount: fetchResult.ParsedItemCount,
                snapshot: snapshot,
                retryAfter: fetchResult.RetryAfter,
                errorMessage: snapshot.LastErrorMessage,
                cancellationToken: CancellationToken.None);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<SteamInventoryFetchResult> FetchInventoryAsync(
        SteamInventoryRefreshJob job,
        SteamInventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var game = _gameCatalog.Get(job.GameType);
        var client = _httpClientFactory.CreateClient("SteamInventoryRefresh");
        var allItems = new List<SteamInventoryItemDto>();
        var seenAssetIds = new HashSet<string>(StringComparer.Ordinal);
        string? startAssetId = null;
        var page = 0;

        while (true)
        {
            page++;
            if (page > MaxInventoryPages)
            {
                return SteamInventoryFetchResult.Failed(
                    "PaginationLimit",
                    $"Steam inventory pagination exceeded {MaxInventoryPages} pages.",
                    isRateLimited: false);
            }

            await WaitForSteamRequestSlotAsync(cancellationToken);
            var requestUrl = BuildInventoryUrl(job.SteamId, game.SteamAppId, game.SteamContextId.ToString(), startAssetId);
            var stopwatch = Stopwatch.StartNew();
            string? rawContent = null;

            try
            {
                await WriteRefreshLogAsync(
                    "Info",
                    "SteamRequestStarted",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    reason: $"Url={requestUrl}",
                    pageNumber: page,
                    parsedItemCount: allItems.Count,
                    snapshot: snapshot,
                    cancellationToken: CancellationToken.None);

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{job.SteamId}/inventory");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                rawContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var bodyLength = rawContent?.Length ?? 0;
                var bodySnippet = BuildBodySnippet(rawContent);
                var retryAfter = GetRetryAfterValue(response);
                var responseHeaders = BuildHeaderSummary(response);
                await WriteRefreshLogAsync(
                    response.IsSuccessStatusCode ? "Info" : "Warning",
                    "SteamHttpStatus",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    reason: $"ElapsedMs={stopwatch.ElapsedMilliseconds}; ReasonPhrase={response.ReasonPhrase ?? "<null>"}",
                    httpStatusCode: (int)response.StatusCode,
                    pageNumber: page,
                    parsedItemCount: allItems.Count,
                    snapshot: snapshot,
                    retryAfter: retryAfter,
                    responseHeaders: responseHeaders,
                    bodyLength: bodyLength,
                    bodySnippet: bodySnippet,
                    cancellationToken: CancellationToken.None);

                if ((int)response.StatusCode == 429)
                {
                    return SteamInventoryFetchResult.Failed(
                        "429",
                        $"Steam inventory request was rate limited on page {page}. Body={bodySnippet}",
                        isRateLimited: true,
                        httpStatusCode: 429,
                        pageNumber: page,
                        parsedItemCount: allItems.Count,
                        retryAfterDelay: GetRetryAfterDelay(response),
                        retryAfter: retryAfter,
                        responseHeaders: responseHeaders,
                        bodyLength: bodyLength,
                        bodySnippet: bodySnippet);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return SteamInventoryFetchResult.Failed(
                        "403",
                        "Steam inventory is private or unavailable.",
                        isRateLimited: false,
                        httpStatusCode: 403,
                        pageNumber: page,
                        parsedItemCount: allItems.Count,
                        retryAfter: retryAfter,
                        responseHeaders: responseHeaders,
                        bodyLength: bodyLength,
                        bodySnippet: bodySnippet);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return SteamInventoryFetchResult.Failed(
                        $"HTTP_{(int)response.StatusCode}",
                        $"Steam inventory request failed with HTTP {(int)response.StatusCode} on page {page}. Body={bodySnippet}",
                        isRateLimited: false,
                        httpStatusCode: (int)response.StatusCode,
                        pageNumber: page,
                        parsedItemCount: allItems.Count,
                        retryAfter: retryAfter,
                        responseHeaders: responseHeaders,
                        bodyLength: bodyLength,
                        bodySnippet: bodySnippet);
                }

                using var document = JsonDocument.Parse(rawContent ?? string.Empty);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return SteamInventoryFetchResult.Failed(
                        "InvalidPayload",
                        $"Steam inventory response root was invalid on page {page}.",
                        isRateLimited: false);
                }

                if (root.TryGetProperty("success", out var successElement) &&
                    successElement.ValueKind == JsonValueKind.Number &&
                    successElement.GetInt32() != 1)
                {
                    return SteamInventoryFetchResult.Failed(
                        "SteamUnavailable",
                        $"Steam inventory payload reported success={successElement.GetInt32()} on page {page}.",
                        isRateLimited: false);
                }

                var totalInventoryCount = GetInt32(root, "total_inventory_count");
                if (!root.TryGetProperty("assets", out var assetsElement) ||
                    assetsElement.ValueKind != JsonValueKind.Array)
                {
                    if (totalInventoryCount == 0)
                    {
                        return SteamInventoryFetchResult.Success(allItems);
                    }

                    return SteamInventoryFetchResult.Failed(
                        "MissingAssets",
                        $"Steam inventory response omitted assets on page {page}. TotalInventoryCount={FormatInt(totalInventoryCount)}.",
                        isRateLimited: false);
                }

                var descriptions = BuildDescriptions(root);
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var item = BuildInventoryItem(asset, descriptions, job.GameType);
                    if (item is null || !seenAssetIds.Add(item.AssetId))
                    {
                        continue;
                    }

                    allItems.Add(item);
                }

                var moreItems = GetBooleanFlag(root, "more_items") == true;
                var lastAssetId = GetString(root, "last_assetid");
                await WriteRefreshLogAsync(
                    "Info",
                    "PaginationPage",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    reason: $"Game={game.Key}; PageAssets={assetsElement.GetArrayLength()}; MoreItems={moreItems}; LastAssetId={lastAssetId ?? "<null>"}; ElapsedMs={stopwatch.ElapsedMilliseconds}",
                    httpStatusCode: 200,
                    pageNumber: page,
                    parsedItemCount: allItems.Count,
                    snapshot: snapshot,
                    cancellationToken: CancellationToken.None);

                if (!moreItems)
                {
                    return SteamInventoryFetchResult.Success(allItems);
                }

                if (string.IsNullOrWhiteSpace(lastAssetId))
                {
                    return SteamInventoryFetchResult.Failed(
                        "MissingLastAssetId",
                        $"Steam inventory requested pagination but omitted last_assetid on page {page}.",
                        isRateLimited: false);
                }

                if (string.Equals(lastAssetId, startAssetId, StringComparison.Ordinal))
                {
                    return SteamInventoryFetchResult.Failed(
                        "RepeatedLastAssetId",
                        $"Steam inventory pagination repeated last_assetid={lastAssetId} on page {page}.",
                        isRateLimited: false);
                }

                startAssetId = lastAssetId;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException exception)
            {
                return SteamInventoryFetchResult.Failed(
                    "InvalidJson",
                    $"Steam inventory returned invalid JSON on page {page}: {exception.Message}. Body={BuildBodySnippet(rawContent)}",
                    isRateLimited: false);
            }
            catch (HttpRequestException exception)
            {
                return SteamInventoryFetchResult.Failed(
                    "HttpRequestException",
                    $"Failed to reach Steam inventory endpoint on page {page}: {exception.Message}",
                    isRateLimited: false);
            }
            catch (TaskCanceledException exception)
            {
                return SteamInventoryFetchResult.Failed(
                    "Timeout",
                    $"Steam inventory request timed out on page {page}: {exception.Message}",
                    isRateLimited: false);
            }
        }
    }

    private async Task WaitForSteamRequestSlotAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _options.DelayBetweenSteamRequestsSeconds));
        await _steamRequestDelayLock.WaitAsync(cancellationToken);
        try
        {
            if (_lastSteamInventoryRequestUtc is not null)
            {
                var waitFor = delay - (DateTime.UtcNow - _lastSteamInventoryRequestUtc.Value);
                if (waitFor > TimeSpan.Zero)
                {
                    await Task.Delay(waitFor, cancellationToken);
                }
            }

            _lastSteamInventoryRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _steamRequestDelayLock.Release();
        }
    }

    private async Task ResetStaleInProgressFlagsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var snapshots = await dbContext.SteamInventorySnapshots
                .Where(item => item.RefreshInProgress)
                .ToListAsync(cancellationToken);
            foreach (var snapshot in snapshots)
            {
                snapshot.RefreshInProgress = false;
            }

            if (snapshots.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            await _appLogService.WriteAsync(
                "Error",
                $"Failed to reset inventory refresh flags. Message={exception.Message}",
                nameof(SteamInventoryRefreshWorker),
                exception,
                CancellationToken.None);
        }
    }

    private async Task<SteamInventorySnapshot> GetOrCreateSnapshotAsync(
        AppDbContext dbContext,
        string steamId,
        GameType gameType,
        CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.SteamInventorySnapshots
            .SingleOrDefaultAsync(item => item.SteamId == steamId && item.GameType == gameType, cancellationToken);
        if (snapshot is not null)
        {
            return snapshot;
        }

        snapshot = new SteamInventorySnapshot
        {
            Id = Guid.NewGuid(),
            SteamId = steamId,
            GameType = gameType,
            ItemsJson = "[]"
        };
        dbContext.SteamInventorySnapshots.Add(snapshot);
        return snapshot;
    }

    private SteamInventoryRefreshStatus BuildStatus(
        SteamInventorySnapshot? snapshot,
        string key,
        DateTime? effectiveNextAllowedUtc = null)
    {
        var nextAllowedUtc = MaxUtc(effectiveNextAllowedUtc, GetEffectiveNextAllowedUtc(snapshot));
        var now = DateTime.UtcNow;
        var isQueued = _queuedKeys.ContainsKey(key);
        var isRefreshing = (snapshot?.RefreshInProgress ?? false) || isQueued;
        return new SteamInventoryRefreshStatus
        {
            LastSuccessRefreshUtc = snapshot?.LastSuccessRefreshUtc,
            LastAttemptUtc = snapshot?.LastAttemptUtc,
            IsRefreshing = isRefreshing,
            IsRateLimited = nextAllowedUtc is not null && nextAllowedUtc.Value > now,
            NextAllowedRefreshUtc = nextAllowedUtc,
            LastErrorMessage = snapshot?.LastErrorMessage
        };
    }

    private DateTime? GetEffectiveNextAllowedUtc(SteamInventorySnapshot? snapshot)
    {
        return MaxUtc(snapshot?.NextAllowedRefreshUtc, GetGlobalNextAllowedRefreshUtc());
    }

    private async Task<DateTime?> GetEffectiveNextAllowedUtcAsync(
        AppDbContext dbContext,
        SteamInventorySnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        var persistedGlobal = await GetPersistedGlobalNextAllowedRefreshUtcAsync(dbContext, cancellationToken);
        if (persistedGlobal is not null)
        {
            SetGlobalNextAllowedRefreshUtc(persistedGlobal.Value);
        }

        return MaxUtc(snapshot?.NextAllowedRefreshUtc, MaxUtc(GetGlobalNextAllowedRefreshUtc(), persistedGlobal));
    }

    private async Task<DateTime?> GetPersistedGlobalNextAllowedRefreshUtcAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        return await dbContext.SteamInventorySnapshots
            .AsNoTracking()
            .Where(item => item.LastErrorCode == "429" &&
                           item.NextAllowedRefreshUtc != null &&
                           item.NextAllowedRefreshUtc > now)
            .MaxAsync(item => item.NextAllowedRefreshUtc, cancellationToken);
    }

    private async Task HydrateGlobalRateLimitFromSnapshotsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var nextAllowedUtc = await GetPersistedGlobalNextAllowedRefreshUtcAsync(dbContext, cancellationToken);
            if (nextAllowedUtc is null)
            {
                return;
            }

            SetGlobalNextAllowedRefreshUtc(nextAllowedUtc.Value);
            await WriteRefreshLogAsync(
                "Warning",
                "GlobalCooldownRestored",
                "<global>",
                _gameCatalog.DefaultGameType,
                reason: "RestoredFromRecent429Snapshots",
                nextAllowedRefreshUtc: nextAllowedUtc,
                cancellationToken: CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _appLogService.WriteAsync(
                "Warning",
                $"Failed to hydrate Steam inventory global cooldown from snapshots. Message={exception.Message}",
                nameof(SteamInventoryRefreshWorker),
                exception,
                CancellationToken.None);
        }
    }

    private DateTime? GetGlobalNextAllowedRefreshUtc()
    {
        lock (_globalRateLimitLock)
        {
            if (_globalNextAllowedRefreshUtc is null || _globalNextAllowedRefreshUtc <= DateTime.UtcNow)
            {
                return null;
            }

            return _globalNextAllowedRefreshUtc;
        }
    }

    private void ActivateGlobalRateLimit(TimeSpan backoff)
    {
        lock (_globalRateLimitLock)
        {
            _globalRateLimitStrikeCount++;
            var nextAllowed = DateTime.UtcNow.Add(GetRateLimitBackoff(_globalRateLimitStrikeCount));
            var perUserNextAllowed = DateTime.UtcNow.Add(backoff);
            _globalNextAllowedRefreshUtc = MaxUtc(_globalNextAllowedRefreshUtc, MaxUtc(nextAllowed, perUserNextAllowed));
        }
    }

    private void SetGlobalNextAllowedRefreshUtc(DateTime nextAllowedRefreshUtc)
    {
        lock (_globalRateLimitLock)
        {
            _globalNextAllowedRefreshUtc = MaxUtc(_globalNextAllowedRefreshUtc, nextAllowedRefreshUtc);
        }
    }

    private void ResetGlobalRateLimit()
    {
        lock (_globalRateLimitLock)
        {
            _globalRateLimitStrikeCount = 0;
            _globalNextAllowedRefreshUtc = null;
        }
    }

    private static TimeSpan GetRateLimitBackoff(int strikeCount)
    {
        return strikeCount switch
        {
            <= 1 => TimeSpan.FromMinutes(15),
            2 => TimeSpan.FromMinutes(30),
            3 => TimeSpan.FromHours(1),
            4 => TimeSpan.FromHours(3),
            _ => TimeSpan.FromHours(6)
        };
    }

    private static DateTime? MaxUtc(DateTime? first, DateTime? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first.Value >= second.Value ? first : second;
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan? second)
    {
        if (second is null || second.Value <= TimeSpan.Zero)
        {
            return first;
        }

        return first >= second.Value ? first : second.Value;
    }

    private static string BuildInventoryUrl(string steamId, int appId, string contextId, string? startAssetId)
    {
        var url = $"https://steamcommunity.com/inventory/{steamId}/{appId}/{contextId}?l=english&count=2000";
        return string.IsNullOrWhiteSpace(startAssetId)
            ? url
            : $"{url}&start_assetid={Uri.EscapeDataString(startAssetId)}";
    }

    private static Dictionary<string, JsonElement> BuildDescriptions(JsonElement root)
    {
        var descriptions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!root.TryGetProperty("descriptions", out var descriptionsElement) ||
            descriptionsElement.ValueKind != JsonValueKind.Array)
        {
            return descriptions;
        }

        foreach (var description in descriptionsElement.EnumerateArray())
        {
            var classId = GetString(description, "classid");
            var instanceId = GetString(description, "instanceid");
            if (string.IsNullOrWhiteSpace(classId) || string.IsNullOrWhiteSpace(instanceId))
            {
                continue;
            }

            descriptions[$"{classId}_{instanceId}"] = description;
        }

        return descriptions;
    }

    private static SteamInventoryItemDto? BuildInventoryItem(
        JsonElement asset,
        IReadOnlyDictionary<string, JsonElement> descriptions,
        GameType gameType)
    {
        var assetId = GetString(asset, "assetid");
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return null;
        }

        var classId = GetString(asset, "classid") ?? string.Empty;
        var instanceId = GetString(asset, "instanceid") ?? string.Empty;
        descriptions.TryGetValue($"{classId}_{instanceId}", out var description);
        var marketHashName = MarketHashNameUtility.Normalize(GetString(description, "market_hash_name"));
        var marketName = MarketHashNameUtility.Normalize(GetString(description, "market_name"));

        return new SteamInventoryItemDto
        {
            GameType = gameType,
            AssetId = assetId,
            ClassId = classId,
            InstanceId = instanceId,
            Name = GetString(description, "name") ?? "Unknown Item",
            MarketHashName = marketHashName,
            MarketName = marketName,
            IconUrl = BuildIconUrl(GetString(description, "icon_url")),
            Tradable = GetBooleanFlag(description, "tradable"),
            Marketable = GetBooleanFlag(description, "marketable")
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
    }

    private static bool? GetBooleanFlag(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetInt32() == 1,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            return null;
        }

        return value;
    }

    private static string? BuildIconUrl(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return null;
        }

        return iconPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? iconPath
            : IconBaseUrl + iconPath;
    }

    private static string BuildBodySnippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        var compact = body
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);

        while (compact.Contains("  ", StringComparison.Ordinal))
        {
            compact = compact.Replace("  ", " ", StringComparison.Ordinal);
        }

        return Truncate(compact.Trim(), BodySnippetLength);
    }

    private static string FormatInt(int? value)
    {
        return value?.ToString() ?? "<null>";
    }

    private static string NormalizeSteamId(string steamId)
    {
        return steamId.Trim();
    }

    private static string BuildKey(string steamId, GameType gameType)
    {
        return $"{steamId}::{(int)gameType}";
    }

    private async Task WriteRefreshLogAsync(
        string level,
        string eventName,
        string steamId,
        GameType gameType,
        SteamInventoryRefreshPriority? priority = null,
        string? reason = null,
        int? httpStatusCode = null,
        int? pageNumber = null,
        int? parsedItemCount = null,
        SteamInventorySnapshot? snapshot = null,
        DateTime? nextAllowedRefreshUtc = null,
        int? rateLimitStrikeCount = null,
        string? retryAfter = null,
        string? responseHeaders = null,
        int? bodyLength = null,
        string? bodySnippet = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedLevel = string.IsNullOrWhiteSpace(level) ? "Info" : level.Trim();
        var normalizedPriority = priority?.ToString() ?? "<none>";
        var refreshSource = priority == SteamInventoryRefreshPriority.High ? "Manual" :
            priority == SteamInventoryRefreshPriority.Normal ? "Auto" : "<unknown>";
        var message = string.Join("; ", new[]
            {
                $"Event={eventName}",
                $"Status={normalizedLevel}",
                $"TimestampUtc={DateTime.UtcNow:O}",
                $"SteamId={steamId}",
                $"GameType={(int)gameType}",
                $"Priority={normalizedPriority}",
                $"RefreshSource={refreshSource}",
                httpStatusCode.HasValue ? $"HttpStatusCode={httpStatusCode.Value}" : null,
                pageNumber.HasValue ? $"PageNumber={pageNumber.Value}" : null,
                parsedItemCount.HasValue ? $"ParsedItemCount={parsedItemCount.Value}" : null,
                snapshot?.LastSuccessRefreshUtc is not null ? $"LastSuccessRefreshUtc={snapshot.LastSuccessRefreshUtc.Value:O}" : null,
                snapshot?.LastAttemptUtc is not null ? $"LastAttemptUtc={snapshot.LastAttemptUtc.Value:O}" : null,
                (nextAllowedRefreshUtc ?? snapshot?.NextAllowedRefreshUtc) is DateTime nextAllowed ? $"NextAllowedRefreshUtc={nextAllowed:O}" : null,
                (rateLimitStrikeCount ?? snapshot?.RateLimitStrikeCount) is int strikes ? $"RateLimitStrikeCount={strikes}" : null,
                !string.IsNullOrWhiteSpace(retryAfter) ? $"RetryAfter={retryAfter}" : null,
                bodyLength.HasValue ? $"BodyLength={bodyLength.Value}" : null,
                !string.IsNullOrWhiteSpace(bodySnippet) ? $"BodySnippet={Truncate(bodySnippet, 300)}" : null,
                !string.IsNullOrWhiteSpace(responseHeaders) ? $"Headers={Truncate(responseHeaders, 1200)}" : null,
                !string.IsNullOrWhiteSpace(reason) ? $"Reason={Truncate(reason, 700)}" : null,
                !string.IsNullOrWhiteSpace(errorMessage) ? $"Error={Truncate(errorMessage, 700)}" : null
            }
            .Where(item => !string.IsNullOrWhiteSpace(item)));

        switch (normalizedLevel.ToLowerInvariant())
        {
            case "error":
            case "critical":
                _logger.LogError("{Message}", message);
                break;
            case "warning":
                _logger.LogWarning("{Message}", message);
                break;
            default:
                _logger.LogInformation("{Message}", message);
                break;
        }

        await _appLogService.WriteAsync(
            normalizedLevel,
            message,
            nameof(SteamInventoryRefreshWorker),
            cancellationToken: cancellationToken);
    }

    private static string GetRetryAfterValue(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return $"{Math.Max(0, (int)Math.Ceiling(delta.TotalSeconds))}s";
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            return date.UtcDateTime.ToString("O");
        }

        return "<none>";
    }

    private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            var delay = date.UtcDateTime - DateTime.UtcNow;
            return delay > TimeSpan.Zero ? delay : null;
        }

        return null;
    }

    private static string BuildHeaderSummary(HttpResponseMessage response)
    {
        var headers = response.Headers
            .Select(header => $"{header.Key}={string.Join(",", header.Value)}")
            .Concat(response.Content.Headers.Select(header => $"{header.Key}={string.Join(",", header.Value)}"));
        return string.Join(" | ", headers);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private readonly record struct SteamInventoryRefreshJob(
        string SteamId,
        GameType GameType,
        SteamInventoryRefreshPriority Priority);

    private sealed class SteamInventoryFetchResult
    {
        public bool IsSuccess { get; private init; }
        public bool IsRateLimited { get; private init; }
        public string? ErrorCode { get; private init; }
        public string? ErrorMessage { get; private init; }
        public int? HttpStatusCode { get; private init; }
        public int? PageNumber { get; private init; }
        public int ParsedItemCount { get; private init; }
        public TimeSpan? RetryAfterDelay { get; private init; }
        public string? RetryAfter { get; private init; }
        public string? ResponseHeaders { get; private init; }
        public int? BodyLength { get; private init; }
        public string? BodySnippet { get; private init; }
        public List<SteamInventoryItemDto> Items { get; private init; } = new();

        public static SteamInventoryFetchResult Success(List<SteamInventoryItemDto> items)
        {
            return new SteamInventoryFetchResult
            {
                IsSuccess = true,
                Items = items
            };
        }

        public static SteamInventoryFetchResult Failed(
            string errorCode,
            string errorMessage,
            bool isRateLimited,
            int? httpStatusCode = null,
            int? pageNumber = null,
            int parsedItemCount = 0,
            TimeSpan? retryAfterDelay = null,
            string? retryAfter = null,
            string? responseHeaders = null,
            int? bodyLength = null,
            string? bodySnippet = null)
        {
            return new SteamInventoryFetchResult
            {
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                IsRateLimited = isRateLimited,
                HttpStatusCode = httpStatusCode,
                PageNumber = pageNumber,
                ParsedItemCount = parsedItemCount,
                RetryAfterDelay = retryAfterDelay,
                RetryAfter = retryAfter,
                ResponseHeaders = responseHeaders,
                BodyLength = bodyLength,
                BodySnippet = bodySnippet
            };
        }
    }
}
