using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;
using SkinMarket.Pages;
using SkinMarket.Services;

namespace SkinMarket.Pages.Admin;

public class BotStatusModel : PageModel
{
    private static readonly TimeSpan ProtectedInventoryFallbackWindow = TimeSpan.FromDays(14);
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
    private readonly ISteamBotInventoryClient _steamBotInventoryClient;
    private readonly ISteamInventoryService _steamInventoryService;
    private readonly ISteamTradeClient _steamTradeClient;
    private readonly ISteamBotIntakeService _steamBotIntakeService;
    private readonly ICreditService _creditService;
    private readonly IMarketDeliveryService _marketDeliveryService;
    private readonly IAppLogService _appLogService;
    private readonly IBotServiceStatusClient _botServiceStatusClient;
    private readonly IAppLogReader _appLogReader;
    private readonly IGameCatalog _gameCatalog;
    private readonly SteamBotOptions _steamBotOptions;

    public BotStatusModel(
        AppDbContext dbContext,
        ISteamProfileService steamProfileService,
        ISteamBotInventoryClient steamBotInventoryClient,
        ISteamInventoryService steamInventoryService,
        ISteamTradeClient steamTradeClient,
        ISteamBotIntakeService steamBotIntakeService,
        ICreditService creditService,
        IMarketDeliveryService marketDeliveryService,
        IAppLogService appLogService,
        IBotServiceStatusClient botServiceStatusClient,
        IAppLogReader appLogReader,
        IGameCatalog gameCatalog,
        IOptions<SteamBotOptions> steamBotOptions)
    {
        _dbContext = dbContext;
        _steamProfileService = steamProfileService;
        _steamBotInventoryClient = steamBotInventoryClient;
        _steamInventoryService = steamInventoryService;
        _steamTradeClient = steamTradeClient;
        _steamBotIntakeService = steamBotIntakeService;
        _creditService = creditService;
        _marketDeliveryService = marketDeliveryService;
        _appLogService = appLogService;
        _botServiceStatusClient = botServiceStatusClient;
        _appLogReader = appLogReader;
        _gameCatalog = gameCatalog;
        _steamBotOptions = steamBotOptions.Value;
    }

    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = DefaultHistoryLimit;
    [BindProperty(SupportsGet = true)]
    public string? SearchTerm { get; set; }
    [BindProperty(SupportsGet = true)]
    public string HistoryMode { get; set; } = "all";
    [BindProperty]
    public CancelTradeInputModel CancelInput { get; set; } = new();

    public SteamProfileSummary? BotProfile { get; private set; }
    public List<BotPurchaseHistoryItem> PurchaseHistory { get; private set; } = new();
    public List<BotSaleHistoryItem> SaleHistory { get; private set; } = new();
    public BotServiceStatusSnapshot ServiceStatus { get; private set; } = new();
    public IReadOnlyList<AppLog> RecentAppIssues { get; private set; } = Array.Empty<AppLog>();
    public IReadOnlyList<AppLog> RecentWorkflowEntries { get; private set; } = Array.Empty<AppLog>();
    public int InventoryItemCount { get; private set; }
    public int AvailableMarketItemCount { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? InventoryErrorMessage { get; private set; }
    public string BotSteamId => _steamBotOptions.BotSteamId;
    public string BotTradeUrl => _steamBotOptions.BotTradeUrl;
    public string ServiceUrl => _steamBotOptions.ServiceUrl;
    public bool BotEnabled => _steamBotOptions.Enabled;
    [TempData]
    public string? SuccessMessage { get; set; }
    [TempData]
    public string? ActionErrorMessage { get; set; }
    public bool HasActiveHistoryFilters =>
        !string.IsNullOrWhiteSpace(SearchTerm) ||
        !string.Equals(HistoryMode, "all", StringComparison.OrdinalIgnoreCase) ||
        Limit != DefaultHistoryLimit;

    public async Task OnGetAsync(CancellationToken cancellationToken)
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

    public async Task<IActionResult> OnPostCreateIntakeAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var operation = await _dbContext.TradeOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == entityId, cancellationToken);
        if (operation is null)
        {
            ActionErrorMessage = "Intake trade was not found.";
            return RedirectToCurrentPage();
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Admin intake creation requested. TradeOperationId={operation.Id}; Status={operation.Status}; SellerAppUserId={operation.AppUserId}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        var result = await _steamBotIntakeService.CreateIntakeRequestAsync(operation.Id, operation.AppUserId, cancellationToken);
        await _appLogService.WriteAsync(
            result.Success ? "Info" : "Warning",
            $"Admin intake creation finished. TradeOperationId={operation.Id}; Success={result.Success}; Status={result.NewStatus}; OfferId={result.TradeOfferId ?? "<null>"}; Message={result.Message}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        if (result.Success)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ActionErrorMessage = result.Message;
        }

        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostConfirmIntakeAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var operation = await _dbContext.TradeOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == entityId, cancellationToken);
        if (operation is null)
        {
            ActionErrorMessage = "Intake trade was not found.";
            return RedirectToCurrentPage();
        }

        if (string.IsNullOrWhiteSpace(operation.TradeOfferId))
        {
            ActionErrorMessage = "Intake trade does not have a Steam offer yet.";
            return RedirectToCurrentPage();
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Admin bot confirmation retry requested for intake. TradeOperationId={operation.Id}; OfferId={operation.TradeOfferId}; Status={operation.Status}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        var result = await _steamTradeClient.ConfirmOfferAsync(operation.TradeOfferId, "intake", cancellationToken);
        await _appLogService.WriteAsync(
            result.Success ? "Info" : "Warning",
            $"Admin bot confirmation retry finished for intake. TradeOperationId={operation.Id}; OfferId={result.OfferId}; Success={result.Success}; State={result.State ?? "<null>"}; Message={result.Message}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        if (result.Success)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ActionErrorMessage = result.Message;
        }

        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostRefreshIntakeAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var operation = await _dbContext.TradeOperations
            .SingleOrDefaultAsync(item => item.Id == entityId, cancellationToken);
        if (operation is null)
        {
            ActionErrorMessage = "Intake trade was not found.";
            return RedirectToCurrentPage();
        }

        if (string.IsNullOrWhiteSpace(operation.TradeOfferId))
        {
            ActionErrorMessage = "Intake trade does not have a Steam offer yet.";
            return RedirectToCurrentPage();
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Admin intake status refresh requested. TradeOperationId={operation.Id}; OfferId={operation.TradeOfferId}; CurrentStatus={operation.Status}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        var results = await _steamTradeClient.GetOfferStatusesAsync(
            new[]
            {
                new SteamTradeOfferStatusRequest
                {
                    OfferId = operation.TradeOfferId,
                    Flow = "intake"
                }
            },
            cancellationToken);
        var status = results.FirstOrDefault();
        if (status is null)
        {
            ActionErrorMessage = "Bot service did not return the intake offer status.";
            await _appLogService.WriteAsync(
                "Warning",
                $"Admin intake status refresh failed because no status was returned. TradeOperationId={operation.Id}; OfferId={operation.TradeOfferId}",
                nameof(BotStatusModel),
                cancellationToken: cancellationToken);
            return RedirectToCurrentPage();
        }

        var transitionLogs = new List<(string Level, string Message, string Source)>();
        var changed = SteamTradeSyncService.ApplyTradeOperationStatus(operation, status, transitionLogs);
        if (changed)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var entry in transitionLogs)
        {
            await _appLogService.WriteAsync(entry.Level, entry.Message, entry.Source, cancellationToken: cancellationToken);
        }

        if (operation.Status == "ReceivedByBot" && !operation.CreditedAtUtc.HasValue)
        {
            var creditResult = await _creditService.ConfirmReceivedAndCreditAsync(operation.Id, operation.AppUserId, cancellationToken);
            await _appLogService.WriteAsync(
                creditResult.Success ? "Info" : "Warning",
                $"Admin-triggered credit after intake refresh finished. TradeOperationId={operation.Id}; Success={creditResult.Success}; Status={creditResult.NewStatus}; OfferId={creditResult.TradeOfferId ?? "<null>"}; Message={creditResult.Message}",
                nameof(BotStatusModel),
                cancellationToken: cancellationToken);

            if (creditResult.Success)
            {
                SuccessMessage = creditResult.Message;
                return RedirectToCurrentPage();
            }
        }

        SuccessMessage = $"Steam intake status refreshed. SteamState={status.State}; AppStatus={operation.Status}.";
        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostCreditIntakeAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var operation = await _dbContext.TradeOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == entityId, cancellationToken);
        if (operation is null)
        {
            ActionErrorMessage = "Intake trade was not found.";
            return RedirectToCurrentPage();
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Admin credit requested. TradeOperationId={operation.Id}; CurrentStatus={operation.Status}; SellerAppUserId={operation.AppUserId}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        var result = await _creditService.ConfirmReceivedAndCreditAsync(operation.Id, operation.AppUserId, cancellationToken);
        await _appLogService.WriteAsync(
            result.Success ? "Info" : "Warning",
            $"Admin credit finished. TradeOperationId={operation.Id}; Success={result.Success}; Status={result.NewStatus}; OfferId={result.TradeOfferId ?? "<null>"}; Message={result.Message}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        if (result.Success)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ActionErrorMessage = result.Message;
        }

        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostCreateDeliveryAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var marketItem = await _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == entityId, cancellationToken);
        if (marketItem is null)
        {
            ActionErrorMessage = "Delivery trade was not found.";
            return RedirectToCurrentPage();
        }

        if (marketItem.BuyerAppUserId is null)
        {
            ActionErrorMessage = "Delivery trade does not have a buyer.";
            return RedirectToCurrentPage();
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Admin delivery creation requested. MarketPurchaseId={marketItem.Id}; DeliveryStatus={marketItem.DeliveryStatus ?? "<null>"}; BuyerAppUserId={marketItem.BuyerAppUserId}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        var result = await _marketDeliveryService.CreateDeliveryTradeAsync(marketItem.Id, marketItem.BuyerAppUserId.Value, cancellationToken);
        await _appLogService.WriteAsync(
            result.Success ? "Info" : "Warning",
            $"Admin delivery creation finished. MarketPurchaseId={marketItem.Id}; Success={result.Success}; Status={result.NewStatus}; OfferId={result.DeliveryTradeOfferId ?? "<null>"}; Message={result.Message}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        if (result.Success)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ActionErrorMessage = result.Message;
        }

        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostConfirmDeliveryAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var marketItem = await _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == entityId, cancellationToken);
        if (marketItem is null)
        {
            ActionErrorMessage = "Delivery trade was not found.";
            return RedirectToCurrentPage();
        }

        if (string.IsNullOrWhiteSpace(marketItem.DeliveryTradeOfferId))
        {
            ActionErrorMessage = "Delivery trade does not have a Steam offer yet.";
            return RedirectToCurrentPage();
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Admin bot confirmation retry requested for delivery. MarketPurchaseId={marketItem.Id}; OfferId={marketItem.DeliveryTradeOfferId}; DeliveryStatus={marketItem.DeliveryStatus ?? "<null>"}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        var result = await _steamTradeClient.ConfirmOfferAsync(marketItem.DeliveryTradeOfferId, "delivery", cancellationToken);
        await _appLogService.WriteAsync(
            result.Success ? "Info" : "Warning",
            $"Admin bot confirmation retry finished for delivery. MarketPurchaseId={marketItem.Id}; OfferId={result.OfferId}; Success={result.Success}; State={result.State ?? "<null>"}; Message={result.Message}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        if (result.Success)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ActionErrorMessage = result.Message;
        }

        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostRefreshDeliveryAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var marketItem = await _dbContext.MarketPurchaseRecords
            .SingleOrDefaultAsync(item => item.Id == entityId, cancellationToken);
        if (marketItem is null)
        {
            ActionErrorMessage = "Delivery trade was not found.";
            return RedirectToCurrentPage();
        }

        if (string.IsNullOrWhiteSpace(marketItem.DeliveryTradeOfferId))
        {
            ActionErrorMessage = "Delivery trade does not have a Steam offer yet.";
            return RedirectToCurrentPage();
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Admin delivery status refresh requested. MarketPurchaseId={marketItem.Id}; OfferId={marketItem.DeliveryTradeOfferId}; CurrentStatus={marketItem.DeliveryStatus ?? "<null>"}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        var results = await _steamTradeClient.GetOfferStatusesAsync(
            new[]
            {
                new SteamTradeOfferStatusRequest
                {
                    OfferId = marketItem.DeliveryTradeOfferId,
                    Flow = "delivery"
                }
            },
            cancellationToken);
        var status = results.FirstOrDefault();
        if (status is null)
        {
            ActionErrorMessage = "Bot service did not return the delivery offer status.";
            await _appLogService.WriteAsync(
                "Warning",
                $"Admin delivery status refresh failed because no status was returned. MarketPurchaseId={marketItem.Id}; OfferId={marketItem.DeliveryTradeOfferId}",
                nameof(BotStatusModel),
                cancellationToken: cancellationToken);
            return RedirectToCurrentPage();
        }

        var transitionLogs = new List<(string Level, string Message, string Source)>();
        var changed = SteamTradeSyncService.ApplyDeliveryStatus(marketItem, status, transitionLogs);
        if (changed)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var entry in transitionLogs)
        {
            await _appLogService.WriteAsync(entry.Level, entry.Message, entry.Source, cancellationToken: cancellationToken);
        }

        SuccessMessage = $"Steam delivery status refreshed. SteamState={status.State}; AppStatus={marketItem.DeliveryStatus ?? "<null>"}";
        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostCancelIntakeAsync(CancellationToken cancellationToken)
    {
        var operation = await _dbContext.TradeOperations
            .SingleOrDefaultAsync(item => item.Id == CancelInput.EntityId, cancellationToken);
        if (operation is null)
        {
            ActionErrorMessage = "Intake trade was not found.";
            return RedirectToCurrentPage();
        }

        if (string.IsNullOrWhiteSpace(operation.TradeOfferId) || !CanCancelIntakeStatus(operation.Status))
        {
            ActionErrorMessage = $"Intake trade cannot be canceled from status {operation.Status}.";
            return RedirectToCurrentPage();
        }

        var result = await _steamTradeClient.CancelOfferAsync(
            operation.TradeOfferId,
            "intake",
            $"Admin recovery for TradeOperationId={operation.Id}",
            cancellationToken);

        if (!result.Success)
        {
            ActionErrorMessage = result.Message;
            return RedirectToCurrentPage();
        }

        operation.Status = "Failed";
        operation.ErrorMessage = $"Trade offer was canceled by admin recovery. {result.Message}";
        operation.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _appLogService.WriteAsync(
            "Warning",
            $"Intake trade canceled by admin recovery. TradeOperationId={operation.Id}; OfferId={operation.TradeOfferId}; PreviousStatus={CancelInput.Status ?? "<unknown>"}; CancelState={result.State ?? "<null>"}; Message={result.Message}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        SuccessMessage = "Intake offer was canceled and the sale operation was released.";
        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostCancelDeliveryAsync(CancellationToken cancellationToken)
    {
        var item = await _dbContext.MarketPurchaseRecords
            .SingleOrDefaultAsync(record => record.Id == CancelInput.EntityId, cancellationToken);
        if (item is null)
        {
            ActionErrorMessage = "Delivery trade was not found.";
            return RedirectToCurrentPage();
        }

        if (string.IsNullOrWhiteSpace(item.DeliveryTradeOfferId) || !CanCancelDeliveryStatus(item.DeliveryStatus))
        {
            ActionErrorMessage = $"Delivery trade cannot be canceled from status {item.DeliveryStatus ?? "<null>"}.";
            return RedirectToCurrentPage();
        }

        var result = await _steamTradeClient.CancelOfferAsync(
            item.DeliveryTradeOfferId,
            "delivery",
            $"Admin recovery for MarketPurchaseId={item.Id}",
            cancellationToken);

        if (!result.Success)
        {
            ActionErrorMessage = result.Message;
            return RedirectToCurrentPage();
        }

        item.DeliveryStatus = "DeliveryFailed";
        item.DeliveryErrorMessage = $"Delivery trade offer was canceled by admin recovery. {result.Message}";
        item.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _appLogService.WriteAsync(
            "Warning",
            $"Delivery trade canceled by admin recovery. MarketPurchaseId={item.Id}; OfferId={item.DeliveryTradeOfferId}; PreviousStatus={CancelInput.Status ?? "<unknown>"}; CancelState={result.State ?? "<null>"}; Message={result.Message}",
            nameof(BotStatusModel),
            cancellationToken: cancellationToken);

        SuccessMessage = "Delivery offer was canceled and marked as failed.";
        return RedirectToCurrentPage();
    }

    private async Task LoadBotStatusAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_steamBotOptions.BotSteamId))
        {
            ErrorMessage = "SteamBot:BotSteamId is not configured.";
            return;
        }

        BotProfile = await _steamProfileService.GetProfileAsync(_steamBotOptions.BotSteamId, cancellationToken);

        var game = _gameCatalog.Get(_gameCatalog.DefaultGameType);
        var inventory = await LoadBotInventoryAsync(game, cancellationToken);
        if (!inventory.IsSuccess)
        {
            InventoryErrorMessage = inventory.ErrorMessage ?? "Bot inventory is unavailable.";
            return;
        }

        var soldAssets = await _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .Where(item => item.AppId == game.SteamAppId)
            .Select(item => new { item.AppId, item.ContextId, item.AssetId })
            .ToListAsync(cancellationToken);
        var soldAssetSet = new HashSet<string>(
            soldAssets.Select(item => BuildAssetKey(item.AppId, item.ContextId, item.AssetId)),
            StringComparer.Ordinal);
        var liveAssetIds = new HashSet<string>(inventory.Items.Select(item => item.AssetId), StringComparer.Ordinal);
        var protectedFallbackCount = await LoadProtectedFallbackCountAsync(game, liveAssetIds, soldAssetSet, cancellationToken);

        InventoryItemCount = inventory.Items.Count + protectedFallbackCount;
        AvailableMarketItemCount = inventory.Items.Count(item => !soldAssetSet.Contains(BuildAssetKey(game.SteamAppId, game.SteamContextId.ToString(), item.AssetId))) + protectedFallbackCount;
    }

    private async Task<SteamInventoryResultDto> LoadBotInventoryAsync(
        GameDefinition game,
        CancellationToken cancellationToken)
    {
        var liveInventory = await _steamBotInventoryClient.GetInventoryAsync(_steamBotOptions.BotSteamId, game.Type, cancellationToken);
        if (liveInventory.IsSuccess)
        {
            return liveInventory;
        }

        return await _steamInventoryService.GetInventoryAsync(_steamBotOptions.BotSteamId, game.Type, cancellationToken);
    }

    private async Task<int> LoadProtectedFallbackCountAsync(
        GameDefinition game,
        ISet<string> liveAssetIds,
        ISet<string> soldAssetSet,
        CancellationToken cancellationToken)
    {
        var thresholdUtc = DateTime.UtcNow - ProtectedInventoryFallbackWindow;
        var operations = await _dbContext.TradeOperations
            .AsNoTracking()
            .Where(operation =>
                operation.AppId == game.SteamAppId &&
                operation.BotAssetId != null &&
                (operation.Status == "ReceivedByBot" || operation.Status == "Credited") &&
                (operation.CreditedAtUtc ?? operation.ReceivedByBotAtUtc ?? operation.UpdatedAtUtc) >= thresholdUtc)
            .OrderByDescending(operation => operation.CreditedAtUtc ?? operation.ReceivedByBotAtUtc ?? operation.UpdatedAtUtc)
            .ToListAsync(cancellationToken);

        return operations
            .GroupBy(operation => operation.BotAssetId!, StringComparer.Ordinal)
            .Select(group => group.First())
            .Count(operation =>
                !liveAssetIds.Contains(operation.BotAssetId!) &&
                !soldAssetSet.Contains(BuildAssetKey(operation.AppId, operation.ContextId, operation.BotAssetId!)));
    }

    private static string BuildAssetKey(int appId, string contextId, string assetId)
    {
        return $"{appId}:{contextId}:{assetId}";
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

    private async Task LoadHistoryAsync(int take, CancellationToken cancellationToken)
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

    public bool CanCancelIntake(BotPurchaseHistoryItem item)
    {
        return !string.IsNullOrWhiteSpace(item.TradeOfferId) && CanCancelIntakeStatus(item.Status);
    }

    public bool CanCreateIntake(BotPurchaseHistoryItem item)
    {
        return item.Status is "Pending" or "Failed";
    }

    public bool CanConfirmIntake(BotPurchaseHistoryItem item)
    {
        return item.Status == "AwaitingBotConfirmation" && !string.IsNullOrWhiteSpace(item.TradeOfferId);
    }

    public bool CanRefreshIntake(BotPurchaseHistoryItem item)
    {
        return !string.IsNullOrWhiteSpace(item.TradeOfferId) &&
               item.Status is "BotPending" or "AwaitingBotConfirmation" or "TradeCreated" or "AwaitingUserAction" or "TradeAcceptedPendingReceipt" or "InEscrow";
    }

    public bool CanCreditIntake(BotPurchaseHistoryItem item)
    {
        return item.Status == "ReceivedByBot" && !item.CreditedAtUtc.HasValue;
    }

    public bool CanOpenSellerOffer(BotPurchaseHistoryItem item)
    {
        return !string.IsNullOrWhiteSpace(item.TradeOfferId);
    }

    public bool CanCancelDelivery(BotSaleHistoryItem item)
    {
        return !string.IsNullOrWhiteSpace(item.DeliveryTradeOfferId) && CanCancelDeliveryStatus(item.DeliveryStatus);
    }

    public bool CanCreateDelivery(BotSaleHistoryItem item)
    {
        return item.DeliveryStatus is "PendingDelivery" or "DeliveryFailed";
    }

    public bool CanConfirmDelivery(BotSaleHistoryItem item)
    {
        return item.DeliveryStatus == "AwaitingBotConfirmation" && !string.IsNullOrWhiteSpace(item.DeliveryTradeOfferId);
    }

    public bool CanRefreshDelivery(BotSaleHistoryItem item)
    {
        return !string.IsNullOrWhiteSpace(item.DeliveryTradeOfferId) &&
               item.DeliveryStatus is "DeliveryBotPending" or "AwaitingBotConfirmation" or "DeliveryTradeCreated" or "AwaitingBuyerAction" or "DeliveryInEscrow";
    }

    public bool CanOpenBuyerOffer(BotSaleHistoryItem item)
    {
        return !string.IsNullOrWhiteSpace(item.DeliveryTradeOfferId);
    }

    private RedirectToPageResult RedirectToCurrentPage()
    {
        var normalizedMode = NormalizeHistoryMode(HistoryMode);
        return RedirectToPage(new
        {
            Limit = NormalizeLimit(Limit),
            SearchTerm = NormalizeSearchTerm(SearchTerm),
            HistoryMode = normalizedMode == "all" ? null : normalizedMode
        });
    }

    private static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            return DefaultHistoryLimit;
        }

        return Math.Min(limit, 500);
    }

    private static string? NormalizeSearchTerm(string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return null;
        }

        return searchTerm.Trim();
    }

    private static string NormalizeHistoryMode(string? historyMode)
    {
        return historyMode?.Trim().ToLowerInvariant() switch
        {
            "actionable" => "actionable",
            "failed" => "failed",
            "completed" => "completed",
            _ => "all"
        };
    }

    private static bool CanCancelIntakeStatus(string? status)
    {
        return status is "AwaitingBotConfirmation" or "TradeCreated" or "AwaitingUserAction";
    }

    private static bool CanCancelDeliveryStatus(string? status)
    {
        return status is "AwaitingBotConfirmation" or "DeliveryTradeCreated" or "AwaitingBuyerAction";
    }

    public class CancelTradeInputModel
    {
        public Guid EntityId { get; set; }
        public string? Status { get; set; }
    }
}
