namespace SkinMarket.Contracts;

public interface IAppLogService
{
    Task WriteAsync(string level, string message, string? source = null, Exception? exception = null, CancellationToken cancellationToken = default);
}
