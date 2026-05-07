using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;
using SkinMarket.Services;

namespace SkinMarket.Pages.Admin;

public class BotStatusModel : PageModel
{
    private static readonly string[] ActionablePurchaseStatuses =
    [
        "Pending",
        "BotPending",
        "AwaitingBotConfirmation",
        "TradeCreated",
        "AwaitingUserAction",
        "TradeAcceptedPendingReceipt",
        "InEscrow",
        "ReceivedByBot"
    ];
    private static readonly string[] CompletedPurchaseStatuses = ["Credited"];
    private static readonly string[] FailedPurchaseStatuses = ["Failed"];
    private static readonly string[] ActionableDeliveryStatuses =
    [
        "PendingDelivery",
        "DeliveryBotPending",
        "AwaitingBotConfirmation",
        "DeliveryTradeCreated",
        "AwaitingBuyerAction",
        "DeliveryInEscrow"
    ];
    private static readonly string[] CompletedDeliveryStatuses = ["Delivered"];
    private static readonly string[] FailedDeliveryStatuses = ["DeliveryFailed"];
    private const int DefaultHistoryLimit = 100;

    private readonly AppDbContext _dbContext;
    private readonly ISteamProfileService _steamProfileService;
    private readonly IBotServiceStatusClient _botServiceStatusClient;
    private readonly IAppLogReader _appLogReader;
    private readonly SteamBotOptions _steamBotOptions;

    public BotStatusModel(
        AppDbContext dbContext,
        ISteamProfileService steamProfileService,
        IBotServiceStatusClient botServiceStatusClient,
        IAppLogReader appLogReader,
        IOptions<SteamBotOptions> steamBotOptions)
    {
        _dbContext = dbContext;
        _steamProfileService = steamProfileService;
        _botServiceStatusClient = botServiceStatusClient;
        _appLogReader = appLogReader;
        _steamBotOptions = steamBotOptions.Value;
    }

    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = DefaultHistoryLimit;
    [BindProperty(SupportsGet = true)]
    public string? SearchTerm { get; set; }
    [BindProperty(SupportsGet = true)]
    public string HistoryMode { get; set; } = "all";

    public SteamProfileSummary? BotProfile { get; private set; }
    public List<BotPurchaseHistoryItem> PurchaseHistory { get; private set; } = new();
    public List<BotSaleHistoryItem> SaleHistory { get; private set; } = new();
    public BotServiceStatusSnapshot ServiceStatus { get; private set; } = new();
    public IReadOnlyList<AppLog> RecentAppIssues { get; private set; } = Array.Empty<AppLog>();
    public IReadOnlyList<AppLog> RecentWorkflowEntries { get; private set; } = Array.Empty<AppLog>();
    public string? ErrorMessage { get; private set; }
    public string BotSteamId => _steamBotOptions.BotSteamId;
    public string BotTradeUrl => _steamBotOptions.BotTradeUrl;
    public string ServiceUrl => _steamBotOptions.ServiceUrl;
    public bool BotEnabled => _steamBotOptions.Enabled;
    public string NotReadyReason => ResolveNotReadyReason();
    public bool HasActiveHistoryFilters =>
        !string.IsNullOrWhiteSpace(SearchTerm) ||
        !string.Equals(HistoryMode, "all", StringComparison.OrdinalIgnoreCase) ||
        Limit != DefaultHistoryLimit;

    public virtual async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Limit = NormalizeLimit(Limit);
        SearchTerm = NormalizeSearchTerm(SearchTerm);
        HistoryMode = NormalizeHistoryMode(HistoryMode);
        var take = Limit;
        ServiceStatus = await _botServiceStatusClient.GetStatusAsync(cancellationToken);
        RecentAppIssues = LoadRecentAppIssues();
        RecentWorkflowEntries = LoadRecentWorkflowEntries(take);
        await LoadBotStatusAsync(cancellationToken);
        await LoadHistoryAsync(take, cancellationToken);
    }

    private async Task LoadBotStatusAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_steamBotOptions.BotSteamId))
        {
            ErrorMessage = "SteamBot:BotSteamId is not configured.";
            return;
        }

        BotProfile = await _steamProfileService.GetProfileAsync(_steamBotOptions.BotSteamId, cancellationToken);
    }

    private IReadOnlyList<AppLog> LoadRecentAppIssues()
    {
        var recent = _appLogReader.GetRecent(80, sources: BotDiagnosticsCatalog.AppLogSources);
        return BotDiagnosticsCatalog.FilterImportantAppEntries(recent, 12);
    }

    private IReadOnlyList<AppLog> LoadRecentWorkflowEntries(int take)
    {
        return _appLogReader.GetRecent(Math.Max(take, 40), sources: BotDiagnosticsCatalog.WorkflowLogSources);
    }

    protected async Task LoadHistoryAsync(int take, CancellationToken cancellationToken)
    {
        var purchaseQuery = ApplyPurchaseHistoryFilters(_dbContext.TradeOperations.AsNoTracking());
        PurchaseHistory = await purchaseQuery
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(take)
            .Select(item => new BotPurchaseHistoryItem
            {
                Id = item.Id,
                ItemName = item.ItemName,
                AssetId = item.AssetId,
                SellerDisplayName = item.AppUser != null
                    ? (item.AppUser.PersonaName ?? item.AppUser.DisplayName)
                    : item.SteamId,
                SellerSteamId = item.SteamId,
                SellerAvatarUrl = item.AppUser != null ? item.AppUser.AvatarUrl : null,
                Status = item.Status,
                CreditAmount = item.CreditAmount,
                CreatedAtUtc = item.CreatedAtUtc,
                UpdatedAtUtc = item.UpdatedAtUtc,
                ReceivedByBotAtUtc = item.ReceivedByBotAtUtc,
                CreditedAtUtc = item.CreditedAtUtc,
                TradeOfferId = item.TradeOfferId,
                ErrorMessage = item.ErrorMessage
            })
            .ToListAsync(cancellationToken);

        var saleQuery = ApplySaleHistoryFilters(_dbContext.MarketPurchaseRecords.AsNoTracking());
        SaleHistory = await saleQuery
            .OrderByDescending(item => item.PurchasedAtUtc ?? item.CreatedAtUtc)
            .Take(take)
            .Select(item => new BotSaleHistoryItem
            {
                Id = item.Id,
                ItemName = item.ItemName,
                AssetId = item.AssetId,
                BuyerDisplayName = item.BuyerAppUser != null
                    ? (item.BuyerAppUser.PersonaName ?? item.BuyerAppUser.DisplayName)
                    : "Unknown buyer",
                BuyerSteamId = item.BuyerAppUser != null ? item.BuyerAppUser.SteamId : string.Empty,
                BuyerAvatarUrl = item.BuyerAppUser != null ? item.BuyerAppUser.AvatarUrl : null,
                SourceSellerDisplayName = item.SourceTradeOperation != null && item.SourceTradeOperation.AppUser != null
                    ? (item.SourceTradeOperation.AppUser.PersonaName ?? item.SourceTradeOperation.AppUser.DisplayName)
                    : null,
                Status = item.Status,
                DeliveryStatus = item.DeliveryStatus,
                Price = item.Price,
                CreatedAtUtc = item.CreatedAtUtc,
                PurchasedAtUtc = item.PurchasedAtUtc,
                UpdatedAtUtc = item.UpdatedAtUtc,
                DeliveryTradeOfferId = item.DeliveryTradeOfferId,
                DeliveredAtUtc = item.DeliveredAtUtc,
                DeliveryErrorMessage = item.DeliveryErrorMessage
            })
            .ToListAsync(cancellationToken);
    }

    private IQueryable<TradeOperation> ApplyPurchaseHistoryFilters(IQueryable<TradeOperation> query)
    {
        var rawSearchTerm = SearchTerm;
        var normalizedSearchTerm = rawSearchTerm?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(rawSearchTerm) && !string.IsNullOrWhiteSpace(normalizedSearchTerm))
        {
            query = query.Where(item =>
                item.ItemName.ToLower().Contains(normalizedSearchTerm) ||
                item.AssetId.Contains(rawSearchTerm) ||
                item.SteamId.Contains(rawSearchTerm) ||
                item.Status.ToLower().Contains(normalizedSearchTerm) ||
                (item.TradeOfferId != null && item.TradeOfferId.Contains(rawSearchTerm)) ||
                (item.ErrorMessage != null && item.ErrorMessage.ToLower().Contains(normalizedSearchTerm)) ||
                (item.AppUser != null && (
                    item.AppUser.DisplayName.ToLower().Contains(normalizedSearchTerm) ||
                    (item.AppUser.PersonaName != null && item.AppUser.PersonaName.ToLower().Contains(normalizedSearchTerm))
                )));
        }

        return NormalizeHistoryMode(HistoryMode) switch
        {
            "actionable" => query.Where(item => ActionablePurchaseStatuses.Contains(item.Status)),
            "failed" => query.Where(item => FailedPurchaseStatuses.Contains(item.Status)),
            "completed" => query.Where(item => CompletedPurchaseStatuses.Contains(item.Status)),
            _ => query
        };
    }

    private IQueryable<MarketPurchaseRecord> ApplySaleHistoryFilters(IQueryable<MarketPurchaseRecord> query)
    {
        var rawSearchTerm = SearchTerm;
        var normalizedSearchTerm = rawSearchTerm?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(rawSearchTerm) && !string.IsNullOrWhiteSpace(normalizedSearchTerm))
        {
            query = query.Where(item =>
                item.ItemName.ToLower().Contains(normalizedSearchTerm) ||
                item.AssetId.Contains(rawSearchTerm) ||
                item.Status.ToLower().Contains(normalizedSearchTerm) ||
                (item.DeliveryStatus != null && item.DeliveryStatus.ToLower().Contains(normalizedSearchTerm)) ||
                (item.DeliveryTradeOfferId != null && item.DeliveryTradeOfferId.Contains(rawSearchTerm)) ||
                (item.DeliveryErrorMessage != null && item.DeliveryErrorMessage.ToLower().Contains(normalizedSearchTerm)) ||
                (item.BuyerAppUser != null && (
                    item.BuyerAppUser.SteamId.Contains(rawSearchTerm) ||
                    item.BuyerAppUser.DisplayName.ToLower().Contains(normalizedSearchTerm) ||
                    (item.BuyerAppUser.PersonaName != null && item.BuyerAppUser.PersonaName.ToLower().Contains(normalizedSearchTerm))
                )) ||
                (item.SourceTradeOperation != null && item.SourceTradeOperation.AppUser != null && (
                    item.SourceTradeOperation.AppUser.DisplayName.ToLower().Contains(normalizedSearchTerm) ||
                    (item.SourceTradeOperation.AppUser.PersonaName != null &&
                     item.SourceTradeOperation.AppUser.PersonaName.ToLower().Contains(normalizedSearchTerm))
                )));
        }

        return NormalizeHistoryMode(HistoryMode) switch
        {
            "actionable" => query.Where(item => item.DeliveryStatus == null || ActionableDeliveryStatuses.Contains(item.DeliveryStatus)),
            "failed" => query.Where(item => item.DeliveryStatus != null && FailedDeliveryStatuses.Contains(item.DeliveryStatus)),
            "completed" => query.Where(item => item.DeliveryStatus != null && CompletedDeliveryStatuses.Contains(item.DeliveryStatus)),
            _ => query
        };
    }

    public bool CanOpenSellerOffer(BotPurchaseHistoryItem item)
    {
        return !string.IsNullOrWhiteSpace(item.TradeOfferId);
    }

    public bool CanOpenBuyerOffer(BotSaleHistoryItem item)
    {
        return !string.IsNullOrWhiteSpace(item.DeliveryTradeOfferId);
    }

    private string ResolveNotReadyReason()
    {
        if (!ServiceStatus.Reachable)
        {
            return "BotServiceHttpUnavailable";
        }

        if (ServiceStatus.Bot.Ready)
        {
            return "Ready";
        }

        return string.IsNullOrWhiteSpace(ServiceStatus.Bot.NotReadyReason)
            ? "BotServiceReturnedNotReady"
            : ServiceStatus.Bot.NotReadyReason;
    }

    protected static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            return DefaultHistoryLimit;
        }

        return Math.Min(limit, 500);
    }

    protected static string? NormalizeSearchTerm(string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return null;
        }

        return searchTerm.Trim();
    }

    protected static string NormalizeHistoryMode(string? historyMode)
    {
        return historyMode?.Trim().ToLowerInvariant() switch
        {
            "actionable" => "actionable",
            "failed" => "failed",
            "completed" => "completed",
            _ => "all"
        };
    }
}
