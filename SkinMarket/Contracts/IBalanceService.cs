using System.Threading;

namespace SkinMarket.Contracts;

public interface IBalanceService
{
    Task<decimal> GetBalanceAsync(Guid userId, CancellationToken cancellationToken = default);
}
