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
    private const int BodySnippetLength = 280;
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

        return BuildStatus(snapshot, BuildKey(normalizedSteamId, gameType));
    }

    public async Task<IReadOnlyList<SteamInventoryRefreshDebugState>> GetDebugStatesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var take = limit <= 0 ? 100 : Math.Min(limit, 500);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var snapshots = await dbContext.SteamInventorySnapshots
            .AsNoTracking()
            .OrderByDescending(item => item.LastAttemptUtc ?? item.LastSuccessRefreshUtc ?? DateTime.MinValue)
            .ThenBy(item => item.SteamId)
            .Take(take)
            .ToListAsync(cancellationToken);

        return snapshots
            .Select(snapshot =>
            {
                var key = BuildKey(snapshot.SteamId, snapshot.GameType);
                var isQueued = _queuedKeys.TryGetValue(key, out var queuedPriority);
                return new SteamInventoryRefreshDebugState
                {
                    SteamId = snapshot.SteamId,
                    GameType = snapshot.GameType,
                    ItemCount = CountSnapshotItems(snapshot.ItemsJson),
                    LastSuccessRefreshUtc = snapshot.LastSuccessRefreshUtc,
                    LastAttemptUtc = snapshot.LastAttemptUtc,
                    LastErrorCode = snapshot.LastErrorCode,
                    LastErrorMessage = snapshot.LastErrorMessage,
                    RateLimitStrikeCount = snapshot.RateLimitStrikeCount,
                    NextAllowedRefreshUtc = MaxUtc(snapshot.NextAllowedRefreshUtc, GetGlobalNextAllowedRefreshUtc()),
                    RefreshInProgress = snapshot.RefreshInProgress,
                    QueueStatus = isQueued ? "Queued" : snapshot.RefreshInProgress ? "InProgress" : "NotQueued",
                    QueuePriority = isQueued ? queuedPriority.ToString() : null
                };
            })
            .ToList();
    }

    public async Task<SteamInventoryRefreshStatus> EnqueueRefreshAsync(
        string steamId,
        GameType gameType,
        SteamInventoryRefreshPriority priority,
        SteamInventoryRefreshSource source,
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
            var globalNextAllowedUtc = GetGlobalNextAllowedRefreshUtc();
            var perUserNextAllowedUtc = snapshot.NextAllowedRefreshUtc is not null && snapshot.NextAllowedRefreshUtc > now
                ? snapshot.NextAllowedRefreshUtc
                : null;
            var effectiveNextAllowedUtc = MaxUtc(perUserNextAllowedUtc, globalNextAllowedUtc);
            if (dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await WriteInventoryRefreshLogAsync(
                "Info",
                "PerUserCooldownCheck",
                normalizedSteamId,
                gameType,
                priority,
                source,
                snapshot: snapshot,
                nextAllowedRefreshUtc: perUserNextAllowedUtc,
                reason: perUserNextAllowedUtc is null ? "NoPerUserCooldown" : "PerUserCooldownActive",
                cancellationToken: CancellationToken.None);
            await WriteInventoryRefreshLogAsync(
                "Info",
                "GlobalCooldownCheck",
                normalizedSteamId,
                gameType,
                priority,
                source,
                snapshot: snapshot,
                nextAllowedRefreshUtc: globalNextAllowedUtc,
                reason: globalNextAllowedUtc is null ? "NoGlobalCooldown" : "GlobalCooldownActive",
                cancellationToken: CancellationToken.None);

            if (effectiveNextAllowedUtc is not null && effectiveNextAllowedUtc.Value > now)
            {
                await WriteInventoryRefreshLogAsync(
                    "Warning",
                    "SkipEnqueue",
                    normalizedSteamId,
                    gameType,
                    priority,
                    source,
                    snapshot: snapshot,
                    nextAllowedRefreshUtc: effectiveNextAllowedUtc,
                    reason: "CooldownActive",
                    cancellationToken: CancellationToken.None);
                return BuildStatus(snapshot, key, effectiveNextAllowedUtc, "CooldownActive");
            }

            if (snapshot.RefreshInProgress && !_queuedKeys.ContainsKey(key))
            {
                await WriteInventoryRefreshLogAsync(
                    "Info",
                    "SkipEnqueue",
                    normalizedSteamId,
                    gameType,
                    priority,
                    source,
                    snapshot: snapshot,
                    reason: "RefreshInProgress",
                    queueStatus: "InProgress",
                    cancellationToken: CancellationToken.None);
                return BuildStatus(snapshot, key, reason: "RefreshInProgress");
            }

            if (_queuedKeys.TryGetValue(key, out var existingPriority))
            {
                if (priority < existingPriority &&
                    _queuedKeys.TryUpdate(key, priority, existingPriority))
                {
                    EnqueueJob(new SteamInventoryRefreshJob(normalizedSteamId, gameType, priority, source));
                    await WriteInventoryRefreshLogAsync(
                        "Info",
                        "PriorityUpgrade",
                        normalizedSteamId,
                        gameType,
                        priority,
                        source,
                        snapshot: snapshot,
                        reason: $"Priority upgraded from {existingPriority} to {priority}.",
                        queueStatus: "Queued",
                        cancellationToken: CancellationToken.None);
                    return BuildStatus(snapshot, key, reason: "PriorityUpgraded");
                }

                await WriteInventoryRefreshLogAsync(
                    "Info",
                    "SkipDuplicateJob",
                    normalizedSteamId,
                    gameType,
                    priority,
                    source,
                    snapshot: snapshot,
                    reason: $"Duplicate queued job already exists with priority {existingPriority}.",
                    queueStatus: "Queued",
                    queuePriority: existingPriority.ToString(),
                    cancellationToken: CancellationToken.None);
                return BuildStatus(snapshot, key, reason: "DuplicateQueued");
            }

            snapshot.RefreshInProgress = true;
            await dbContext.SaveChangesAsync(cancellationToken);

            _queuedKeys[key] = priority;
            EnqueueJob(new SteamInventoryRefreshJob(normalizedSteamId, gameType, priority, source));
            await WriteInventoryRefreshLogAsync(
                "Info",
                "EnqueueJob",
                normalizedSteamId,
                gameType,
                priority,
                source,
                snapshot: snapshot,
                reason: "Queued",
                queueStatus: "Queued",
                cancellationToken: CancellationToken.None);

            return BuildStatus(snapshot, key, reason: "Queued");
        }
        finally
        {
            refreshLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStaleInProgressFlagsAsync(stoppingToken);
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
            await WriteInventoryRefreshLogAsync(
                "Info",
                "SkipDuplicateJob",
                job.SteamId,
                job.GameType,
                job.Priority,
                job.Source,
                reason: $"Stale lower-priority job skipped because queued priority is {queuedPriority}.",
                queueStatus: "Queued",
                queuePriority: queuedPriority.ToString(),
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
            await WriteInventoryRefreshLogAsync(
                "Info",
                "WorkerStartedJob",
                job.SteamId,
                job.GameType,
                job.Priority,
                job.Source,
                snapshot: snapshot,
                queueStatus: "Dequeued",
                cancellationToken: CancellationToken.None);

            var globalNextAllowedUtc = GetGlobalNextAllowedRefreshUtc();
            var perUserNextAllowedUtc = snapshot.NextAllowedRefreshUtc is not null && snapshot.NextAllowedRefreshUtc > DateTime.UtcNow
                ? snapshot.NextAllowedRefreshUtc
                : null;
            var effectiveNextAllowedUtc = MaxUtc(perUserNextAllowedUtc, globalNextAllowedUtc);
            var now = DateTime.UtcNow;
            await WriteInventoryRefreshLogAsync(
                "Info",
                "PerUserCooldownCheck",
                job.SteamId,
                job.GameType,
                job.Priority,
                job.Source,
                snapshot: snapshot,
                nextAllowedRefreshUtc: perUserNextAllowedUtc,
                reason: perUserNextAllowedUtc is null ? "NoPerUserCooldown" : "PerUserCooldownActive",
                cancellationToken: CancellationToken.None);
            await WriteInventoryRefreshLogAsync(
                "Info",
                "GlobalCooldownCheck",
                job.SteamId,
                job.GameType,
                job.Priority,
                job.Source,
                snapshot: snapshot,
                nextAllowedRefreshUtc: globalNextAllowedUtc,
                reason: globalNextAllowedUtc is null ? "NoGlobalCooldown" : "GlobalCooldownActive",
                cancellationToken: CancellationToken.None);

            if (effectiveNextAllowedUtc is not null && effectiveNextAllowedUtc.Value > now)
            {
                snapshot.RefreshInProgress = false;
                snapshot.NextAllowedRefreshUtc = MaxUtc(snapshot.NextAllowedRefreshUtc, effectiveNextAllowedUtc);
                await dbContext.SaveChangesAsync(cancellationToken);
                await WriteInventoryRefreshLogAsync(
                    "Warning",
                    "SkipWorkerJob",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    job.Source,
                    snapshot: snapshot,
                    nextAllowedRefreshUtc: effectiveNextAllowedUtc,
                    reason: "CooldownActive",
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

                await WriteInventoryRefreshLogAsync(
                    "Info",
                    "SnapshotSaved",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    job.Source,
                    parsedItemCount: fetchResult.Items.Count,
                    snapshot: snapshot,
                    cancellationToken: CancellationToken.None);
                await WriteInventoryRefreshLogAsync(
                    "Info",
                    "RefreshFinished",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    job.Source,
                    parsedItemCount: fetchResult.Items.Count,
                    snapshot: snapshot,
                    reason: "Success",
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
                var backoff = GetRateLimitBackoff(snapshot.RateLimitStrikeCount);
                snapshot.NextAllowedRefreshUtc = failureUtc.Add(backoff);
                ActivateGlobalRateLimit(backoff);
            }
            else
            {
                snapshot.NextAllowedRefreshUtc = failureUtc.AddMinutes(Math.Max(1, _options.FailedRefreshCooldownMinutes));
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (fetchResult.IsRateLimited)
            {
                await WriteInventoryRefreshLogAsync(
                    "Warning",
                    "RateLimited429",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    job.Source,
                    httpStatusCode: fetchResult.HttpStatusCode,
                    pageNumber: fetchResult.PageNumber,
                    parsedItemCount: fetchResult.ParsedItemCount,
                    snapshot: snapshot,
                    errorMessage: snapshot.LastErrorMessage,
                    reason: "Steam429BackoffApplied",
                    cancellationToken: CancellationToken.None);
            }

            await WriteInventoryRefreshLogAsync(
                fetchResult.IsRateLimited ? "Warning" : "Error",
                "RefreshFinished",
                job.SteamId,
                job.GameType,
                job.Priority,
                job.Source,
                httpStatusCode: fetchResult.HttpStatusCode,
                pageNumber: fetchResult.PageNumber,
                parsedItemCount: fetchResult.ParsedItemCount,
                snapshot: snapshot,
                errorMessage: snapshot.LastErrorMessage,
                reason: "Failure",
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
                    isRateLimited: false,
                    pageNumber: page,
                    parsedItemCount: allItems.Count);
            }

            await WaitForSteamRequestSlotAsync(cancellationToken);
            var requestUrl = BuildInventoryUrl(job.SteamId, game.SteamAppId, game.SteamContextId.ToString(), startAssetId);
            var stopwatch = Stopwatch.StartNew();
            string? rawContent = null;

            try
            {
                await WriteInventoryRefreshLogAsync(
                    "Info",
                    "SteamRequestStarted",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    job.Source,
                    pageNumber: page,
                    parsedItemCount: allItems.Count,
                    snapshot: snapshot,
                    reason: $"Url={requestUrl}",
                    cancellationToken: CancellationToken.None);

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{job.SteamId}/inventory");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                rawContent = await response.Content.ReadAsStringAsync(cancellationToken);
                await WriteInventoryRefreshLogAsync(
                    response.IsSuccessStatusCode ? "Info" : "Warning",
                    "SteamHttpStatus",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    job.Source,
                    httpStatusCode: (int)response.StatusCode,
                    pageNumber: page,
                    parsedItemCount: allItems.Count,
                    snapshot: snapshot,
                    reason: response.ReasonPhrase ?? "<null>",
                    cancellationToken: CancellationToken.None);

                if ((int)response.StatusCode == 429)
                {
                    return SteamInventoryFetchResult.Failed(
                        "429",
                        $"Steam inventory request was rate limited on page {page}. Body={BuildBodySnippet(rawContent)}",
                        isRateLimited: true,
                        httpStatusCode: 429,
                        pageNumber: page,
                        parsedItemCount: allItems.Count);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    await WriteInventoryRefreshLogAsync(
                        "Warning",
                        "PrivateInventory403",
                        job.SteamId,
                        job.GameType,
                        job.Priority,
                        job.Source,
                        httpStatusCode: 403,
                        pageNumber: page,
                        parsedItemCount: allItems.Count,
                        snapshot: snapshot,
                        errorMessage: "Steam inventory is private or unavailable.",
                        cancellationToken: CancellationToken.None);
                    return SteamInventoryFetchResult.Failed(
                        "403",
                        "Steam inventory is private or unavailable.",
                        isRateLimited: false,
                        httpStatusCode: 403,
                        pageNumber: page,
                        parsedItemCount: allItems.Count);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return SteamInventoryFetchResult.Failed(
                        $"HTTP_{(int)response.StatusCode}",
                        $"Steam inventory request failed with HTTP {(int)response.StatusCode} on page {page}. Body={BuildBodySnippet(rawContent)}",
                        isRateLimited: false,
                        httpStatusCode: (int)response.StatusCode,
                        pageNumber: page,
                        parsedItemCount: allItems.Count);
                }

                using var document = JsonDocument.Parse(rawContent);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return SteamInventoryFetchResult.Failed(
                        "InvalidPayload",
                        $"Steam inventory response root was invalid on page {page}.",
                        isRateLimited: false,
                        pageNumber: page,
                        parsedItemCount: allItems.Count);
                }

                if (root.TryGetProperty("success", out var successElement) &&
                    successElement.ValueKind == JsonValueKind.Number &&
                    successElement.GetInt32() != 1)
                {
                    return SteamInventoryFetchResult.Failed(
                        "SteamUnavailable",
                        $"Steam inventory payload reported success={successElement.GetInt32()} on page {page}.",
                        isRateLimited: false,
                        pageNumber: page,
                        parsedItemCount: allItems.Count);
                }

                var totalInventoryCount = GetInt32(root, "total_inventory_count");
                if (!root.TryGetProperty("assets", out var assetsElement) ||
                    assetsElement.ValueKind != JsonValueKind.Array)
                {
                    if (totalInventoryCount == 0)
                    {
                        await WriteInventoryRefreshLogAsync(
                            "Info",
                            "TotalParsedItems",
                            job.SteamId,
                            job.GameType,
                            job.Priority,
                            job.Source,
                            pageNumber: page,
                            parsedItemCount: allItems.Count,
                            snapshot: snapshot,
                            reason: "EmptyInventory",
                            cancellationToken: CancellationToken.None);
                        return SteamInventoryFetchResult.Success(allItems, page, allItems.Count);
                    }

                    return SteamInventoryFetchResult.Failed(
                        "MissingAssets",
                        $"Steam inventory response omitted assets on page {page}. TotalInventoryCount={FormatInt(totalInventoryCount)}.",
                        isRateLimited: false,
                        pageNumber: page,
                        parsedItemCount: allItems.Count);
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
                await WriteInventoryRefreshLogAsync(
                    "Info",
                    "PaginationPageParsed",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    job.Source,
                    pageNumber: page,
                    parsedItemCount: allItems.Count,
                    snapshot: snapshot,
                    moreItems: moreItems,
                    lastAssetId: lastAssetId,
                    reason: $"PageAssets={assetsElement.GetArrayLength()}; ElapsedMs={stopwatch.ElapsedMilliseconds}",
                    cancellationToken: CancellationToken.None);

                if (!moreItems)
                {
                    await WriteInventoryRefreshLogAsync(
                        "Info",
                        "TotalParsedItems",
                        job.SteamId,
                        job.GameType,
                        job.Priority,
                        job.Source,
                        pageNumber: page,
                        parsedItemCount: allItems.Count,
                        snapshot: snapshot,
                        reason: "PaginationComplete",
                        cancellationToken: CancellationToken.None);
                    return SteamInventoryFetchResult.Success(allItems, page, allItems.Count);
                }

                if (string.IsNullOrWhiteSpace(lastAssetId))
                {
                    return SteamInventoryFetchResult.Failed(
                        "MissingLastAssetId",
                        $"Steam inventory requested pagination but omitted last_assetid on page {page}.",
                        isRateLimited: false,
                        pageNumber: page,
                        parsedItemCount: allItems.Count);
                }

                if (string.Equals(lastAssetId, startAssetId, StringComparison.Ordinal))
                {
                    return SteamInventoryFetchResult.Failed(
                        "RepeatedLastAssetId",
                        $"Steam inventory pagination repeated last_assetid={lastAssetId} on page {page}.",
                        isRateLimited: false,
                        pageNumber: page,
                        parsedItemCount: allItems.Count);
                }

                startAssetId = lastAssetId;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException exception)
            {
                await WriteInventoryRefreshLogAsync(
                    "Error",
                    "RefreshError",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    job.Source,
                    pageNumber: page,
                    parsedItemCount: allItems.Count,
                    snapshot: snapshot,
                    errorMessage: exception.Message,
                    reason: "InvalidJson",
                    cancellationToken: CancellationToken.None);
                return SteamInventoryFetchResult.Failed(
                    "InvalidJson",
                    $"Steam inventory returned invalid JSON on page {page}: {exception.Message}. Body={BuildBodySnippet(rawContent)}",
                    isRateLimited: false,
                    pageNumber: page,
                    parsedItemCount: allItems.Count);
            }
            catch (HttpRequestException exception)
            {
                await WriteInventoryRefreshLogAsync(
                    "Error",
                    "RefreshError",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    job.Source,
                    pageNumber: page,
                    parsedItemCount: allItems.Count,
                    snapshot: snapshot,
                    errorMessage: exception.Message,
                    reason: "HttpRequestException",
                    cancellationToken: CancellationToken.None);
                return SteamInventoryFetchResult.Failed(
                    "HttpRequestException",
                    $"Failed to reach Steam inventory endpoint on page {page}: {exception.Message}",
                    isRateLimited: false,
                    pageNumber: page,
                    parsedItemCount: allItems.Count);
            }
            catch (TaskCanceledException exception)
            {
                await WriteInventoryRefreshLogAsync(
                    "Error",
                    "RefreshTimeout",
                    job.SteamId,
                    job.GameType,
                    job.Priority,
                    job.Source,
                    pageNumber: page,
                    parsedItemCount: allItems.Count,
                    snapshot: snapshot,
                    errorMessage: exception.Message,
                    cancellationToken: CancellationToken.None);
                return SteamInventoryFetchResult.Failed(
                    "Timeout",
                    $"Steam inventory request timed out on page {page}: {exception.Message}",
                    isRateLimited: false,
                    pageNumber: page,
                    parsedItemCount: allItems.Count);
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
        DateTime? effectiveNextAllowedUtc = null,
        string reason = "StatusRead")
    {
        var nextAllowedUtc = MaxUtc(effectiveNextAllowedUtc, GetEffectiveNextAllowedUtc(snapshot));
        var now = DateTime.UtcNow;
        var isQueued = _queuedKeys.TryGetValue(key, out var queuedPriority);
        var isRefreshing = (snapshot?.RefreshInProgress ?? false) || isQueued;
        return new SteamInventoryRefreshStatus
        {
            LastSuccessRefreshUtc = snapshot?.LastSuccessRefreshUtc,
            LastAttemptUtc = snapshot?.LastAttemptUtc,
            IsRefreshing = isRefreshing,
            IsRateLimited = nextAllowedUtc is not null && nextAllowedUtc.Value > now,
            NextAllowedRefreshUtc = nextAllowedUtc,
            LastErrorMessage = snapshot?.LastErrorMessage,
            RefreshState = isQueued ? "Queued" : snapshot?.RefreshInProgress == true ? "Refreshing" : nextAllowedUtc is not null && nextAllowedUtc.Value > now ? "Cooldown" : "Idle",
            Reason = reason,
            QueueStatus = isQueued ? "Queued" : snapshot?.RefreshInProgress == true ? "InProgress" : "NotQueued",
            QueuePriority = isQueued ? queuedPriority.ToString() : null
        };
    }

    private DateTime? GetEffectiveNextAllowedUtc(SteamInventorySnapshot? snapshot)
    {
        return MaxUtc(snapshot?.NextAllowedRefreshUtc, GetGlobalNextAllowedRefreshUtc());
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

    private async Task WriteInventoryRefreshLogAsync(
        string level,
        string eventName,
        string steamId,
        GameType gameType,
        SteamInventoryRefreshPriority priority,
        SteamInventoryRefreshSource source,
        int? httpStatusCode = null,
        int? pageNumber = null,
        int? parsedItemCount = null,
        SteamInventorySnapshot? snapshot = null,
        DateTime? nextAllowedRefreshUtc = null,
        int? rateLimitStrikeCount = null,
        bool? moreItems = null,
        string? lastAssetId = null,
        string? errorMessage = null,
        string? reason = null,
        string? queueStatus = null,
        string? queuePriority = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedLevel = string.IsNullOrWhiteSpace(level) ? "Info" : level.Trim();
        var message = string.Join("; ", new[]
            {
                $"Event={eventName}",
                $"Status={normalizedLevel}",
                $"TimestampUtc={DateTime.UtcNow:O}",
                $"SteamId={steamId}",
                $"GameType={(int)gameType}",
                $"JobPriority={priority}",
                $"RefreshSource={source}",
                httpStatusCode.HasValue ? $"HttpStatusCode={httpStatusCode.Value}" : null,
                pageNumber.HasValue ? $"PageNumber={pageNumber.Value}" : null,
                parsedItemCount.HasValue ? $"ParsedItemCount={parsedItemCount.Value}" : null,
                snapshot?.LastSuccessRefreshUtc is not null ? $"LastSuccessRefreshUtc={snapshot.LastSuccessRefreshUtc.Value:O}" : null,
                snapshot?.LastAttemptUtc is not null ? $"LastAttemptUtc={snapshot.LastAttemptUtc.Value:O}" : null,
                (nextAllowedRefreshUtc ?? snapshot?.NextAllowedRefreshUtc) is DateTime nextAllowed ? $"NextAllowedRefreshUtc={nextAllowed:O}" : null,
                $"RateLimitStrikeCount={rateLimitStrikeCount ?? snapshot?.RateLimitStrikeCount ?? 0}",
                moreItems.HasValue ? $"MoreItems={moreItems.Value}" : null,
                !string.IsNullOrWhiteSpace(lastAssetId) ? $"LastAssetId={lastAssetId}" : null,
                !string.IsNullOrWhiteSpace(queueStatus) ? $"QueueStatus={queueStatus}" : null,
                !string.IsNullOrWhiteSpace(queuePriority) ? $"QueuePriority={queuePriority}" : null,
                !string.IsNullOrWhiteSpace(reason) ? $"Reason={Truncate(reason, 500)}" : null,
                !string.IsNullOrWhiteSpace(errorMessage) ? $"Error={Truncate(errorMessage, 500)}" : null
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

    private static int CountSnapshotItems(string? itemsJson)
    {
        if (string.IsNullOrWhiteSpace(itemsJson))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(itemsJson);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private readonly record struct SteamInventoryRefreshJob(
        string SteamId,
        GameType GameType,
        SteamInventoryRefreshPriority Priority,
        SteamInventoryRefreshSource Source);

    private sealed class SteamInventoryFetchResult
    {
        public bool IsSuccess { get; private init; }
        public bool IsRateLimited { get; private init; }
        public string? ErrorCode { get; private init; }
        public string? ErrorMessage { get; private init; }
        public int? HttpStatusCode { get; private init; }
        public int? PageNumber { get; private init; }
        public int ParsedItemCount { get; private init; }
        public List<SteamInventoryItemDto> Items { get; private init; } = new();

        public static SteamInventoryFetchResult Success(List<SteamInventoryItemDto> items, int pageNumber, int parsedItemCount)
        {
            return new SteamInventoryFetchResult
            {
                IsSuccess = true,
                PageNumber = pageNumber,
                ParsedItemCount = parsedItemCount,
                Items = items
            };
        }

        public static SteamInventoryFetchResult Failed(
            string errorCode,
            string errorMessage,
            bool isRateLimited,
            int? httpStatusCode = null,
            int? pageNumber = null,
            int parsedItemCount = 0)
        {
            return new SteamInventoryFetchResult
            {
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                IsRateLimited = isRateLimited,
                HttpStatusCode = httpStatusCode,
                PageNumber = pageNumber,
                ParsedItemCount = parsedItemCount
            };
        }
    }
}
