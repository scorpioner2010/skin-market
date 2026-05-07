using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IBotServiceStatusClient
{
    Task<BotServiceStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<BotServiceLogSnapshot> GetLogsAsync(BotServiceLogQuery query, CancellationToken cancellationToken = default);
}
