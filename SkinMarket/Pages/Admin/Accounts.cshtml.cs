using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages.Admin;

public class AccountsModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IAppLogService _appLogService;

    public AccountsModel(AppDbContext dbContext, IAppLogService appLogService)
    {
        _dbContext = dbContext;
        _appLogService = appLogService;
    }

    public List<AdminAccountItem> Accounts { get; private set; } = new();
    [TempData]
    public string? SuccessMessage { get; set; }
    [TempData]
    public string? ErrorMessage { get; set; }
    [BindProperty]
    public BalanceInputModel BalanceInput { get; set; } = new();
    [BindProperty]
    public Guid DeleteUserId { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAccountsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostUpdateBalanceAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = "Balance value is invalid.";
            return RedirectToPage();
        }

        var user = await _dbContext.AppUsers
            .SingleOrDefaultAsync(item => item.Id == BalanceInput.UserId, cancellationToken);
        if (user is null)
        {
            ErrorMessage = "Account was not found.";
            return RedirectToPage();
        }

        var newBalance = decimal.Round(BalanceInput.Balance, 2, MidpointRounding.AwayFromZero);
        var delta = newBalance - user.Balance;
        if (delta == 0)
        {
            SuccessMessage = "Balance is already set to this value.";
            return RedirectToPage();
        }

        user.Balance = newBalance;
        _dbContext.BalanceTransactions.Add(new BalanceTransaction
        {
            Id = Guid.NewGuid(),
            AppUserId = user.Id,
            Amount = delta,
            Type = "AdminBalanceAdjustment",
            CreatedAtUtc = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _appLogService.WriteAsync(
            "Warning",
            $"Admin balance adjustment. AppUserId={user.Id}; SteamId={user.SteamId}; Delta={delta:0.00}; NewBalance={newBalance:0.00}",
            nameof(AccountsModel),
            cancellationToken: cancellationToken);

        SuccessMessage = $"Balance updated for {ResolveAccountName(user)}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        var user = await _dbContext.AppUsers
            .SingleOrDefaultAsync(item => item.Id == DeleteUserId, cancellationToken);
        if (user is null)
        {
            ErrorMessage = "Account was not found.";
            return RedirectToPage();
        }

        var userTradeOperationIds = await _dbContext.TradeOperations
            .Where(item => item.AppUserId == user.Id)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var relatedPurchaseRecords = await _dbContext.MarketPurchaseRecords
            .Where(item =>
                item.BuyerAppUserId == user.Id ||
                (item.SourceTradeOperationId != null && userTradeOperationIds.Contains(item.SourceTradeOperationId.Value)))
            .ToListAsync(cancellationToken);
        _dbContext.MarketPurchaseRecords.RemoveRange(relatedPurchaseRecords);

        var balanceTransactions = await _dbContext.BalanceTransactions
            .Where(item => item.AppUserId == user.Id)
            .ToListAsync(cancellationToken);
        _dbContext.BalanceTransactions.RemoveRange(balanceTransactions);

        var tradeOperations = await _dbContext.TradeOperations
            .Where(item => item.AppUserId == user.Id)
            .ToListAsync(cancellationToken);
        _dbContext.TradeOperations.RemoveRange(tradeOperations);

        _dbContext.AppUsers.Remove(user);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _appLogService.WriteAsync(
            "Warning",
            $"Admin deleted account. AppUserId={user.Id}; SteamId={user.SteamId}; Name={ResolveAccountName(user)}; BalanceTransactions={balanceTransactions.Count}; TradeOperations={tradeOperations.Count}; MarketPurchaseRecords={relatedPurchaseRecords.Count}",
            nameof(AccountsModel),
            cancellationToken: cancellationToken);

        SuccessMessage = $"Account {ResolveAccountName(user)} was deleted.";
        return RedirectToPage();
    }

    private async Task LoadAccountsAsync(CancellationToken cancellationToken)
    {
        Accounts = await _dbContext.AppUsers
            .AsNoTracking()
            .OrderBy(item => item.CreatedAtUtc)
            .Select(user => new AdminAccountItem
            {
                Id = user.Id,
                SteamId = user.SteamId,
                DisplayName = user.DisplayName,
                PersonaName = user.PersonaName,
                AvatarUrl = user.AvatarUrl,
                IsAdmin = user.IsAdmin,
                Balance = user.Balance,
                CreatedAtUtc = user.CreatedAtUtc,
                TradeOperationCount = _dbContext.TradeOperations.Count(operation => operation.AppUserId == user.Id),
                PurchaseCount = _dbContext.MarketPurchaseRecords.Count(record => record.BuyerAppUserId == user.Id),
                BalanceTransactionCount = _dbContext.BalanceTransactions.Count(transaction => transaction.AppUserId == user.Id)
            })
            .ToListAsync(cancellationToken);
    }

    private static string ResolveAccountName(AppUser user)
    {
        return string.IsNullOrWhiteSpace(user.PersonaName)
            ? user.DisplayName
            : user.PersonaName;
    }

    public sealed class AdminAccountItem
    {
        public Guid Id { get; init; }
        public string SteamId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? PersonaName { get; init; }
        public string? AvatarUrl { get; init; }
        public bool IsAdmin { get; init; }
        public decimal Balance { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public int TradeOperationCount { get; init; }
        public int PurchaseCount { get; init; }
        public int BalanceTransactionCount { get; init; }
        public string Name => string.IsNullOrWhiteSpace(PersonaName) ? DisplayName : PersonaName;
    }

    public sealed class BalanceInputModel
    {
        [Required]
        public Guid UserId { get; set; }
        [Range(-1_000_000, 1_000_000)]
        public decimal Balance { get; set; }
    }
}
