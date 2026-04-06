using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Pages;

public class HistoryModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IHistoryService _historyService;
    private readonly AppRuntimeState _runtimeState;

    public HistoryModel(AppDbContext dbContext, IHistoryService historyService, AppRuntimeState runtimeState)
    {
        _dbContext = dbContext;
        _historyService = historyService;
        _runtimeState = runtimeState;
    }

    public decimal CurrentBalance { get; private set; }
    public List<SaleHistoryItem> Sales { get; private set; } = new();
    public List<PurchaseHistoryItem> Purchases { get; private set; } = new();
    public List<BalanceHistoryItem> BalanceTransactions { get; private set; } = new();
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return;
        }

        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return;
        }

        var steamId = User.FindFirst("SteamId")?.Value;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return;
        }

        var userId = await _dbContext.AppUsers
            .AsNoTracking()
            .Where(user => user.SteamId == steamId)
            .Select(user => (Guid?)user.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (!userId.HasValue)
        {
            return;
        }

        var history = await _historyService.GetHistoryAsync(userId.Value, cancellationToken);
        if (history is null)
        {
            return;
        }

        CurrentBalance = history.CurrentBalance;
        Sales = history.Sales;
        Purchases = history.Purchases;
        BalanceTransactions = history.BalanceTransactions;
    }
}
