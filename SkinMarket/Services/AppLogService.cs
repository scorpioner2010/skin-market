using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class AppLogService : IAppLogService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<AppLogService> _logger;

    public AppLogService(AppDbContext dbContext, ILogger<AppLogService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task WriteAsync(string level, string message, string? source = null, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _dbContext.Logs.Add(new AppLog
            {
                Id = Guid.NewGuid(),
                TimestampUtc = DateTime.UtcNow,
                Level = string.IsNullOrWhiteSpace(level) ? "Info" : level.Trim(),
                Message = message.Trim(),
                Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
                StackTrace = exception?.ToString()
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception logException)
        {
            _logger.LogError(logException, "Failed to persist application log. Level={Level} Source={Source} Message={Message}", level, source, message);
        }
    }
}
