using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IHistoryService
{
    Task<HistoryPageData?> GetHistoryAsync(Guid appUserId, CancellationToken cancellationToken = default);
}
