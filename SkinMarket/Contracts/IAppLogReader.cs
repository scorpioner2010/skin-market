using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IAppLogReader
{
    IReadOnlyList<AppLog> GetRecent(int limit = 100, string? level = null, IReadOnlyCollection<string>? sources = null);

    Task<IReadOnlyList<AppLog>> GetRecentAsync(
        int limit = 100,
        string? level = null,
        IReadOnlyCollection<string>? sources = null,
        CancellationToken cancellationToken = default);
}
