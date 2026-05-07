using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages.Admin;

public class TradeDiagnosticsModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public TradeDiagnosticsModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty(SupportsGet = true)]
    public string Filter { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = 100;

    public List<TradeOperation> TradeOperations { get; private set; } = new();
    public List<MarketPurchaseRecord> PurchaseRecords { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var take = Limit <= 0 ? 100 : Math.Min(Limit, 500);
        var normalizedFilter = string.IsNullOrWhiteSpace(Filter) ? "all" : Filter.Trim().ToLowerInvariant();
        Filter = normalizedFilter;

        var tradeQuery = _dbContext.TradeOperations.AsNoTracking();
        var purchaseQuery = _dbContext.MarketPurchaseRecords.AsNoTracking();

        switch (normalizedFilter)
        {
            case "stuck-pending":
                tradeQuery = tradeQuery.Where(item => item.Status == "Pending" && item.UpdatedAtUtc <= now.AddMinutes(-1));
                purchaseQuery = purchaseQuery.Where(item => item.Status == "Sold" && item.DeliveryStatus == "PendingDelivery" && item.UpdatedAtUtc <= now.AddMinutes(-1));
                break;
            case "botpending":
                tradeQuery = tradeQuery.Where(item => item.Status == "BotPending" && item.UpdatedAtUtc <= now.AddMinutes(-1));
                purchaseQuery = purchaseQuery.Where(item => item.DeliveryStatus == "DeliveryBotPending" && item.UpdatedAtUtc <= now.AddMinutes(-1));
                break;
            case "awaiting-bot-confirmation":
                tradeQuery = tradeQuery.Where(item => item.Status == "AwaitingBotConfirmation" && item.UpdatedAtUtc <= now.AddMinutes(-5));
                purchaseQuery = purchaseQuery.Where(item => item.DeliveryStatus == "AwaitingBotConfirmation" && item.UpdatedAtUtc <= now.AddMinutes(-5));
                break;
            case "awaiting-user-action":
                tradeQuery = tradeQuery.Where(item => item.Status == "AwaitingUserAction" && item.UpdatedAtUtc <= now.AddMinutes(-15));
                purchaseQuery = purchaseQuery.Where(item => item.DeliveryStatus == "AwaitingBuyerAction" && item.UpdatedAtUtc <= now.AddMinutes(-15));
                break;
            case "delivery-failed":
                tradeQuery = tradeQuery.Where(item => false);
                purchaseQuery = purchaseQuery.Where(item => item.DeliveryStatus == "DeliveryFailed");
                break;
            case "sold-without-delivery-progress":
                tradeQuery = tradeQuery.Where(item => false);
                purchaseQuery = purchaseQuery.Where(item =>
                    item.Status == "Sold" &&
                    (item.DeliveryStatus == null || item.DeliveryStatus == "PendingDelivery") &&
                    item.UpdatedAtUtc <= now.AddMinutes(-1));
                break;
        }

        TradeOperations = await tradeQuery
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);
        PurchaseRecords = await purchaseQuery
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);
    }
}
