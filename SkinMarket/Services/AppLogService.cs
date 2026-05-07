using System.Text.Json;
using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class AppLogService : IAppLogService, IAppLogReader
{
    private const int MaxEntries = 2000;
    private const int MaxMessageLength = 6000;
    private const int MaxDetailsLength = 16000;
    private const int PersistedMessageLength = 4000;
    private const int PersistedDetailsLength = 12000;
    private const int MaxFileScanMultiplier = 100;
    private const int MaxFileScanLines = 10000;
    private const int MaxLogFiles = 14;
    private const long MaxLogFileBytes = 10L * 1024L * 1024L;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _fileWriteLock = new(1, 1);
    private readonly LinkedList<AppLog> _entries = new();
    private readonly string _logDirectory;
    private readonly ILogger<AppLogService> _logger;

    public AppLogService(IHostEnvironment environment, ILogger<AppLogService> logger)
    {
        _logger = logger;
        _logDirectory = Path.Combine(environment.ContentRootPath, "logs", "app");
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

        await WriteFileSafeAsync(entry, cancellationToken);
    }

    public IReadOnlyList<AppLog> GetRecent(int limit = 100, string? level = null, IReadOnlyCollection<string>? sources = null)
    {
        var take = NormalizeLimit(limit);
        var memoryEntries = GetRecentMemoryEntries(take, level, sources);
        var fileEntries = ReadRecentFileEntries(take, level, sources);

        return memoryEntries
            .Concat(fileEntries)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .OrderByDescending(item => item.TimestampUtc)
            .Take(take)
            .ToList();
    }

    public Task<IReadOnlyList<AppLog>> GetRecentAsync(
        int limit = 100,
        string? level = null,
        IReadOnlyCollection<string>? sources = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetRecent(limit, level, sources));
    }

    private IReadOnlyList<AppLog> GetRecentMemoryEntries(int limit, string? level, IReadOnlyCollection<string>? sources)
    {
        AppLog[] snapshot;
        lock (_sync)
        {
            snapshot = _entries.ToArray();
        }

        return Filter(snapshot, level, sources)
            .OrderByDescending(item => item.TimestampUtc)
            .Take(limit)
            .Select(Clone)
            .ToList();
    }

    private IReadOnlyList<AppLog> ReadRecentFileEntries(int limit, string? level, IReadOnlyCollection<string>? sources)
    {
        if (!Directory.Exists(_logDirectory))
        {
            return Array.Empty<AppLog>();
        }

        var scanLimit = Math.Max(limit, Math.Min(MaxFileScanLines, limit * MaxFileScanMultiplier));
        var results = new List<AppLog>(Math.Min(scanLimit, Math.Max(limit, 1)));

        foreach (var file in EnumerateRecentLogFiles())
        {
            foreach (var line in ReadTailLines(file.FullName, scanLimit))
            {
                var item = DeserializeEntry(line);
                if (item is null || !Matches(item, level, sources))
                {
                    continue;
                }

                results.Add(item);
                if (results.Count >= scanLimit)
                {
                    break;
                }
            }

            if (results.Count >= scanLimit)
            {
                break;
            }
        }

        return results
            .OrderByDescending(item => item.TimestampUtc)
            .Take(limit)
            .ToList();
    }

    private async Task WriteFileSafeAsync(AppLog entry, CancellationToken cancellationToken)
    {
        try
        {
            await _fileWriteLock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(_logDirectory);
                PruneOldFiles();
                var filePath = ResolveCurrentFilePath();
                var fileEntry = new AppLog
                {
                    Id = entry.Id,
                    TimestampUtc = entry.TimestampUtc,
                    Level = Truncate(entry.Level, 20),
                    Message = Truncate(entry.Message, PersistedMessageLength),
                    Source = string.IsNullOrWhiteSpace(entry.Source) ? null : Truncate(entry.Source, 200),
                    StackTrace = entry.StackTrace is null ? null : Truncate(entry.StackTrace, PersistedDetailsLength)
                };
                var json = JsonSerializer.Serialize(fileEntry, JsonOptions);
                await File.AppendAllTextAsync(filePath, json + Environment.NewLine, cancellationToken);
            }
            finally
            {
                _fileWriteLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist app log entry to server file storage.");
        }
    }

    private string ResolveCurrentFilePath()
    {
        var baseName = $"app-logs-{DateTime.UtcNow:yyyy-MM-dd}";
        var filePath = Path.Combine(_logDirectory, $"{baseName}.log");
        if (!File.Exists(filePath) || new FileInfo(filePath).Length < MaxLogFileBytes)
        {
            return filePath;
        }

        for (var index = 1; index < 100; index++)
        {
            var candidate = Path.Combine(_logDirectory, $"{baseName}.{index}.log");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length < MaxLogFileBytes)
            {
                return candidate;
            }
        }

        return filePath;
    }

    private IEnumerable<FileInfo> EnumerateRecentLogFiles()
    {
        try
        {
            return Directory
                .EnumerateFiles(_logDirectory, "app-logs-*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxLogFiles)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Failed to enumerate app log files.");
            return Array.Empty<FileInfo>();
        }
    }

    private void PruneOldFiles()
    {
        var files = Directory.GetFiles(_logDirectory, "app-logs-*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(MaxLogFiles)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Failed to prune old app log file {FilePath}.", file.FullName);
            }
        }
    }

    private IEnumerable<string> ReadTailLines(string filePath, int limit)
    {
        var queue = new Queue<string>(Math.Max(1, limit));
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                queue.Enqueue(line);
                while (queue.Count > limit)
                {
                    queue.Dequeue();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogDebug(exception, "Failed to read app log file {FilePath}.", filePath);
        }

        return queue.Reverse();
    }

    private static IEnumerable<AppLog> Filter(IEnumerable<AppLog> entries, string? level, IReadOnlyCollection<string>? sources)
    {
        return entries.Where(item => Matches(item, level, sources));
    }

    private static bool Matches(AppLog item, string? level, IReadOnlyCollection<string>? sources)
    {
        if (!string.IsNullOrWhiteSpace(level) &&
            !string.Equals(item.Level, level.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (sources is { Count: > 0 })
        {
            var sourceSet = new HashSet<string>(sources, StringComparer.Ordinal);
            if (item.Source is null || !sourceSet.Contains(item.Source))
            {
                return false;
            }
        }

        return true;
    }

    private static AppLog? DeserializeEntry(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AppLog>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static int NormalizeLimit(int limit)
    {
        return limit <= 0 ? 100 : Math.Min(limit, 500);
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
