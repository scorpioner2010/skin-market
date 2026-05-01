using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class AppLogService : IAppLogService, IAppLogReader
{
    private const int MaxEntries = 2000;
    private const int MaxMessageLength = 6000;
    private const int MaxDetailsLength = 16000;
    private const int PersistedMessageLength = 4000;
    private const int PersistedDetailsLength = 12000;
    private static readonly HashSet<string> PersistedSources = new(StringComparer.Ordinal)
    {
        nameof(SteamInventoryRefreshWorker),
        "AdminSteamInventoryDiagnostic"
    };

    private readonly object _sync = new();
    private readonly LinkedList<AppLog> _entries = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppLogService> _logger;

    public AppLogService(IServiceScopeFactory scopeFactory, ILogger<AppLogService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task WriteAsync(string level, string message, string? source = null, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        var entry = new AppLog
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTime.UtcNow,
            Level = string.IsNullOrWhiteSpace(level) ? "Info" : level.Trim(),
            Message = Truncate(string.IsNullOrWhiteSpace(message) ? "<empty>" : message.Trim(), MaxMessageLength),
            Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
            StackTrace = exception is null ? null : Truncate(exception.ToString(), MaxDetailsLength)
        };

        lock (_sync)
        {
            _entries.AddLast(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveFirst();
            }
        }

        if (!ShouldPersist(entry.Source))
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Logs.Add(new AppLog
            {
                Id = entry.Id,
                TimestampUtc = entry.TimestampUtc,
                Level = Truncate(entry.Level, 20),
                Message = Truncate(entry.Message, PersistedMessageLength),
                Source = string.IsNullOrWhiteSpace(entry.Source) ? null : Truncate(entry.Source, 200),
                StackTrace = entry.StackTrace is null ? null : Truncate(entry.StackTrace, PersistedDetailsLength)
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exceptionToLog)
        {
            _logger.LogWarning(
                exceptionToLog,
                "Failed to persist app log entry for source {Source}.",
                entry.Source ?? "<null>");
        }
    }

    public IReadOnlyList<AppLog> GetRecent(int limit = 100, string? level = null, IReadOnlyCollection<string>? sources = null)
    {
        var take = limit <= 0 ? 100 : Math.Min(limit, 500);
        AppLog[] snapshot;
        lock (_sync)
        {
            snapshot = _entries.ToArray();
        }

        IEnumerable<AppLog> query = snapshot;
        if (!string.IsNullOrWhiteSpace(level))
        {
            query = query.Where(item => string.Equals(item.Level, level.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (sources is { Count: > 0 })
        {
            var sourceSet = new HashSet<string>(sources, StringComparer.Ordinal);
            query = query.Where(item => item.Source is not null && sourceSet.Contains(item.Source));
        }

        return query
            .OrderByDescending(item => item.TimestampUtc)
            .Take(take)
            .Select(Clone)
            .ToList();
    }

    public async Task<IReadOnlyList<AppLog>> GetRecentAsync(
        int limit = 100,
        string? level = null,
        IReadOnlyCollection<string>? sources = null,
        CancellationToken cancellationToken = default)
    {
        var take = limit <= 0 ? 100 : Math.Min(limit, 500);
        var memoryEntries = GetRecent(take, level, sources);
        var persistedEntries = new List<AppLog>();

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var query = dbContext.Logs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(level))
            {
                var normalizedLevel = level.Trim();
                query = query.Where(item => item.Level == normalizedLevel);
            }

            if (sources is { Count: > 0 })
            {
                var sourceArray = sources.ToArray();
                query = query.Where(item => item.Source != null && sourceArray.Contains(item.Source));
            }

            persistedEntries = await query
                .OrderByDescending(item => item.TimestampUtc)
                .Take(take)
                .Select(item => new AppLog
                {
                    Id = item.Id,
                    TimestampUtc = item.TimestampUtc,
                    Level = item.Level,
                    Message = item.Message,
                    Source = item.Source,
                    StackTrace = item.StackTrace
                })
                .ToListAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read persisted app logs.");
        }

        return memoryEntries
            .Concat(persistedEntries)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .OrderByDescending(item => item.TimestampUtc)
            .Take(take)
            .ToList();
    }

    private static AppLog Clone(AppLog item)
    {
        return new AppLog
        {
            Id = item.Id,
            TimestampUtc = item.TimestampUtc,
            Level = item.Level,
            Message = item.Message,
            Source = item.Source,
            StackTrace = item.StackTrace
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private static bool ShouldPersist(string? source)
    {
        return !string.IsNullOrWhiteSpace(source) && PersistedSources.Contains(source);
    }
}
