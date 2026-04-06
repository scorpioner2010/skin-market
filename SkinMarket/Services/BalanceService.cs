using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;

namespace SkinMarket.Services;

public class BalanceService : IBalanceService
{
    private readonly AppDbContext _dbContext;

    public BalanceService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<decimal> GetBalanceAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AppUsers
            .Where(user => user.Id == userId)
            .Select(user => user.Balance)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
