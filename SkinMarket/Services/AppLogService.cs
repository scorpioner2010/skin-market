using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class AppLogService : IAppLogService, IAppLogReader
{
    private const int MaxEntries = 2000;
    private const int MaxMessageLength = 6000;
    private const int MaxDetailsLength = 16000;
    private readonly object _sync = new();
    private readonly LinkedList<AppLog> _entries = new();

    public Task WriteAsync(string level, string message, string? source = null, Exception? exception = null, CancellationToken cancellationToken = default)
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

        return Task.CompletedTask;
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
}
